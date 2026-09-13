using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.GUI;
using Sandbox.Game.World;
using Shared.Plugin;

namespace ClientPlugin.Patches;

// Fixes a missing null check in the HUD's "energy time remaining" stat.
//
// MyStatControlledEntityEnergyEstimatedTimeRemaining.Update walks
// MySession.Static.ControlledGrid.GridSystems.ResourceDistributor with only ControlledGrid
// null-checked, and the NullReferenceException lands on the update thread, taking the
// client down.
//
// ResourceDistributor is shared by a logical group, not owned by the grid: MyCubeGridSystems
// never assigns it in its constructor, OnAddedToGroup takes it from the group and
// OnRemovedFromGroup sets it back to null. GridSystems itself is assigned once and never
// nulled. "GridSystems non-null, ResourceDistributor null" is therefore the designed shape
// of a grid that is outside a logical group, not a startup artifact: the window opens at
// OnRemovedFromScene -> RemoveNode(Logical) -> OnRemovedFromGroup and closes at
// OnAddedToScene -> AddNode -> AcquireGroup -> OnAddedToGroup. MySession.ControlledGrid is
// computed live on every call, so it keeps returning the grid for the whole window.
//
// Present on Windows. The driver is MyHud.UpdateBeforeSimulation -> m_Stats.Update(), gated
// only on !Sync.IsDedicated — a simulation-phase session-component update, not a draw call,
// so it is gated on neither rendering nor HUD visibility, and MyHud carries no platform
// branch. Vanilla guards this same null in three other places, one of them
// MyStatControlledEntityPowerUsage.Update, which reads the same field off the same
// controlled entity in the same stats sweep and zeroes its value when it is missing. This
// stat is the only one that omits the check, which makes it a vanilla inconsistency rather
// than a platform or loader artifact. How often a player hits the window is another
// question: taking control programmatically, right after replication, reaches it far more
// often than walking to a cockpit does. Closing the controlled grid does not: the game
// clears the controller before the systems lose their distributor, which was checked by
// instrumenting this prefix and closing a grid from under a seated character.
//
// The prefix skips the stat for that frame, so the previous value stands until the
// distributor appears; the game recomputes it on the next update.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyStatControlledEntityEnergyEstimatedTimeRemaining),
    nameof(MyStatControlledEntityEnergyEstimatedTimeRemaining.Update))]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class HudEnergyTimeRemainingStatPatch
{
    private static bool Prefix()
    {
        if (Common.Config?.Enabled != true)
            return true;

        var grid = MySession.Static?.ControlledGrid;
        return grid == null || grid.GridSystems?.ResourceDistributor != null;
    }
}
