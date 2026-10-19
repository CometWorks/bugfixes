using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Shared.Plugin;
using VRage.Game;

namespace Shared.Patches;

// Client + Server
//
// Hides a clipboard or projector preview when one of its grids failed to initialize, instead
// of adding the broken grid to the world under entity id 0. See
// Docs/clipboard-preview-failed-grid.md.
//
// MyGridClipboard.ChangeClipboardPreview creates each preview grid, keeps it in a local and
// then initializes it with MyEntities.InitEntity. When a block's Init throws, InitEntity logs
// "ERROR Entity init", sets the grid's EntityId to 0 and nulls its own reference, but the
// method goes on with the local and puts the half initialized grid into the preview. The
// activation callback then adds every preview grid to the world, and MyEntities.Add
// registers an entity whose id is not taken yet, so the broken grid ends up as entity 0.
//
// Entity id 0 is what the game uses for "nothing". A player sitting in a cockpit with no
// target locked has target id 0, so MyTargetLockingComponent takes the broken grid for its
// locked target and its UpdateBeforeSimulation dereferences the grid's Physics, which a
// preview grid doesn't have. The game crashes with a NullReferenceException in
// MyTargetFocusComponent.IsTargetInRange on the first simulation step (DIS-0033).
//
// The postfix drops the grids that failed to initialize, recognized by their EntityId of 0,
// and then hides the whole preview, which is how ChangeClipboardPreview itself handles a
// copied grid that comes out without blocks. A projector shows its "cannot find blueprint"
// message then, and the creative clipboard shows nothing.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyGridClipboard), "ChangeClipboardPreview")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ClipboardPreviewFailedGridPatch
{
    private static readonly Action<
        MyGridClipboard,
        bool,
        List<MyCubeGrid>,
        List<MyObjectBuilder_CubeGrid>
    > ChangeClipboardPreview = AccessTools.MethodDelegate<
        Action<MyGridClipboard, bool, List<MyCubeGrid>, List<MyObjectBuilder_CubeGrid>>
    >(AccessTools.Method(typeof(MyGridClipboard), "ChangeClipboardPreview"));

    private static void Postfix(
        MyGridClipboard __instance,
        bool visible,
        List<MyCubeGrid> previewGrids,
        List<MyObjectBuilder_CubeGrid> copiedGrids
    )
    {
        if (!visible || Common.Config?.Enabled != true)
            return;

        var failed = previewGrids.RemoveAll(grid => grid.EntityId == 0);
        if (failed == 0)
            return;

        Common.Logger.Info(
            "Hiding the clipboard or projector preview: {0} of its grids failed to initialize, see the ERROR Entity init above",
            failed
        );
        ChangeClipboardPreview(__instance, false, previewGrids, copiedGrids);
    }
}
