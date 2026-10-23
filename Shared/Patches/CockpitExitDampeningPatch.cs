using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Shared.Plugin;
using VRage.Game.Entity;

namespace Shared.Patches;

// Client + Server
//
// Keeps a pilot leaving a fast ship from crashing the server when the pilot is put back out of
// relative dampening range of the ship. See Docs/cockpit-exit-dampening.md.
//
// MyCockpit.AttachPilot stores the pilot's world matrix relative to the seat, from wherever the
// character was when it boarded, and RemovePilot puts the character back there. The server does
// not check how far that is: a modified or bot client can board from hundreds of meters away.
// When the ship moves faster than 10 m/s, RemovePilot then sets the ship as the pilot's relative
// dampening entity and tells the clients its EntityId. The RelativeDampeningEntity setter stores
// null when the character is out of MyEntityThrustComponent.IsInRangeOfRelativeDampening range,
// so reading the EntityId throws a NullReferenceException halfway through RemovePilot. From the
// pilot's own exit the server disconnects that client and the body stays seated. From the trash
// collector removing the abandoned character (MySessionComponentTrash.RemoveCharacter) the server
// crashes, again after every restart, since the seated body is saved (DF-684).
//
// The transpiler reads the EntityId through a method that returns 0 for a null entity, which
// tells the clients the character has no dampening entity. That matches the server's own state,
// and RemovePilot finishes as for any other exit.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyCockpit), nameof(MyCockpit.RemovePilot))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class CockpitExitDampeningPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var getDampeningEntity = AccessTools.PropertyGetter(
            typeof(MyCharacter),
            nameof(MyCharacter.RelativeDampeningEntity)
        );
        var getEntityId = AccessTools.PropertyGetter(typeof(MyEntity), nameof(MyEntity.EntityId));
        var il = instructions.ToList();
        var replaced = 0;
        for (var i = 0; i < il.Count - 1; i++)
        {
            if (!il[i].Calls(getDampeningEntity) || !il[i + 1].Calls(getEntityId))
                continue;

            il[i].opcode = OpCodes.Call;
            il[i].operand = AccessTools.Method(
                typeof(CockpitExitDampeningPatch),
                nameof(DampeningEntityId)
            );
            il.RemoveAt(i + 1);
            replaced++;
        }

        if (replaced != 1)
            Common.Logger.Warning(
                "CockpitExitDampeningPatch: expected one RelativeDampeningEntity.EntityId in MyCockpit.RemovePilot, replaced {0}",
                replaced
            );

        return il;
    }

    private static long DampeningEntityId(MyCharacter pilot)
    {
        var entity = pilot.RelativeDampeningEntity;
        if (entity != null || Common.Config?.Enabled != true)
            return entity.EntityId; // Disabled: throws like the game does

        Common.Logger.Info(
            "Pilot {0} ({1}) left a cockpit out of relative dampening range of the ship, sending no dampening entity",
            pilot.DisplayName,
            pilot.EntityId
        );
        return 0;
    }
}
