# Debris crashing the game on an entity id that is already taken

**Category:** Client + Server
**Patch:** `Shared/Patches/DebrisEntityIdPatch.cs`

## The bug

`MyDebris.CreateDirectedDebris` builds its pieces on a worker thread in
`MyCreateDebrisWork.DoWork`. Each piece gets a new random entity id, which goes
into that worker's own registry, and that registry is then emptied without
looking at it. `OnWorkCompleted` registers the pieces on the main thread with
`MyEntityIdentifier.AddEntityWithId`, which throws a `DuplicateIdException`
when the id is already registered. Nothing checks for that in between, so one
debris piece whose id is taken ends the game:

```
Exception: newEntity: SpaceEngineers.Game.Entities.Blocks.MyInteriorLight, oldEntity: Sandbox.Game.Entities.Debris.MyDebrisBase
VRage.DuplicateIdException: Exception of type 'VRage.DuplicateIdException' was thrown.
   at VRage.MyEntityIdentifier.AddEntityWithId(IMyEntity entity)
   at Sandbox.Game.Entities.Debris.MyDebris.MyCreateDebrisWork.OnWorkCompleted()
   at ParallelTasks.Parallel.RunCallbacks()
```

`DuplicateIdException.ToString` prints the two types the other way around:
the debris is the new entity and the interior light is the one that was
already registered.

How the debris got the light's id was not found. Entity ids come from
`MyRandom.Instance`, which is per thread and seeded with
`Environment.TickCount + ManagedThreadId`, so two threads can end up with the
same sequence of ids. On a test server all the worker threads seeded their
generators within the same millisecond, with consecutive seeds. That run had
no duplicates, but the seeding scheme does nothing to prevent them.

## The fix

A prefix on `OnWorkCompleted` checks each piece before it is registered. When
its id is taken, the piece gets a new id. Debris is local to each game
instance: it is never saved, replicated or looked up by id, so the new id
changes nothing else. The id is written to the entity's field directly,
because the `EntityId` setter would move the registration of the old id, which
belongs to the other entity. Each replacement is logged at info level.

## Testing

On a Linux Magnetar 2.4.3.3 dedicated server (game 1.210.014, 2026-10-11), a
test plugin created directed debris next to an interior light and gave the
first piece the light's entity id just before `OnWorkCompleted` ran:

- Without the fix the server stopped with the `DuplicateIdException` above.
- With the fix the piece got a new id, the log showed
  `Debris entity id 95072676583851562 was already taken, the debris got 88747208897693394 instead`,
  and the server kept running.

The same forced collision on a headless Pulsar client (Linux, offline Earth
test world) logged `Debris entity id 115740012231311358 was already taken, the
debris got 113808548214850131 instead` and the game kept running.
