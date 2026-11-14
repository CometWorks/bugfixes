# Crash inside Havok when a physics cluster reorder removes a rigid body that is not in the world

**Category:** Client + Server
**Patch:** `Shared/Patches/HavokBatchRemovalPatch.cs`

## The bug

A client crashed to desktop at random on a busy station of a multiplayer server, with about a dozen
NPC ships moving around while a shipyard printed a large ship (SE-1534). The game log and the loader
log just ended. The core dump had one usable frame, `Havok.dll + 0x5b8b70`, and no managed frames.

In game version 1.208 that address is the entry of `hkpSimulationIsland::internalRemoveEntity`. Its
first instruction reads the entity list of the island (`mov 0x60(%rcx),%r9`), and the only caller is
`hkpWorldOperationUtil::removeEntitySI`, which passes the island the entity points to. The crash is
a read of address `0x60`: the entity had no island, so it was not in the world.

Havok has two ways to remove a rigid body:

- `HkWorld.RemoveRigidBody` calls `HkWorld_RemoveEntity`, which does nothing unless the body's world
  is this world. Removing a body twice this way is harmless, also while the world is locked.
- `HkWorld.RemoveRigidBodyBatch` appends the body to a native array, and `HkWorld.FinishBatch` passes
  the array to `hkpWorld::removeEntityBatch`. Nothing checks the entries. A body that is listed
  twice, or that left the world after it was batched, crashes at the address above.

The game batches removals when it reorders its physics clusters: `MyClusterTree.ReorderClusters`
calls `MyPhysicsBody.DeactivateBatch` on every entity it moves to another cluster, then
`HkWorld.FinishBatch` on the old world. Clusters are reordered when an entity enters the world or
moves out of its cluster, so many ships flying around one place make it frequent.

Both sequences were reproduced outside the game on the Havok library alone, and they are the only
ones of the removal sequences tried that crash, both at `Havok.dll+0x5b8b70` with the fault address
`0x60`. Which game code puts such a body into the batch is not known yet. The log line of the fix
names the entity and prints the call stack of the first three cases to find out.

## The fix

A prefix on `HkWorld.FinishBatch` compacts the remove batch in place before the native call. It
keeps each body once, and only if Havok has it in this world. A dropped body needs no removal, so
the reorder goes on as if the batch had been right. Each dropped entry is logged as a warning
(the first 20 of a session):

```
Havok batch removal: skipped the body 7F3A5C0123A0 of MyCubeGrid 'Trader 12' (123456789), it is listed twice (total 1)
```

The add batch is left alone. The crash is the same native code on Windows and Linux, on the client
and the server.

## Testing

`tests/havok-batch-removal/rig.py on|off [twice|removed]` loads a world in an offline headless
client and has the probe plugin in `tests/havok-batch-removal/probe` batch the removal of a grid's
rigid body twice, or after removing it from the world, and finish the batch. Without Bugfixes the
client must die, at `Havok.dll + 0x5b8b70` according to its core dump. With Bugfixes it must go
on, with the body out of the world once and the warning in the game log.
