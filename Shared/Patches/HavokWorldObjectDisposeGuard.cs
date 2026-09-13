using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Havok;
using Shared.Logging;
using Shared.Plugin;

namespace Shared.Patches;

// Fixes a lifetime bug in the game's own Havok wrapper.
//
// HkHandle releases its native object both from Dispose() and from its finalizer, and
// HkWorld keeps no managed reference to the phantoms added to it. A rigid body or phantom
// whose managed wrapper is dropped without a RemoveRigidBody/RemovePhantom is therefore
// released while the broadphase still holds it, and the next broadphase pass walks freed
// memory. Nothing about this is platform specific: it is managed lifetime handling, so it
// is present on Windows as well, and the dedicated server steps the same worlds through
// the same wrapper.
//
// The fix:
// - An explicit Dispose() of a world object removes it from its world first, then lets the
//   release proceed.
// - The finalizer of a world object skips the native release entirely. A leak is far
//   cheaper than a freed body in the broadphase, and the finalizer thread must not touch
//   the world.
// - AddPhantom/RemovePhantom keep a native pointer -> world map, because phantoms have no
//   InWorld of their own; entities expose one through their native world pointer.
//
// The patches are applied here rather than by PatchHelpers.HarmonyPatchAll, one by one and
// with explicit argument lists on every target: HkHandle has both Dispose() and
// Dispose(bool), and an ambiguous match aborts the whole PatchAll, which would take every
// other fix in this plugin down with it.
//
// ReSharper disable once UnusedType.Global
internal static class HavokWorldObjectDisposeGuard
{
    // Detailed log lines per kind of event, before only the periodic summary remains
    private const int ReportedCases = 5;

    private static readonly long SummaryIntervalTicks = 60L * Stopwatch.Frequency;

    // Phantoms are not entities, so they have no native world pointer to read back
    private static readonly ConcurrentDictionary<IntPtr, HkWorld> PhantomWorlds = new();

    // HkEntity.GetWorld is protected and this plugin does not publicize the game assemblies
    private static readonly MethodInfo GetWorldMethod = AccessTools.Method(typeof(HkEntity), "GetWorld", new[] { typeof(IntPtr) });

    private static long bodiesRemoved, bodiesLeaked, phantomsRemoved, phantomsLeaked;
    private static long lastSummaryTimestamp, lastSummaryTotal;
    private static int failuresReported;

    public static void Apply(Harmony harmony, IPluginLogger log)
    {
        if (GetWorldMethod == null)
        {
            log.Error("Havok dispose guard: HkEntity.GetWorld(IntPtr) is missing, the guard stays inactive");
            return;
        }

        var applied = 0;
        applied += Patch(harmony, log, "HkWorld.AddPhantom",
            AccessTools.Method(typeof(HkWorld), nameof(HkWorld.AddPhantom), new[] { typeof(HkPhantom) }),
            postfix: nameof(AddPhantomPostfix));
        applied += Patch(harmony, log, "HkWorld.RemovePhantom",
            AccessTools.Method(typeof(HkWorld), nameof(HkWorld.RemovePhantom), new[] { typeof(HkPhantom) }),
            postfix: nameof(RemovePhantomPostfix));
        applied += Patch(harmony, log, "HkHandle.Dispose",
            AccessTools.Method(typeof(HkHandle), nameof(HkHandle.Dispose), Type.EmptyTypes),
            prefix: nameof(DisposePrefix));
        applied += Patch(harmony, log, "HkHandle.Finalize",
            AccessTools.Method(typeof(HkHandle), "Finalize", Type.EmptyTypes),
            prefix: nameof(FinalizePrefix));

        if (applied == 4)
            log.Debug("Havok dispose guard: armed");
        else
            log.Error($"Havok dispose guard: only {applied} of 4 patches applied, world objects are not fully guarded");
    }

