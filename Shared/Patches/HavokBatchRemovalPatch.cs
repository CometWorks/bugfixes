using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Shared.Plugin;

namespace Shared.Patches;

// Client + Server
//
// Keeps Havok from crashing on a batched removal of a rigid body that is not in the world.
// See Docs/havok-batch-removal.md.
//
// When the physics clusters are reordered, MyPhysicsBody.DeactivateBatch puts the bodies of the
// moved entities into the remove batch of their HkWorld, and HkWorld.FinishBatch hands the batch
// to hkpWorld::removeEntityBatch. Unlike HkWorld.RemoveRigidBody, which checks that the body is
// in this world, the batch is passed on as it is. A body that is listed twice, or that left the
// world between DeactivateBatch and FinishBatch, has no simulation island anymore, and Havok
// reads the entity list of a null island: an access violation inside Havok.dll (at +0x5b8b70 in
// game version 1.208), with no managed frames in the crash dump.
//
// The prefix on FinishBatch compacts the remove batch in place before the native call: it keeps
// each body once, and only if Havok has it in this world. Such a body needs no removal, so the
// game goes on as if the batch had been right. Every dropped entry is logged with its entity.
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class HavokBatchRemovalPatch
{
    private const int LoggedDrops = 20;
    private const int LoggedStackTraces = 3;

    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, HkRigidBody> Batched = new(); // for the log only
    private static readonly HashSet<IntPtr> Seen = new();

    public static long Dropped;

    private static bool Enabled => Common.Config?.Enabled != false;

    [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.RemoveRigidBodyBatch))]
    [HarmonyPrefix]
    private static void BeforeRemoveRigidBodyBatch(HkRigidBody rigidBody)
    {
        if (!Enabled || rigidBody == null)
            return;

        lock (Sync)
            Batched[rigidBody.NativeObject] = rigidBody;
    }

    [HarmonyPatch(typeof(HkWorld), nameof(HkWorld.FinishBatch))]
    [HarmonyPrefix]
    private static unsafe void BeforeFinishBatch(
        HkWorld __instance,
        IntPtr ___m_removeBatch,
        ref int ___m_removeBatchCount
    )
    {
        if (!Enabled || ___m_removeBatchCount <= 0 || ___m_removeBatch == IntPtr.Zero)
            return;

        var world = __instance.NativeObject;
        var bodies = (IntPtr*)___m_removeBatch;
        lock (Sync)
        {
            var kept = 0;
            for (var i = 0; i < ___m_removeBatchCount; i++)
            {
                var body = bodies[i];
                var reason =
                    body == IntPtr.Zero ? "is disposed"
                    : !Seen.Add(body) ? "is listed twice"
                    : HavokEndOfStepCallbackPatch.ReadWorld(body) != world ? "is not in this world"
                    : null;
                if (reason == null)
                    bodies[kept++] = body;
                else
                    Report(body, reason);
            }

            ___m_removeBatchCount = kept;
            Seen.Clear();
            Batched.Clear();
        }
    }

    private static void Report(IntPtr body, string reason)
    {
        var total = Interlocked.Increment(ref Dropped);
        if (total > LoggedDrops)
            return;

        Common.Logger.Warning(
            "Havok batch removal: skipped the body {0:X} of {1}, it {2} (total {3}){4}",
            body.ToInt64(),
            Describe(body),
            reason,
            total,
            total <= LoggedStackTraces ? Environment.NewLine + Environment.StackTrace : ""
        );
    }

    private static string Describe(IntPtr body)
    {
        try
        {
            if (
                Batched.TryGetValue(body, out var rigidBody)
                && rigidBody.UserObject is MyPhysicsBody { Entity: { } entity }
            )
                return $"{entity.GetType().Name} '{entity.DisplayName}' ({entity.EntityId})";
        }
        catch (Exception)
        {
            // A disposed body: the log line goes without its entity
        }

        return "an unknown entity";
    }
}
