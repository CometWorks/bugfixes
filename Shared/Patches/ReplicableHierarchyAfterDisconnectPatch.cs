using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Shared.Plugin;
using VRage.Network;

namespace Shared.Patches;

// Client + Server
//
// Keeps the world unload from failing when the multiplayer session was disposed before it. See
// Docs/replicable-hierarchy-after-disconnect.md.
//
// A client the server refuses with a join result (kicked, or booted by a cluster Gateway) runs
// MyMultiplayerClient.OnUserJoined, which disposes the multiplayer session first and only then
// calls MySessionLoader.UnloadAndExitToMenu. With MyMultiplayer.Static gone, the
// MyMultiplayer.ReplicationLayer getter returns the offline MyReplicationSingle. Four replicables
// refresh their hierarchy with `(MyMultiplayer.ReplicationLayer as MyReplicationLayer)`, which is
// null then. A pilot leaves the cockpit while MyEntities.CloseAll deletes the grid, and
// MyCharacterReplicable.OnParentChanged throws a NullReferenceException. MySession.Unload stops
// halfway: the session components after MySector, the mods among them, never unload. The next
// join then fails on half-unloaded definitions, or a mod's message handler left registered from
// the old world crashes the client (SE-1824).
//
// The transpiler replaces each RefreshReplicableHierarchy call with one that skips a null layer.
// Without a multiplayer session there is no replication hierarchy to refresh.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ReplicableHierarchyAfterDisconnectPatch
{
    private static readonly MethodInfo Refresh = AccessTools.Method(
        typeof(MyReplicationLayer),
        nameof(MyReplicationLayer.RefreshReplicableHierarchy)
    );

    // The replicables are internal in Sandbox.Game, which the client build doesn't publicize
    private static IEnumerable<MethodBase> TargetMethods() =>
        [
            AccessTools.Method("Sandbox.Game.Replication.MyCharacterReplicable:OnParentChanged"),
            AccessTools.Method(
                "Sandbox.Game.Replication.MyTerminalReplicable:OnBlockCubeGridChanged"
            ),
            AccessTools.Method(
                "Sandbox.Game.Replication.MyInventoryReplicable:OnBlockCubeGridChanged"
            ),
            AccessTools.Method(
                "Sandbox.Game.Replication.MyInventoryBaseReplicable:OnBlockCubeGridChanged"
            ),
        ];

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod
    )
    {
        var patched = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Refresh))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(
                    typeof(ReplicableHierarchyAfterDisconnectPatch),
                    nameof(RefreshIfOnline)
                );
                patched++;
            }
            yield return instruction;
        }

        if (patched == 0)
            Common.Logger.Warning(
                "ReplicableHierarchyAfterDisconnectPatch: no RefreshReplicableHierarchy call in {0}.{1}, not patched",
                __originalMethod.DeclaringType?.Name,
                __originalMethod.Name
            );
    }

    // Same stack as the replaced callvirt: the layer, then the replicable
    private static void RefreshIfOnline(MyReplicationLayer layer, IMyReplicable replicable)
    {
        if (layer != null || Common.Config?.Enabled == false)
            layer!.RefreshReplicableHierarchy(replicable);
    }
}
