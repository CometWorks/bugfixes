# Crash when a rocket hits a character whose identity was removed

**Category:** Client + Server
**Patch:** `Shared/Patches/CharacterMissileIdentityPatch.cs`

## The bug

A Torch server went down with this:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Game.Entities.Character.MyCharacter.RigidBody_ContactPointCallback(HkContactPointEvent& value)
   at Havok.HkContactListener.OnContact(IntPtr handle, IntPtr contactPointEvent)
   at Havok.HkWorld.HkWorld_FinishMtStep(IntPtr world, IntPtr jobQueue, IntPtr threadPool)
   at Sandbox.Engine.Physics.MyPhysics.StepWorlds()
```

`MyCharacter.RigidBody_ContactPointCallback` handles the contacts of a living
character. On the server, when a missile touches the character hard enough to
hurt it and friendly fire is on (the default), it asks the missile whether its
owner is friendly to the character:

```csharp
if (!myMissile.IsCharacterIdFriendly(GetIdentity().IdentityId))
```

`GetIdentity` returns the controlling player's identity, or else the identity
whose id is the character's owner. It returns null when no player controls the
character and that identity no longer exists. `MyPlayerCollection.RemoveIdentity`
only unlinks the identity's character, it doesn't close it. The admin screen
and the trash collector call it, and so can plugins. The
character left behind is still a normal target, and the first rocket that hits
it throws here. The callback runs inside the Havok step, so nothing catches the
exception and the game or server stops.

None of the other values the callback reads without a check can be null in
the game's own code: every rigid body that reaches the callback carries a
`MyPhysicsBody` with an entity, and a grid's physics is closed, which takes
its body out of the Havok world, before it is set to null.

## The fix

A transpiler replaces `GetIdentity().IdentityId` in that callback with a call
that returns the identity's id as before, or the character's owner identity id
when the identity is gone. That is the id `GetIdentity` looked up, so the
friendly fire check runs with the id the character still carries, and the
rocket does its damage. Each such character is logged once at info
level. Contacts with an existing identity run unchanged.

## Testing

A test plugin (workspace note [#650](https://tasks.cometworks.se/tasks/650))
runs five scenarios once a world is loaded, each with a fresh character: a
character flying into a static slab, a dynamic armor block hitting a character
at 40 m/s, a character getting on a ladder, a rocket hitting a character with
an identity, and a rocket hitting a character whose identity was just removed.
It logs every damage the characters take and every `NullReferenceException`
thrown in a contact point callback, with its IL offset.

On Linux (game 1.210.014, 2026-10-15), both on a headless Pulsar 2.4.3 client
in an offline survival world and on a Magnetar 2.4.3.3 dedicated server:

- Without Bugfixes the last scenario threw at IL offset `0x0614`, the start of
  the friendly fire statement, and the process stopped with an unhandled
  `NullReferenceException` in `RigidBody_ContactPointCallback`.
- With Bugfixes the same rocket killed the character and the log showed
  `A missile hit character ProbeMissileOrphan (...) whose identity ... does not exist, using that id for the friendly fire check`.
- The other four scenarios gave the same results with and without the fix: no
  damage from the landing, four 1811.5 environment damage events from the
  block, the character staying on the ladder, and the same rocket damage
  events from the missile. The orphan character took the same rocket damage
  as the one with an identity.
- With the Performance plugin on the server too, whose transpiler also patches
  this callback, the run without Bugfixes stopped with the same trace as the
  Torch report, `RigidBody_ContactPointCallback_Patch1`. With both plugins
  the results matched the runs above.

Not tested on Windows itself.
