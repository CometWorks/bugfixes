using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shared.Config;

public class PluginConfig : IPluginConfig
{
    public const string FileName = "Bugfixes.cfg";
    public event PropertyChangedEventHandler PropertyChanged;

    private void SetValue<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [Category("General")]
    [DisplayName("Enable Bugfixes")]
    [Description("Controls all fixes. Individual settings are kept. Restart to apply changes.")]
    public bool Enabled
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Rendering")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Resolve mod model paths during vicinity preload")]
    [Description("Resolve mod model paths during vicinity preload. Restart to apply changes.")]
    public bool VicinityModelPaths
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Rendering")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Prevent mesh index stalls")]
    [Description("Prevent mesh index stalls. Restart to apply changes.")]
    public bool MeshIndexHashPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Rendering")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Fix stale rendering of moving blocks")]
    [Description("Fix stale rendering of moving blocks. Restart to apply changes.")]
    public bool ActorWorldMatrixIndexPrepatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Rendering")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Smooth block label text edges")]
    [Description("Smooth block label text edges. Restart to apply changes.")]
    public bool TextAtlasAlphamaskPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Rendering")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Keep seat interiors hidden from outside")]
    [Description("Keep seat interiors hidden from outside. Restart to apply changes.")]
    public bool CockpitInteriorVisibilityPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Rendering")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Fix LCD render message reuse")]
    [Description("Fix LCD render message reuse. Restart to apply changes.")]
    public bool OffscreenRenderMessageReusePatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Rendering")]
    [DisplayName("Release closed voxel maps")]
    [Description("Release closed voxel maps. Restart to apply changes.")]
    public bool VoxelClipmapWaitSetPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("HUD")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Detect ore on rotated asteroids")]
    [Description("Show ore near the detector on rotated asteroids. Restart to apply changes.")]
    public bool RotatedAsteroidOrePatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Audio")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Voice chat follows the speaker")]
    [Description(
        "Play voice chat from where the speaker is now, not from where they first spoke. Restart to apply changes."
    )]
    public bool VoiceChatPositionPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("User interface")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Guard the HUD energy time stat")]
    [Description("Guard the HUD energy time stat. Restart to apply changes.")]
    public bool HudEnergyTimeRemainingStatPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("User interface")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Exit when the save progress box outlives the session")]
    [Description(
        "Exit the game instead of crashing when the save progress box is answered after the session unloaded. Restart to apply changes."
    )]
    public bool SaveProgressBoxAfterUnloadPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("User interface")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Fix the caret after multiline paste")]
    [Description("Fix the caret after multiline paste. Restart to apply changes.")]
    public bool MultilinePasteCaretPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Physics")]
    [ConfigTarget(PluginTarget.Server)]
    [DisplayName("Let characters push resting grids")]
    [Description(
        "Transfers the character's impact when a loose grid stays motionless after contact. Restart to apply changes."
    )]
    public bool CharacterGridPushPatch
    {
        get;
        set => SetValue(ref field, value);
    }

    [Category("Physics")]
    [DisplayName("Guard detached Havok contact listeners")]
    [Description("Guard detached Havok contact listeners. Restart to apply changes.")]
    public bool HavokEndOfStepCallbackPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Physics")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Turn off flames of a ship nobody flies")]
    [Description(
        "Turn off thruster flames left lit after another pilot lost control of a ship. Restart to apply changes."
    )]
    public bool RemotePilotFlamesPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Physics")]
    [DisplayName("Guard Havok batch removal")]
    [Description("Guard Havok batch removal. Restart to apply changes.")]
    public bool HavokBatchRemovalPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Physics")]
    [DisplayName("Release Havok shapes after placement tests")]
    [Description("Release Havok shapes after placement tests. Restart to apply changes.")]
    public bool PlacementTestShapeLeakPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Physics")]
    [DisplayName("Prevent duplicate debris entity IDs")]
    [Description("Prevent duplicate debris entity IDs. Restart to apply changes.")]
    public bool DebrisEntityIdPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Copy valid laser antenna GPS coordinates")]
    [Description("Copy valid laser antenna GPS coordinates. Restart to apply changes.")]
    public bool LaserAntennaCopyCoordsPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Preserve laser links during repairs")]
    [Description("Preserve laser links during repairs. Restart to apply changes.")]
    public bool LaserAntennaRepairWeldPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Keep vanilla conveyor sorter controls")]
    [Description("Keep vanilla conveyor sorter controls. Restart to apply changes.")]
    public bool ConveyorSorterControlsPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Keep vanilla terminal controls when mods add controls early")]
    [Description(
        "Keep vanilla terminal controls when mods add controls early. Restart to apply changes."
    )]
    public bool EarlyModTerminalControlsPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Preserve thrust override when saving")]
    [Description("Preserve thrust override when saving. Restart to apply changes.")]
    public bool ThrustOverrideMultiplierPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Handle duplicate event controller selections")]
    [Description("Handle duplicate event controller selections. Restart to apply changes.")]
    public bool EventControllerSelectedBlocksPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Guard merge blocks without a merge field")]
    [Description("Guard merge blocks without a merge field. Restart to apply changes.")]
    public bool MergeBlockPhysicsPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Reattach rotors, hinges and pistons on the update thread")]
    [Description(
        "Reattach rotors, hinges and pistons on the update thread. Restart to apply changes."
    )]
    public bool MechanicalReattachThreadPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Guard pistons with missing subparts")]
    [Description("Guard pistons with missing subparts. Restart to apply changes.")]
    public bool PistonAttachWithoutSubpartsPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blocks and conveyors")]
    [DisplayName("Guard conveyor tubes without a line")]
    [Description("Guard conveyor tubes without a line. Restart to apply changes.")]
    public bool ConveyorTubeWithoutLinePatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Characters")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Keep prediction on when touching static grids")]
    [Description(
        "Stops the character lagging and snapping back after touching a station. Restart to apply changes."
    )]
    public bool CharacterContactPredictionPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    // 0 (the default): CharacterContactPredictionPatch keeps prediction on for static grids only.
    // Above 0: also for loose grids slower than this many m/s at the character's position, which
    // risks the character briefly ending up inside such a grid. Set in Bugfixes.cfg, not in the dialog.
    [Category("Characters")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Rest speed of touched grids (m/s)")]
    [Description(
        "0: static grids only. Above 0: loose grids slower than this also count as at rest. Restart to apply changes."
    )]
    public float CharacterContactRestSpeed
    {
        get;
        set => SetValue(ref field, value);
    }

    [Category("Characters")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Smooth the switches of character prediction")]
    [Description(
        "Stops your character jumping back when its prediction switches off, and turns it back on sooner. Restart to apply changes."
    )]
    public bool CharacterPredictionSmoothingPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    // Seconds after the last contact or excessive correction until CharacterPredictionSmoothingPatch
    // turns prediction back on. 10 or more keeps the game's 10 s. Set in Bugfixes.cfg, not in the dialog.
    [Category("Characters")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Prediction hold after contact (s)")]
    [Description("10 or more: the game's 10 s. Restart to apply changes.")]
    public float CharacterPredictionHoldSeconds
    {
        get;
        set => SetValue(ref field, value);
    } = 2f;

    // Seconds over which CharacterPredictionSmoothingPatch moves the character from where it was shown
    // to the server's position when prediction switches off. 0 keeps the game's jump. Set in
    // Bugfixes.cfg, not in the dialog.
    [Category("Characters")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Prediction switch-off blend (s)")]
    [Description("0: the game's jump. Restart to apply changes.")]
    public float CharacterPredictionBlendSeconds
    {
        get;
        set => SetValue(ref field, value);
    } = 0.5f;

    // CharacterPredictionSmoothingPatch scales the smoothing of server corrections and the switch-off
    // blend by max(1, RTT / this). 0: no scaling. Set in Bugfixes.cfg, not in the dialog.
    [Category("Characters")]
    [ConfigTarget(PluginTarget.Client)]
    [DisplayName("Smoothing RTT unit (ms)")]
    [Description("0: no scaling with the round trip time. Restart to apply changes.")]
    public float CharacterSmoothingRttMs
    {
        get;
        set => SetValue(ref field, value);
    } = 100f;

    [Category("Characters")]
    [DisplayName("Guard missiles with a deleted pilot identity")]
    [Description("Guard missiles with a deleted pilot identity. Restart to apply changes.")]
    public bool CharacterMissileIdentityPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Characters")]
    [DisplayName("Skip radiation updates on closed characters")]
    [Description("Skip radiation updates on closed characters. Restart to apply changes.")]
    public bool RadiationStatClosedCharacterPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Characters")]
    [DisplayName("Guard dampeners when exiting a fast cockpit")]
    [Description("Guard dampeners when exiting a fast cockpit. Restart to apply changes.")]
    public bool CockpitExitDampeningPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blueprints and projections")]
    [DisplayName("Clean up failed clipboard entity creation")]
    [Description("Clean up failed clipboard entity creation. Restart to apply changes.")]
    public bool ClipboardPreviewCreationBlockPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blueprints and projections")]
    [DisplayName("Allow empty stockpiles in pasted blueprints")]
    [Description("Allow empty stockpiles in pasted blueprints. Restart to apply changes.")]
    public bool EmptyStockpilePastePatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Blueprints and projections")]
    [DisplayName("Hide failed clipboard grid previews")]
    [Description("Hide failed clipboard grid previews. Restart to apply changes.")]
    public bool ClipboardPreviewFailedGridPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Mods, scripts and world storage")]
    [DisplayName("Handle entity ID zero when loading the camera")]
    [Description("Handle entity ID zero when loading the camera. Restart to apply changes.")]
    public bool LoadCameraEntityIdZeroPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Mods, scripts and world storage")]
    [DisplayName("Keep mod storage in the world folder")]
    [Description(
        "Uses the loaded world's folder. Mods may stop seeing data stored under the old path."
    )]
    public bool WorldStorageInWorldFolder
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Mods, scripts and world storage")]
    [DisplayName("Close mod files when a world unloads")]
    [Description("Close mod files when a world unloads. Restart to apply changes.")]
    public bool ModFileHandlesPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Mods, scripts and world storage")]
    [DisplayName("Isolate VRage.Math static fields")]
    [Description("Keeps scripts and mods from changing shared math values like Vector3D.Zero.")]
    public bool IsolateMathStatics
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Mods, scripts and world storage")]
    [DisplayName("Allow mods without a Data folder")]
    [Description("Allow mods without a Data folder. Restart to apply changes.")]
    public bool ModWithoutDataFolderPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;

    [Category("Audio")]
    [DisplayName("Restore missing looping sound emitters")]
    [Description("Restore missing looping sound emitters. Restart to apply changes.")]
    public bool SoundBlockEmitterPatch
    {
        get;
        set => SetValue(ref field, value);
    } = true;
}
