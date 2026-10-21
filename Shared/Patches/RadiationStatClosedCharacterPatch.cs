using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Shared.Plugin;
using SpaceEngineers.Game.EntityComponents.GameLogic;
using VRage.Game.Entity;
using VRage.Utils;

namespace Shared.Patches;

// Client + Server
//
// Skips the radiation damage callback of a character whose stats were removed after the
// callback was queued, instead of crashing the game. See Docs/radiation-stat-closed-character.md.
//
// MyRadiationStat.Update100 runs from MyEntityStatComponent.UpdateAfterSimulationParallel on a
// worker thread, and DoRadiationDamage queues a lambda with MySandboxGame.Static.Invoke, which
// runs it on the main thread later. When the character leaves the world in between (death,
// respawn, closing or a cluster handover), MyEntityStatComponent.OnBeforeRemovedFromContainer
// clears the stats dictionary the stat logic shares, so m_radiationStat returns null and the
// lambda throws a NullReferenceException in MySandboxGame.Update, which ends the game or server
// (DF #1289).
//
// The prefix skips the lambda when the radiation stat is gone or the character is closing.
// There is nobody left to damage then.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class RadiationStatClosedCharacterPatch
{
    private static readonly AccessTools.FieldRef<
        MyStatLogic,
        Dictionary<MyStringHash, MyEntityStat>
    > StatsField = AccessTools.FieldRefAccess<MyStatLogic, Dictionary<MyStringHash, MyEntityStat>>(
        "m_stats"
    );

    // The lambda DoRadiationDamage queues, compiled as <DoRadiationDamage>b__12_0 in 1.210
    private static MethodBase TargetMethod() =>
        AccessTools
            .GetDeclaredMethods(typeof(MyRadiationStat))
            .Single(m => m.Name.StartsWith("<DoRadiationDamage>b__"));

    private static bool Prefix(MyRadiationStat __instance)
    {
        if (Common.Config?.Enabled != true)
            return true;

        var stats = StatsField(__instance);
        var character = __instance.Character as MyEntity;
        if (
            stats != null
            && stats.ContainsKey(MyCharacterStatComponent.RADIATION_ID)
            && character is { MarkedForClose: false }
        )
            return true;

        Common.Logger.Info(
            "Skipped the radiation damage of character {0} ({1}), its stats were removed or it is closing",
            character?.DisplayName,
            character?.EntityId
        );
        return false;
    }
}
