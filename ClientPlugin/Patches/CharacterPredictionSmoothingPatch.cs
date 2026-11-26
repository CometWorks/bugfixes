using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication.History;
using Sandbox.Game.World;
using Shared.Config;
using Shared.Plugin;
using VRage.Game.Entity;
using VRage.Game.Networking;
using VRage.Network;
using VRageMath;

namespace ClientPlugin.Patches;

// Makes the switches of the client's prediction of its own character less jarring.
// See Docs/character-contact-prediction.md.
//
// The game switches prediction off on contact with a loose grid and after excessive server
// corrections, and leaves it off until 10 s after the last such event (MyCharacter.UpdatePredictionFlag).
// All that time the controls react a round trip late. CharacterPredictionHoldSeconds shortens that.
//
// When prediction switches off, MyAnimatedSnapshotSync shows the server's state from an
// interpolation buffer, a round trip and 64 ms behind the predicted position, so the character
// snaps back by about its speed times that delay (3-9 m in the SE-1527 rig). Here the first
// animated snapshot is offset to where the character was shown, and the offset shrinks to zero
// over CharacterPredictionBlendSeconds. Offsets under 10 cm or over 20 m are left to the game.
// The jump when prediction comes back is small already: the predicted sync smooths it.
//
// The predicted sync spreads a server correction over SMOOTH_ITERATIONS times the setup's
// IterationsFactor updates, 9 for the jetpack. The farther the server, the larger the corrections,
// so with CharacterSmoothingRttMs above 0 both that and the switch-off blend are scaled by
// max(1, RTT / CharacterSmoothingRttMs), with the RTT the game measures (MyReplicationClient.Ping).
//
// ReSharper disable once UnusedType.Global
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class CharacterPredictionSmoothingPatch
{
    private const double MinBlendMeters = 0.1;
    private const double MaxBlendMeters = 20;
    private const double VanillaHoldSeconds = 10;

    private static readonly FieldInfo ForceDisableTime = AccessTools.Field(
        typeof(MyCharacter),
        "m_forceDisablePredictionTime"
    );

    private static readonly FieldInfo AnimatedEntity = AccessTools.Field(
        typeof(MyAnimatedSnapshotSync),
        "m_entity"
    );

    private static PluginConfig ActiveConfig =>
        Common.Config is PluginConfig { Enabled: true } config ? config : null;

    // The local character, while its animated sync runs
    private static MyCharacter animating;

    private static int lastAnimatedFrame = -10;
    private static Vector3D offset;
    private static int blendSteps,
        blendLeft;

    private static readonly FieldInfo PredictedEntity = AccessTools.Field(
        typeof(MyPredictedSnapshotSync),
        "m_entity"
    );

    // max(1, RTT / CharacterSmoothingRttMs), or 1 when that is 0 or there is no RTT yet
    private static double RttScale(PluginConfig config)
    {
        if (
            config.CharacterSmoothingRttMs <= 0
            || MyMultiplayer.Static?.ReplicationLayer is not MyReplicationClient client
        )
            return 1;

        return Math.Max(1, client.Ping.Milliseconds / config.CharacterSmoothingRttMs);
    }

    [HarmonyPatch(typeof(MyPredictedSnapshotSync), "UpdatePrediction")]
    [HarmonyPatchCategory("Client")]
    private static class Smoothing
    {
        private static void Prefix(MyPredictedSnapshotSync __instance, out int __state)
        {
            __state = MyPredictedSnapshotSync.SMOOTH_ITERATIONS;
            if (
                PredictedEntity.GetValue(__instance) is MyCharacter character
                && character == MySession.Static?.LocalCharacter
                && ActiveConfig is { } config
            )
                MyPredictedSnapshotSync.SMOOTH_ITERATIONS = (int)
                    Math.Round(__state * RttScale(config));
        }

        private static void Postfix(int __state)
        {
            MyPredictedSnapshotSync.SMOOTH_ITERATIONS = __state;
        }
    }

    [HarmonyPatch(typeof(MyCharacter), nameof(MyCharacter.UpdatePredictionFlag))]
    [HarmonyPatchCategory("Client")]
    private static class Hold
    {
        private static void Prefix(MyCharacter __instance)
        {
            if (
                !__instance.ForceDisablePrediction
                || Sync.IsServer
                || ActiveConfig is not { } config
                || config.CharacterPredictionHoldSeconds >= VanillaHoldSeconds
            )
                return;

            var since = (double)ForceDisableTime.GetValue(__instance);
            if (
                MySandboxGame.Static.SimulationTime.Seconds
                > since + config.CharacterPredictionHoldSeconds
            )
                __instance.ForceDisablePrediction = false;
        }
    }

    [HarmonyPatch(typeof(MyAnimatedSnapshotSync), nameof(MyAnimatedSnapshotSync.Update))]
    [HarmonyPatchCategory("Client")]
    private static class Animated
    {
        private static void Prefix(MyAnimatedSnapshotSync __instance)
        {
            if (
                AnimatedEntity.GetValue(__instance) is MyCharacter character
                && character == MySession.Static?.LocalCharacter
            )
                animating = character;
        }

        private static void Postfix()
        {
            animating = null;
        }
    }

    [HarmonyPatch(typeof(MySnapshotCache), nameof(MySnapshotCache.Add))]
    [HarmonyPatchCategory("Client")]
    private static class Blend
    {
        private static void Prefix(
            MyEntity entity,
            ref MySnapshot snapshot,
            MySnapshotFlags snapshotFlags,
            bool reset
        )
        {
            if (animating == null || entity != animating || !snapshotFlags.ApplyPosition)
                return;

            var frame = MySession.Static.GameplayFrameCounter;
            var switchedOff = frame - lastAnimatedFrame > 1;
            lastAnimatedFrame = frame;
            if (reset || ActiveConfig is not { CharacterPredictionBlendSeconds: > 0 } config)
            {
                blendLeft = 0;
                return;
            }

            if (switchedOff)
            {
                blendLeft = 0;
                var shown = new MySnapshot(
                    entity,
                    default(MyClientInfo),
                    snapshotFlags.ApplyPhysicsLocal,
                    snapshotFlags.InheritRotation
                );
                if (shown.ParentId != snapshot.ParentId)
                    return;

                offset = shown.Position - snapshot.Position;
                var distance = offset.Length();
                if (distance < MinBlendMeters || distance > MaxBlendMeters)
                    return;

                // One snapshot per update, 60 per second
                blendSteps = blendLeft = Math.Max(
                    1,
                    (int)Math.Round(config.CharacterPredictionBlendSeconds * RttScale(config) * 60)
                );
            }

            if (blendLeft <= 0)
                return;

            snapshot.Position += offset * ((double)blendLeft / blendSteps);
            blendLeft--;
        }
    }
}
