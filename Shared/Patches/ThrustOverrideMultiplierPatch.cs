using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Shared.Plugin;
using VRage.Game;
using VRage.Sync;

namespace Shared.Patches;

// Client + Server
//
// Keeps a thruster's override across a save and load when a mod has set its thrust
// multiplier. See Docs/thrust-override-multiplier.md.
//
// MyThrust.GetObjectBuilderCubeBlock saves ThrustOverride in Newtons including the multiplier
// (override% x ThrustMultiplier x ForceMagnitude / 100), but MyThrust.Init reads it back as
// override% = ThrustOverride x 100 / ForceMagnitude, clamped to ForceMagnitude, without the
// multiplier. The multiplier itself is not saved: IMyThrust.ThrustMultiplier is set by mod
// code at runtime (MES NpcThrusterLogic sets it on every NPC thruster it manages). So every
// save and load multiplies the override percentage by the multiplier, and once the mod sets
// the multiplier again the thruster pushes that much harder. On a Magnetar cluster each
// handover is a save and load: MES NPC thrusters with multiplier 2 came back with twice the
// override (19.68 N -> 39.36 N) and failed the handover check (DF #1382).
//
// The postfix saves the override in the unit Init reads, override% x ForceMagnitude / 100,
// so a save and load restore the same percentage whatever the multiplier. A thruster with
// multiplier 1 (every vanilla thruster) is left alone, so its saved value stays the same to
// the last bit.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyThrust), nameof(MyThrust.GetObjectBuilderCubeBlock))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ThrustOverrideMultiplierPatch
{
    private static readonly AccessTools.FieldRef<MyThrust, Sync<float, SyncDirection.BothWays>> ThrustOverridePercent =
        AccessTools.FieldRefAccess<MyThrust, Sync<float, SyncDirection.BothWays>>("m_thrustOverride");

    private static void Postfix(MyThrust __instance, MyObjectBuilder_CubeBlock __result)
    {
        if (Common.Config?.Enabled != true || __result is not MyObjectBuilder_Thrust builder)
            return;

        if (((Sandbox.ModAPI.IMyThrust)__instance).ThrustMultiplier == 1f)
            return;

        builder.ThrustOverride = (float)ThrustOverridePercent(__instance) * __instance.BlockDefinition.ForceMagnitude * 0.01f;
    }
}
