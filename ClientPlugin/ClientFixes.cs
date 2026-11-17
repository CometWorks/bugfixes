using System;
using ClientPlugin.Patches;
using Shared.Config;
using Shared.Patches;

namespace ClientPlugin;

internal static class ClientFixes
{
    internal const string VicinityModelPaths = nameof(VicinityModelPaths);

    // Patch class names double as config keys except where one fix uses multiple hooks
    // or already has its own config key.
    internal static string GetPatchFix(Type patch)
    {
        while (patch.DeclaringType != null)
            patch = patch.DeclaringType;

        return patch.Name switch
        {
            nameof(MyLocalCacheVicinityModelPathPatch) or nameof(MySessionVicinityModelPathPatch) =>
                VicinityModelPaths,
            nameof(ScriptCompilerNamePatch) => nameof(PluginConfig.IsolateMathStatics),
            nameof(WorldSavePathPatch) => nameof(PluginConfig.WorldStorageInWorldFolder),
            _ => patch.Name,
        };
    }

    internal static readonly (string Id, string Label)[] Settings =
    {
        (nameof(PluginConfig.Enabled), "Enable Bugfixes"),
        (VicinityModelPaths, "Resolve mod model paths during vicinity preload"),
        (nameof(HudEnergyTimeRemainingStatPatch), "Guard the HUD energy time stat"),
        (nameof(LaserAntennaCopyCoordsPatch), "Copy valid laser antenna GPS coordinates"),
        (nameof(MeshIndexHashPatch), "Prevent mesh index stalls"),
        (nameof(ActorWorldMatrixIndexPrepatch), "Fix stale rendering of moving blocks"),
        (nameof(MultilinePasteCaretPatch), "Fix the caret after multiline paste"),
        (nameof(TextAtlasAlphamaskPatch), "Smooth block label text edges"),
        (nameof(CockpitInteriorVisibilityPatch), "Keep seat interiors hidden from outside"),
        (nameof(OffscreenRenderMessageReusePatch), "Fix LCD render message reuse"),
        (nameof(HavokEndOfStepCallbackPatch), "Guard detached Havok contact listeners"),
        (nameof(VoxelClipmapWaitSetPatch), "Release closed voxel maps"),
        (nameof(LaserAntennaRepairWeldPatch), "Preserve laser links during repairs"),
        (nameof(ClipboardPreviewCreationBlockPatch), "Clean up failed clipboard entity creation"),
        (nameof(DebrisEntityIdPatch), "Prevent duplicate debris entity IDs"),
        (nameof(EmptyStockpilePastePatch), "Allow empty stockpiles in pasted blueprints"),
        (nameof(LoadCameraEntityIdZeroPatch), "Handle entity ID zero when loading the camera"),
        (nameof(PluginConfig.WorldStorageInWorldFolder), "Keep mod storage in the world folder"),
        (nameof(ModFileHandlesPatch), "Close mod files when a world unloads"),
        (nameof(CharacterMissileIdentityPatch), "Guard missiles with a deleted pilot identity"),
        (nameof(ConveyorSorterControlsPatch), "Keep vanilla conveyor sorter controls"),
        (nameof(ClipboardPreviewFailedGridPatch), "Hide failed clipboard grid previews"),
        (nameof(RadiationStatClosedCharacterPatch), "Skip radiation updates on closed characters"),
        (nameof(PluginConfig.IsolateMathStatics), "Isolate VRage.Math static fields"),
        (nameof(ThrustOverrideMultiplierPatch), "Preserve thrust override when saving"),
        (nameof(CockpitExitDampeningPatch), "Guard dampeners when exiting a fast cockpit"),
        (nameof(SoundBlockEmitterPatch), "Restore missing looping sound emitters"),
        (nameof(ModWithoutDataFolderPatch), "Allow mods without a Data folder"),
        (nameof(PlacementTestShapeLeakPatch), "Release Havok shapes after placement tests"),
        (
            nameof(EventControllerSelectedBlocksPatch),
            "Handle duplicate event controller selections"
        ),
        (nameof(MergeBlockPhysicsPatch), "Guard merge blocks without a merge field"),
        (nameof(HavokBatchRemovalPatch), "Guard Havok batch removal"),
        (nameof(PistonAttachWithoutSubpartsPatch), "Guard pistons with missing subparts"),
        (nameof(ConveyorTubeWithoutLinePatch), "Guard conveyor tubes without a line"),
    };
}
