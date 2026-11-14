using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Shared.Plugin;
using VRageMath;

namespace ClientPlugin.Patches;

// Keeps the client's prediction of its own character on while the character touches a grid that
// is at rest, like the floor or a wall of a station. See Docs/character-contact-prediction.md.
//
// On a multiplayer client MyCharacter.UpdatePredictionFlag tests every frame whether the local
// character's capsule overlaps the shape of a grid. If it does, it sets ForceDisablePrediction,
// and that stays set for 10 seconds after the last overlap. Without prediction the character is
// moved by the server's snapshots (MyAnimatedSnapshotSync), which arrive a ping and an
// interpolation delay late, so the controls feel laggy for those 10 seconds. When prediction
// comes back, MyCharacterPhysicsStateGroup re-inits MyPredictedSnapshotSync and the character
// jumps by about its speed times that delay. Flying with the jetpack in a hangar, brushing a
// floor, a wall or a parked ship does this over and over (SE-1527).
//
// The rule is there for grids that move: the client shows those where the server had them a
// moment ago, so a predicted character would collide with the grid in the wrong place. A grid at
// rest is in the same place on both sides, and the server and the client resolve the contact the
// same way. Measured on a live server, the server's corrections stay the same size with
// prediction left on through such contacts.
//
// The prefix remembers whether prediction was already forced off. The postfix acts only when
// this call switched it off, which inside the method only the overlap does. It repeats the
// overlap test, and if every overlapped grid is static or slower than 0.5 m/s at the character,
// it clears the flag and restores IsClientPredicted. The game's own InvokeLater of
// UpdateCharacterPhysics then runs with the restored value. An overlap with a moving grid, and
// the switch-off after excessive corrections, are left to the game.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyCharacter), nameof(MyCharacter.UpdatePredictionFlag))]
[HarmonyPatchCategory("Client")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class CharacterContactPredictionPatch
{
    private const float RestSpeedSquared = 0.5f * 0.5f;

    // The collision layer the game's own test uses
    private const int OverlapLayer = MyPhysics.CollisionLayers.CollisionLayerWithoutCharacter;

    private static readonly FieldInfo ForceDisablePrediction = AccessTools.Field(
        typeof(MyCharacter),
        "m_forceDisablePrediction"
    );

    private static readonly MethodInfo SetIsClientPredicted = AccessTools.PropertySetter(
        typeof(MyCharacter),
        nameof(MyCharacter.IsClientPredicted)
    );

    private static readonly List<HkBodyCollision> Overlaps = new();

    private static void Prefix(MyCharacter __instance, out bool __state)
    {
        __state = __instance.ForceDisablePrediction;
    }

    private static void Postfix(MyCharacter __instance, bool __state)
    {
        if (
            __state
            || !__instance.ForceDisablePrediction
            || Sync.IsServer
            || __instance.IsDead
            || __instance.IsOnLadder
            || Common.Config?.Enabled != true
            || !OverlapsOnlyGridsAtRest(__instance)
        )
            return;

        ForceDisablePrediction.SetValue(__instance, false);
        var predicted =
            MyFakes.MULTIPLAYER_CLIENT_SIMULATE_CONTROLLED_CHARACTER
            && MySession.Static.TopMostControlledEntity == __instance
            && (
                !__instance.JetpackRunning
                || MyFakes.MULTIPLAYER_CLIENT_SIMULATE_CONTROLLED_CHARACTER_IN_JETPACK
            )
            && !__instance.AlwaysDisablePrediction;
        if (__instance.IsClientPredicted != predicted)
            SetIsClientPredicted.Invoke(__instance, new object[] { predicted });
    }

    private static bool OverlapsOnlyGridsAtRest(MyCharacter character)
    {
        var proxy = character.Physics?.CharacterProxy;
        if (proxy == null)
            return false;

        var shape = proxy.GetCollisionShape();
        if (shape.IsZero)
            return false;

        MatrixD matrix = character.Physics.GetWorldMatrix();
        matrix.Translation += Vector3D.TransformNormal(character.Physics.Center, ref matrix);
        Vector3D position = matrix.Translation;
        var rotation = Quaternion.CreateFromRotationMatrix(in matrix);
        Overlaps.Clear();
        MyPhysics.GetPenetrationsShape(
            shape,
            ref position,
            ref rotation,
            Overlaps,
            OverlapLayer
        );
        try
        {
            foreach (var overlap in Overlaps)
            {
                if (
                    overlap.Body.UserObject is MyGridPhysics { IsStatic: false } grid
                    && grid.GetVelocityAtPoint(position).LengthSquared() > RestSpeedSquared
                )
                    return false;
            }
        }
        finally
        {
            Overlaps.Clear();
        }

        return true;
    }
}
