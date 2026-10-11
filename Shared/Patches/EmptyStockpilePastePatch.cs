using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.Entities;
using Shared.Plugin;
using VRage.Game;
using VRageMath;

namespace Shared.Patches;

// Fixes the paste of a blueprint with an empty construction stockpile failing in any world
// that isn't OFFLINE.
//
// A partially built block whose stockpile holds no components is saved as
// <ConstructionStockpile />. The pre-generated XML reader of VRage.Game.XmlSerializers sets
// Items to null whenever the element has no Items children, overwriting the field's empty
// array initializer. MyCubeGrid.TryPasteGrid_Implementation is a replicated event, and
// outside OFFLINE its MyPasteGridParameters go through the binary event serializer, which
// refuses the null:
//   Error serializing MyObjectBuilder_ConstructionStockpile.Items, member contains null
// The clipboard (Ctrl+V, F10) clones the grids first, which restores the empty array, but
// MyGridStorageHelper, MyVisualScriptLogicProvider and plugins raise the event on freshly
// read builders.
//
// Present on Windows: reproduced on the Windows binaries under Proton without any Linux
// plugin, settled against decompiled 1.210.014.
//
// Every route builds MyPasteGridParameters through its only constructor, so the prefix
// replaces a null Items with an empty array there, also in projected grids. An empty array
// means the same as a stockpile with no components and serializes as zero items.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(
    typeof(MyCubeGrid.MyPasteGridParameters),
    MethodType.Constructor,
    typeof(List<MyObjectBuilder_CubeGrid>),
    typeof(bool),
    typeof(Vector3),
    typeof(bool),
    typeof(MyCubeGrid.RelativeOffset),
    typeof(List<ulong>)
)]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class EmptyStockpilePastePatch
{
    private static void Prefix(List<MyObjectBuilder_CubeGrid> entities)
    {
        if (Common.Config?.Enabled != true || entities == null)
            return;

        foreach (var grid in entities)
            FixStockpiles(grid);
    }

    private static void FixStockpiles(MyObjectBuilder_CubeGrid grid)
    {
        if (grid?.CubeBlocks == null)
            return;

        foreach (var block in grid.CubeBlocks)
        {
            var stockpile = block?.ConstructionStockpile;
            if (stockpile != null && stockpile.Items == null)
                stockpile.Items = [];

            if (block is not MyObjectBuilder_ProjectorBase projector)
                continue;

            FixStockpiles(projector.ProjectedGrid);
            if (projector.ProjectedGrids != null)
            {
                foreach (var projectedGrid in projector.ProjectedGrids)
                    FixStockpiles(projectedGrid);
            }
        }
    }
}