    private static int Patch(Harmony harmony, IPluginLogger log, string name, MethodBase target, string prefix = null, string postfix = null)
    {
        try
        {
            if (target == null)
                throw new MissingMethodException($"{name} not found");

            harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(HavokWorldObjectDisposeGuard), prefix)),
                postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(HavokWorldObjectDisposeGuard), postfix)));

            return 1;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Havok dispose guard: failed to patch {name}");
            return 0;
        }
    }

    // ReSharper disable once InconsistentNaming
    private static void AddPhantomPostfix(HkWorld __instance, HkPhantom __0)
    {
        if (__0 != null && !__0.IsDisposed)
            PhantomWorlds[__0.NativeObject] = __instance;
    }

    // ReSharper disable once InconsistentNaming
    private static void RemovePhantomPostfix(HkPhantom __0)
    {
        if (__0 != null && !__0.IsDisposed)
            PhantomWorlds.TryRemove(__0.NativeObject, out _);
    }

    // Returning false skips Dispose(), which leaves the object allocated and its handle
    // registered, so the world keeps a valid body instead of a dangling one.
    // ReSharper disable once InconsistentNaming
    private static bool DisposePrefix(HkHandle __instance) => !Guard(__instance, finalizer: false);

    // Returning false skips the wrapper's finalizer, therefore the native release.
    // ReSharper disable once InconsistentNaming
    private static bool FinalizePrefix(HkHandle __instance) => !Guard(__instance, finalizer: true);

    // Returns true when the native release must be skipped, leaking the object instead.
    private static bool Guard(HkHandle handle, bool finalizer)
    {
        try
        {
            if (handle == null || handle.IsDisposed || Common.Config?.Enabled != true)
                return false;

            switch (handle)
            {
                case HkRigidBody body:
                    return GuardRigidBody(body, finalizer);

                case HkPhantom phantom:
                    return GuardPhantom(phantom, finalizer);

                // Phantoms are not removed from a world being torn down, so their entries
                // would outlive it and match whatever allocation reuses the pointer.
                case HkWorld world:
                    ForgetPhantomsOf(world);
                    return false;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref failuresReported) <= ReportedCases)
                Common.Logger.Error(ex, "Havok dispose guard failed");

            return false;
        }
    }

    private static bool GuardRigidBody(HkRigidBody body, bool finalizer)
    {
        // Cheap native field read, keeping the reflected call below off the common path
        if (!body.InWorldInternal)
            return false;

        if (finalizer)
        {
            Count(ref bodiesLeaked, "rigid body finalized while still in a world, native release skipped", body);
            return true;
        }

        var worldPtr = (IntPtr)GetWorldMethod.Invoke(null, new object[] { body.NativeObject });
        if (worldPtr != IntPtr.Zero && HkHandle.TryGetHandle<HkWorld>(worldPtr, out var world) && !world.IsDisposed)
        {
            world.RemoveRigidBody(body);
            Count(ref bodiesRemoved, "rigid body disposed while still in a world, removed from the world first", body);
            return false;
        }

        Count(ref bodiesLeaked, "rigid body disposed while in a world with no managed wrapper, native release skipped", body);
        return true;
    }

    private static bool GuardPhantom(HkPhantom phantom, bool finalizer)
    {
        if (!PhantomWorlds.TryRemove(phantom.NativeObject, out var world))
            return false;

        if (finalizer)
        {
            Count(ref phantomsLeaked, "phantom finalized while still in a world, native release skipped", phantom);
            return true;
        }

        if (world.IsDisposed)
        {
            Count(ref phantomsLeaked, "phantom disposed while in a world already gone, native release skipped", phantom);
            return true;
        }

        world.RemovePhantom(phantom);
        Count(ref phantomsRemoved, "phantom disposed while still in a world, removed from the world first", phantom);
        return false;
    }

    private static void ForgetPhantomsOf(HkWorld world)
    {
        foreach (var pair in PhantomWorlds)
        {
            if (pair.Value == world)
                PhantomWorlds.TryRemove(pair.Key, out _);
        }
    }

    private static void Count(ref long counter, string what, HkHandle handle)
    {
        if (Interlocked.Increment(ref counter) <= ReportedCases)
            Common.Logger.Warning($"Havok dispose guard: {what}: {handle.GetType().Name} 0x{handle.NativeObject.ToInt64():x}");

        Summarise();
    }

    // Logs a rollup at most once a minute, and only while the counts keep moving
    private static void Summarise()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref lastSummaryTimestamp);

        if (last == 0)
        {
            Interlocked.CompareExchange(ref lastSummaryTimestamp, now, 0);
            return;
        }

        if (now - last < SummaryIntervalTicks || Interlocked.CompareExchange(ref lastSummaryTimestamp, now, last) != last)
            return;

        var removedBodies = Interlocked.Read(ref bodiesRemoved);
        var leakedBodies = Interlocked.Read(ref bodiesLeaked);
        var removedPhantoms = Interlocked.Read(ref phantomsRemoved);
        var leakedPhantoms = Interlocked.Read(ref phantomsLeaked);

        var total = removedBodies + leakedBodies + removedPhantoms + leakedPhantoms;
        if (Interlocked.Exchange(ref lastSummaryTotal, total) == total)
            return;

        Common.Logger.Info(
            $"Havok dispose guard: rigid bodies removed from their world before release={removedBodies} leaked={leakedBodies}, " +
            $"phantoms removed from their world before release={removedPhantoms} leaked={leakedPhantoms}");
    }
}
