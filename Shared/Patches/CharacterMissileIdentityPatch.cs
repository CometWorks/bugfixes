using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Shared.Plugin;

namespace Shared.Patches;

// Client + Server
//
// Keeps a rocket hitting a character whose identity no longer exists from crashing the game.
// See Docs/character-missile-identity.md.
//
// When a missile touches a character, MyCharacter.RigidBody_ContactPointCallback checks on the
// server whether the missile's owner is friendly to the character, with
// GetIdentity().IdentityId. GetIdentity returns null when no player controls the character and
// the identity that owns it is gone. That happens when the identity is removed while its
// character stays in the world: the trash collector's identity cleanup, the admin screen or a
// server plugin. The callback runs inside the Havok step, so the NullReferenceException takes
// the server down (SE1-0209).
//
// The transpiler replaces that GetIdentity().IdentityId with a call that falls back to the
// character's owner identity id, which is the id GetIdentity looks up, so the friendly fire
// check runs with that id.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyCharacter), "RigidBody_ContactPointCallback")]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class CharacterMissileIdentityPatch
{
    // One missile hit runs the callback for several contact points, log each character once.
    // Havok may call it from its worker threads.
    private static readonly HashSet<long> Logged = new();

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var getIdentity = AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.GetIdentity));
        var getIdentityId = AccessTools.PropertyGetter(
            typeof(MyIdentity),
            nameof(MyIdentity.IdentityId)
        );
        var il = instructions.ToList();
        var replaced = 0;
        for (var i = 0; i < il.Count - 1; i++)
        {
            if (!il[i].Calls(getIdentity) || !il[i + 1].Calls(getIdentityId))
                continue;

            il[i].operand = AccessTools.Method(
                typeof(CharacterMissileIdentityPatch),
                nameof(IdentityId)
            );
            il.RemoveAt(i + 1);
            replaced++;
        }

        if (replaced != 1)
            Common.Logger.Warning(
                "CharacterMissileIdentityPatch: expected one GetIdentity().IdentityId in MyCharacter.RigidBody_ContactPointCallback, replaced {0}",
                replaced
            );

        return il;
    }

    private static long IdentityId(MyCharacter character)
    {
        var identity = character.GetIdentity();
        if (identity != null || Common.Config?.Enabled != true)
            return identity.IdentityId; // Disabled: throws like the game does

        var owner = character.GetPlayerIdentityId();
        bool first;
        lock (Logged)
            first = Logged.Add(character.EntityId);
        if (first)
            Common.Logger.Info(
                "A missile hit character {0} ({1}) whose identity {2} does not exist, using that id for the friendly fire check",
                character.DisplayName,
                character.EntityId,
                owner
            );
        return owner;
    }
}
