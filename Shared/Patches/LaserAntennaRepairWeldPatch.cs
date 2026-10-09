using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity.EntityComponents;
using Shared.Plugin;

namespace Shared.Patches;

// Fixes a laser antenna dropping its link, permanent flag included, when someone other than
// its owner repairs it.
//
// MySlimBlock.IncreaseMountLevel calls FatBlock.OnIntegrityChanged(setOwnership: true, welder)
// whenever the welder is not the block's owner: a faction mate with a hand welder, a ship
// welder owned by someone else, or a repair mod going through IMySlimBlock.IncreaseMountLevel.
// Welders only work on blocks that are damaged or unfinished, so this happens on a repair.
// MyCubeBlock.OnIntegrityChanged hands an ownerless block to the welder in that case and does
// nothing to a block that has an owner. MyLaserAntenna's override, however, clears the GPS
// target and sends the antenna to idle on every call. Going idle also idles the partner
// (IdleOther) and clears the permanent flag on both, so the link never comes back by itself.
//
// Present on Windows: platform independent game code, settled against decompiled 1.210.014.
// Reproduced on a dedicated server (DIS-0004): a permanent link, the antenna damaged by 300 HP
// and then welded by a player who does not own it went to idle on both ends for good.
//
// The prefix skips the call when it is the ownership hand-over for a block that already has an
// owner, which is exactly the case where the base method does nothing. Every other call - a
// block losing or regaining function through damage, grinding or construction, or an ownerless
// antenna being claimed - still idles the antenna as before.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyLaserAntenna), "OnIntegrityChanged")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class LaserAntennaRepairWeldPatch
{
    private static bool Prefix(MyLaserAntenna __instance, bool setOwnership)
    {
        if (Common.Config?.Enabled != true || !setOwnership || __instance.OwnerId == 0)
            return true;

        // The base method also hands over a block whose ownership component has no owner
        var ownership = __instance.Components.Get<MyEntityOwnershipComponent>();
        return ownership != null && ownership.OwnerId == 0;
    }
}
