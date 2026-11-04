using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents.Renders;
using Shared.Plugin;
using VRage.Game.Components;

namespace ClientPlugin.Patches;

// Fixes a cockpit's or cryo chamber's interior model being drawn from outside, on top of the
// exterior model, after its grid or the block is hidden and shown again.
//
// A seat with an InteriorModel has two render objects: the exterior and the interior seen from
// the seat. MyRenderComponentCockpit.AddRenderObjects creates the interior hidden, and
// MyCockpit.UpdateCockpitModel shows exactly one of them, depending on whether the camera is in
// the seat. MyRenderComponentBase.UpdateRenderObjectVisibility(true) does not know about this
// and shows every render object of the component, and the cockpit component does not override
// it. It runs whenever an entity becomes visible again: Render.Visible (IMyEntity.Visible) going
// from false to true on the block or on any parent (it recurses through the hierarchy), or the
// block re-entering the scene with its render objects still there. Plugins and mods that hide
// grids for a while and show them again (Impostors, Sections' cutaway, Space Jump's effects)
// and the game's own MyUpdateTriggerComponent all go through it. Nothing hides the interior
// again until the camera enters or leaves the seat, which calls UpdateCockpitModel.
//
// The interior then sits in the exterior's place and the two Z-fight. At low model quality the
// exterior is drawn at LOD1 even up close while the interior has no LODs, so their surfaces
// differ slightly and the fighting is plain to see (DIS-0010). The interior also keeps the color
// and skin the block had when the render objects were made: a repaint only updates
// m_renderObjectIDs[0] (MyRenderComponentBase.UpdateRenderEntity), so the second copy shows the
// old color.
//
// Present on Windows: the render components are platform independent game code.
//
// The postfix calls UpdateCockpitModel after the render objects of a seat with an interior model
// have been shown, so the interior is hidden again unless the camera is in the seat.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyRenderComponentBase), "UpdateRenderObjectVisibility")]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class CockpitInteriorVisibilityPatch
{
    private static void Postfix(MyRenderComponentBase __instance, bool visible)
    {
        if (
            visible
            && __instance is MyRenderComponentCockpit { Container.Entity: MyCockpit cockpit }
            && Common.Config?.Enabled == true
        )
            cockpit.UpdateCockpitModel();
    }
}
