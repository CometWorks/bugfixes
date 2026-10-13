using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.World;
using Shared.Plugin;
using VRage.Game;
using VRage.ModAPI;
using VRageMath;

namespace Shared.Patches;

// Client + Server
//
// Keeps a world from failing to load when a mod has registered an entity under entity id 0.
// See Docs/load-camera-entity-id-zero.md.
//
// A saved world without a camera entity has CameraEntity 0 in its checkpoint, which is what a
// dedicated server always saves. MySession.LoadCamera takes 0 for a real id: it asks
// MyEntities.EntityExists(0), and when that is true it puts the camera on that entity. No game
// entity has id 0, but MyEntities.Add registers an entity that has no id yet under 0. WeaponCore
// does that with the projectile model entities it creates while the world's entities load.
//
// LoadCamera then calls SetCameraController(Entity, thatEntity). The entity is not a camera
// controller, so the game falls back to the local character, which a dedicated server doesn't
// have, and the CameraController setter dies on null. The world load fails with a
// NullReferenceException in MySession.set_CameraController (DIS-0018).
//
// The transpiler routes LoadCamera's SetCameraController call through a method that drops a
// camera entity with id 0 and uses the spectator camera, which is what LoadCamera does when
// the checkpoint's camera entity doesn't exist.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MySession), "LoadCamera")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class LoadCameraEntityIdZeroPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    ) =>
        instructions.MethodReplacer(
            AccessTools.Method(typeof(MySession), nameof(MySession.SetCameraController)),
            AccessTools.Method(typeof(LoadCameraEntityIdZeroPatch), nameof(SetCameraController))
        );

    private static void SetCameraController(
        MySession session,
        MyCameraControllerEnum cameraControllerEnum,
        IMyEntity cameraEntity,
        Vector3D? position
    )
    {
        if (Common.Config?.Enabled == true && cameraEntity != null && cameraEntity.EntityId == 0)
        {
            Common.Logger.Info(
                "The saved camera entity resolved to an entity with id 0 ({0}, model {1}), using the spectator camera instead",
                cameraEntity.GetType().FullName,
                cameraEntity.Model?.AssetName
            );
            cameraControllerEnum = MyCameraControllerEnum.Spectator;
            cameraEntity = null;
        }

        session.SetCameraController(cameraControllerEnum, cameraEntity, position);
    }
}
