using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Shared.Plugin;
using VRage.Game.Entity;
using VRageMath;

namespace ClientPlugin.Patches;

// Fixes thruster flames staying lit on a ship somebody else flew, after that pilot lost control
// of it mid-burn. See Docs/remote-pilot-flames.md.
//
// A grid's MyEntityThrustComponent.ControlThrust is the pilot's throttle. The current ship
// controller adds the pilot's move indicator to it every frame (MyShipController.MoveAndRotate)
// and sets it back to zero after the frame (MyShipController.UpdateAfterSimulation), but only
// while it is the control system's current ship controller. When control is released,
// MyShipController.OnControlReleased clears it on the server and on the pilot's own client only.
// On every other client the control system just drops its controller, nothing zeroes the
// throttle any more, and the thrust component keeps lighting the thrusters from the last frame's
// value for good. The server has no thrust: the ship drifts and burns no fuel.
//
// It shows when a pilot's client drops out mid-burn (a crash, a disconnect, a bot restarted)
// and on clustered servers, where a pilot's control moves between nodes.
//
// The prefix zeroes the throttle of a grid with no ship controller, no turret in control and no
// autopilot before the thrust update, so the thrust component turns the flames off by itself.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(
    typeof(MyEntityThrustComponent),
    nameof(MyEntityThrustComponent.UpdateBeforeSimulation),
    typeof(bool),
    typeof(MyEntity)
)]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class RemotePilotFlamesPatch
{
    private static void Prefix(MyEntityThrustComponent __instance)
    {
        if (
            __instance.ControlThrust != Vector3.Zero
            && !__instance.AutopilotEnabled
            && __instance.Entity is MyCubeGrid { ControlledFromTurret: false } grid
            && grid.GridSystems?.ControlSystem?.GetShipController() == null
            && Common.Config?.Enabled == true
        )
            __instance.ControlThrust = Vector3.Zero;
    }
}
