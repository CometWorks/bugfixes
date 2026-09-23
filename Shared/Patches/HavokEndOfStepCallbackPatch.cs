using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Havok;
using Shared.Plugin;
using VRage.Utils;

namespace Shared.Patches;

// Client + Server
//
// Keeps Havok's end-of-step contact callback list free of freed contact managers when a
// contact listener is detached from a body that is still colliding. See
// Docs/havok-end-of-step-callbacks.md for the full story; the short version:
//
// Keen's native Havok::HkContactListener registers every collision it is told about with
// the world's hkpEndOfStepCallbackUtil (collisionAddedCallback) and unregisters it only
// when Havok delivers collisionRemovedCallback. Havok delivers that to the listeners
// attached to the entity at that moment. Detach the listener while it touches something
// (ContactPointCallbackEnabled = false, or HkEntity.Dispose before the body left the
// world), let that contact end before the next step (the other body removed, agents
// rebuilt), and FinishMtStep calls a virtual through a freed contact manager. Doors turn
// their panels' callbacks off while moving and missiles are removed every frame, so
// combat near doors hits it; the crash is inside Havok.dll on every platform.
//
// The prefixes below run before the native detach and drop the listener's live
// registrations from the util's lists in place. Nothing walks those lists between steps,
// so a detach on the main thread outside a step edits them directly. A detach from inside
// a step or from another thread (the finalizer) is queued and applied on the main thread
// around that world's next step instead, before the list is fired. The listener's own
// records are left alone so the game still receives its CollisionRemoved event.
//
// Layout facts (SE1 Havok.dll, 64-bit) come from the crash core and the disassembly, and
// are verified at run time through MSVC RTTI before anything is written: the fix disables
// itself with one warning when the classes behind the pointers are not the expected ones.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class HavokEndOfStepCallbackPatch
{
    // hkpWorldObject
    private const int EntityWorldFallbackOffset = 0x10;

    // hkpWorld
    private const int WorldExtensionsData = 0x268;
    private const int WorldExtensionsSize = 0x270;

    // hkpWorldExtension (hkpCollisionCallbackUtil embeds the end-of-step util)
    private const int ExtensionId = 0x18;
    private const int ExtensionUtil = 0x28;
    private const int EndOfStepExtensionId = 1001;

    // hkpEndOfStepCallbackUtil: hkArray is { T* data; int size; int capacityAndFlags; }
    private const int UtilPostSimulationVtable = 0x10;
    private const int UtilCollisions = 0x20; // 24-byte entries: mgr, listener, source, seq
    private const int UtilNewCollisions = 0x30; // 32-byte entries: mgr, listener, source, seq
    private const int CollisionEntrySize = 24;
    private const int NewCollisionEntrySize = 32;

    // Havok::HkContactListener: hkArray of hkpCollisionEvent copies { source, bodyA, bodyB, mgr }
    private const int ListenerRecordsData = 0x20;
    private const int ListenerRecordsSize = 0x28;
    private const int RecordSize = 32;
    private const int RecordSource = 0x0;
    private const int RecordManager = 0x18;

    private const string ListenerClass = ".?AVHkContactListener@Havok@@";
    private const string ExtensionClass = ".?AVhkpCollisionCallbackUtil@@";
    private const string UtilClass = ".?AVhkpEndOfStepCallbackUtil@@";

    private struct Registration
    {
        public IntPtr Manager;
        public int Source;
    }

    private sealed class Pending
    {
        public IntPtr World;
        public IntPtr Listener;
        public Registration[] Records;
    }

    private static readonly object Sync = new();
    private static readonly List<Pending> Queue = new();

    [ThreadStatic]
    private static int stepDepth;

    private static int state; // 0 unverified, 1 verified, -1 disabled
    private static IntPtr listenerVtable;
    private static IntPtr extensionVtable;
    private static IntPtr utilVtable;
    private static int worldOffset = -1;

    public static long Detaches;
    public static long Dropped;
    public static long Deferred;

    private static bool Enabled => Common.Config?.Enabled != false && state >= 0;

    // ---- Harmony targets ----

    [HarmonyPatch(
        typeof(HkEntity),
        nameof(HkEntity.ContactPointCallbackEnabled),
        MethodType.Setter
    )]
    [HarmonyPrefix]
    private static void BeforeContactPointCallbackEnabledSet(
        HkEntity __instance,
        bool value,
        bool ___m_contactListenerEnabled,
        HkContactListener ___m_contactListener
    )
    {
        if (value || !___m_contactListenerEnabled)
            return;

        OnDetach(__instance, ___m_contactListener);
    }

    [HarmonyPatch(typeof(HkEntity), "Dispose", typeof(bool))]
    [HarmonyPrefix]
    private static void BeforeEntityDispose(
        HkEntity __instance,
        bool ___m_contactListenerEnabled,
        HkContactListener ___m_contactListener
    )
    {
        if (!___m_contactListenerEnabled)
            return;

        OnDetach(__instance, ___m_contactListener);
    }

    [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.FinishMtStep))]
    [HarmonyPrefix]
    private static void BeforeFinishMtStep(HkWorld __instance) => EnterStep(__instance);

    [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.FinishMtStep))]
    [HarmonyPostfix]
    private static void AfterFinishMtStep(HkWorld __instance) => LeaveStep(__instance);

    [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.StepSimulation))]
    [HarmonyPrefix]
    private static void BeforeStepSimulation(HkWorld __instance) => EnterStep(__instance);

    [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.StepSimulation))]
    [HarmonyPostfix]
    private static void AfterStepSimulation(HkWorld __instance) => LeaveStep(__instance);

    [HarmonyPatch(typeof(HkWorld), "Dispose", typeof(bool))]
    [HarmonyPrefix]
    private static void BeforeWorldDispose(HkWorld __instance)
    {
        if (__instance.IsDisposed)
            return;

        var world = __instance.NativeObject;
        lock (Sync)
            Queue.RemoveAll(p => p.World == world);
    }

    // ---- Step bracketing ----

    private static void EnterStep(HkWorld world)
    {
        stepDepth++;
        if (!world.IsDisposed)
            Flush(world.NativeObject);
    }

    private static void LeaveStep(HkWorld world)
    {
        stepDepth--;
        if (!world.IsDisposed)
            Flush(world.NativeObject);
    }

    // ---- Detach handling ----

    private static void OnDetach(HkEntity entity, HkContactListener listener)
    {
        if (
            !Enabled
            || entity == null
            || listener == null
            || entity.IsDisposed
            || listener.IsDisposed
        )
            return;

        try
        {
            var body = entity.NativeObject;
            var native = listener.NativeObject;
            if (!VerifyListener(native))
                return;

            var records = ReadRecords(native);
            if (records == null)
                return;

            var world = ReadWorld(body);
            if (world == IntPtr.Zero)
                return; // out of the world: no agents, nothing can be registered

            var onMainThread =
                MyUtils.MainThread == null || Thread.CurrentThread == MyUtils.MainThread;
            if (onMainThread && stepDepth == 0)
            {
                Purge(world, native, records);
                return;
            }

            lock (Sync)
                Queue.Add(
                    new Pending
                    {
                        World = world,
                        Listener = native,
                        Records = records,
                    }
                );
            Interlocked.Increment(ref Deferred);
        }
        catch (Exception e)
        {
            Disable($"unexpected error: {e.Message}");
        }
    }

    private static void Flush(IntPtr world)
    {
        List<Pending> due = null;
        lock (Sync)
        {
            for (var i = Queue.Count - 1; i >= 0; i--)
            {
                if (Queue[i].World != world)
                    continue;

                due ??= new List<Pending>();
                due.Add(Queue[i]);
                Queue.RemoveAt(i);
            }
        }

        if (due == null || !Enabled)
            return;

        try
        {
            foreach (var pending in due)
                Purge(pending.World, pending.Listener, pending.Records);
        }
        catch (Exception e)
        {
            Disable($"unexpected error: {e.Message}");
        }
    }

    private static unsafe Registration[] ReadRecords(IntPtr listener)
    {
        var count = *(int*)(listener + ListenerRecordsSize);
        var data = *(byte**)(listener + ListenerRecordsData);
        if (count <= 0 || data == null)
            return null;

        var records = new Registration[count];
        for (var i = 0; i < count; i++)
        {
            var record = data + i * RecordSize;
            records[i].Manager = *(IntPtr*)(record + RecordManager);
            records[i].Source = *(int*)(record + RecordSource);
        }

        return records;
    }

    private static unsafe IntPtr ReadWorld(IntPtr body)
    {
        if (worldOffset < 0)
        {
            // HkEntity asks Havok for its field offsets in its static constructor
            var field = AccessTools.Field(typeof(HkEntity), "WorldOffset");
            worldOffset = field?.GetValue(null) as int? ?? EntityWorldFallbackOffset;
        }

        return *(IntPtr*)(body + worldOffset);
    }

    private static unsafe void Purge(IntPtr world, IntPtr listener, Registration[] records)
    {
        var util = FindUtil(world);
        if (util == IntPtr.Zero)
            return;

        long dropped = 0;
        foreach (var record in records)
        {
            dropped += RemoveEntries(util + UtilCollisions, CollisionEntrySize, record, listener);
            dropped += RemoveEntries(
                util + UtilNewCollisions,
                NewCollisionEntrySize,
                record,
                listener
            );
        }

        Interlocked.Increment(ref Detaches);
        if (dropped == 0)
            return;

        Interlocked.Add(ref Dropped, dropped);
        Common.Logger.Debug(
            "Havok end-of-step callbacks: dropped {0} registration(s) of detached listener {1:X} (total {2})",
            dropped,
            listener.ToInt64(),
            Dropped
        );
    }

    // Removes every { mgr, listener, source } match from an hkArray of entries, keeping the order.
    private static unsafe long RemoveEntries(
        IntPtr array,
        int entrySize,
        Registration record,
        IntPtr listener
    )
    {
        var data = *(byte**)array;
        var count = *(int*)(array + 8);
        if (data == null || count <= 0)
            return 0;

        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = data + i * entrySize;
            var matches =
                *(IntPtr*)entry == record.Manager
                && *(IntPtr*)(entry + 8) == listener
                && *(int*)(entry + 16) == record.Source;
            if (matches)
                continue;

            if (kept != i)
                Buffer.MemoryCopy(entry, data + kept * entrySize, entrySize, entrySize);
            kept++;
        }

        *(int*)(array + 8) = kept;
        return count - kept;
    }

    private static unsafe IntPtr FindUtil(IntPtr world)
    {
        var extensions = *(IntPtr**)(world + WorldExtensionsData);
        var count = *(int*)(world + WorldExtensionsSize);
        for (var i = 0; i < count; i++)
        {
            var extension = extensions[i];
            if (
                extension == IntPtr.Zero
                || *(int*)(extension + ExtensionId) != EndOfStepExtensionId
            )
                continue;

            var util = extension + ExtensionUtil;
            return VerifyUtil(extension, util) ? util : IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ---- Layout verification through MSVC RTTI ----

    private static unsafe bool VerifyListener(IntPtr listener)
    {
        var vtable = *(IntPtr*)listener;
        if (state < 0)
            return false;
        if (vtable == listenerVtable)
            return true;
        if (RttiName(vtable) != ListenerClass)
            return Fail("the contact listener is not Havok::HkContactListener");

        listenerVtable = vtable;
        return true;
    }

    private static unsafe bool VerifyUtil(IntPtr extension, IntPtr util)
    {
        var extVtable = *(IntPtr*)extension;
        var utilVt = *(IntPtr*)(util + UtilPostSimulationVtable);
        if (state > 0)
            return extVtable == extensionVtable && utilVt == utilVtable
                || Fail("the end-of-step util changed class");

        if (RttiName(extVtable) != ExtensionClass)
            return Fail("world extension 1001 is not hkpCollisionCallbackUtil");
        if (RttiName(utilVt) != UtilClass)
            return Fail("the embedded util is not hkpEndOfStepCallbackUtil");

        extensionVtable = extVtable;
        utilVtable = utilVt;
        state = 1;
        Common.Logger.Info("Havok end-of-step callback fix active");
        return true;
    }

    // Resolves the MSVC RTTI class name behind a 64-bit vtable: the complete object
    // locator sits one slot before the vtable and holds the image-relative addresses of
    // the type descriptor and of itself, which gives the image base without asking the OS.
    private static unsafe string RttiName(IntPtr vtable)
    {
        if (vtable == IntPtr.Zero)
            return null;

        var locator = *(IntPtr*)(vtable - 8);
        if (locator == IntPtr.Zero || *(int*)locator != 1)
            return null;

        var descriptorRva = *(uint*)(locator + 0xc);
        var selfRva = *(uint*)(locator + 0x14);
        var imageBase = locator - (int)selfRva;
        if (*(ushort*)imageBase != 0x5a4d) // "MZ"
            return null;

        var name = (byte*)(imageBase + (int)descriptorRva + 0x10);
        var length = 0;
        while (length < 64 && name[length] != 0)
            length++;
        return length == 0 || length == 64 ? null : Marshal.PtrToStringAnsi((IntPtr)name, length);
    }

    private static bool Fail(string why)
    {
        Disable(why);
        return false;
    }

    private static void Disable(string why)
    {
        if (state < 0)
            return;

        state = -1;
        lock (Sync)
            Queue.Clear();
        Common.Logger.Warning("Havok end-of-step callback fix disabled: {0}", why);
    }
}
