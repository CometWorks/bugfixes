using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.EntityComponents.Renders;
using Shared.Plugin;
using VRage.Game.Components;

namespace Shared.Patches;

// Fixes a leak of every voxel map (asteroid, planet) that ever enters the scene.
//
// MyRenderComponentVoxelMap.AddRenderObjects creates the voxel map's clipmap and puts it into
// the static set MyRenderComponentVoxelMap.VoxelLoadingWaitStep.Clipmaps, which the loading
// screen polls to know when terrain is ready. The only way out of that set is the clipmap's
// own Loaded event (RemoveClipmap). Removing the render objects - the voxel map leaving the
// scene or being deleted - does not take the clipmap out, and neither does closing the
// entity. A clipmap that is dropped before it reports Loaded therefore stays in the static
// set for the life of the process, and it references the MyVoxelMesherComponent, the entity's
// component container and so the closed MyVoxelMap with its whole clipmap cell structure.
//
// On the dedicated server nothing renders, Loaded never fires, and the set holds every voxel
// map the server ever created. Two full heap dumps of one clustered server 38 minutes apart
// (2026-09-29) showed the set's count equal to the MyVoxelMap count (6,957 then 11,031), about
// 84% of those maps closed, and the voxel types the largest live growth of the heap. A game
// client can hit the same thing for a voxel map that leaves the scene before its clipmap has
// loaded.
//
// The postfix removes the component's clipmap from the set when its render objects are
// removed, through the game's own RemoveClipmap, so a set that becomes empty still signals
// ClipmapsReady exactly as a finished load would. MyRenderComponentVoxelMap does not override
// RemoveRenderObjects and MyRenderComponentPlanet calls the base method, so patching the base
// covers both. A voxel map added back to the scene gets a new clipmap from AddRenderObjects,
// which goes into the set again.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyRenderComponentBase), nameof(MyRenderComponentBase.RemoveRenderObjects))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class VoxelClipmapWaitSetPatch
{
    private static void Postfix(MyRenderComponentBase __instance)
    {
        if (Common.Config?.Enabled != true)
            return;

        if (!(__instance is MyRenderComponentVoxelMap voxelRender))
            return;

        var clipmap = voxelRender.Clipmap;
        if (clipmap == null)
            return;

        var waiting = MyRenderComponentVoxelMap.VoxelLoadingWaitStep.Clipmaps;
        bool present;
        lock (waiting)
            present = waiting.Contains(clipmap);

        if (present)
            MyRenderComponentVoxelMap.VoxelLoadingWaitStep.RemoveClipmap(clipmap);
    }
}
