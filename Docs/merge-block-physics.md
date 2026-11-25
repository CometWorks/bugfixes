# Crash when a grid with a merge block whose model has no merge field is added to the world

**Category:** Client + Server
**Patch:** `Shared/Patches/MergeBlockPhysicsPatch.cs`

## The bug

A client crashed to desktop while joining a dedicated server, as a grid with a merge block
streamed in:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at SpaceEngineers.Game.Entities.Blocks.MyShipMergeBlock.OnAddedToScene(Object source)
   at VRage.Game.Entity.MyEntity.OnAddedToScene(Object source)
   at Sandbox.Game.Entities.MyCubeGrid.OnAddedToScene(Object source)
   at Sandbox.Game.Entities.MyEntities.Add(MyEntity entity, Boolean insertIntoScene)
   at Sandbox.Game.Entities.MyEntityCreationThread.ConsumeResult(MyTimeSpan timestamp)
```

`MyShipMergeBlock.Init` creates the block's merge field, a phantom `Physics` body, in
`LoadDummies`, and only when the block's model has a dummy whose name contains "merge". It reads
the dummies with `MyModels.GetModelOnlyDummies`. If that import fails, `MyModel.LoadOnlyDummies`
logs `Importing asset failed` and leaves the dummies empty. A mod's merge block whose model lacks
the dummy ends the same way. `Physics` stays null, and `OnAddedToScene` sets `Physics.Enabled`
without a null check. The block's own `IsWorkingChanged` handler does check it.

On a server the exception stops the world load; on a client it is an unhandled exception on the
main thread.

## The fix

A transpiler on `OnAddedToScene` sets `Physics.Enabled` through a method that skips a null
`Physics`. The rest of the method runs as before. Such a block has no merge field, so it never
merges, as if it were switched off. Nothing changes for merge blocks with a merge field.

## Testing

`tests/merge-block-physics/rig.py` makes the merge field go missing without a mod. The side under
test runs from a stand-in game install whose `Content` links to the real one, except that
`Models/Cubes/large/MergeBlock.mwm` is a copy of `MergeBlock_LOD1.mwm`, the same model without its
dummies. The world is the game's Empty World with a station holding a large merge block.

- `server`: a Magnetar server loads the world from the stand-in install.
- `join`: a server from the real install, and a headless client from the stand-in install that
  joins it over DirectTransport, respawns and teleports next to the station, so the grid comes in
  through the entity creation thread.

On Linux (game 1.210.014, Magnetar and Pulsar Interim, 2026-11-14):

| Scenario | Without Bugfixes | With Bugfixes |
|---|---|---|
| `server` | `NullReferenceException` in `MyShipMergeBlock.OnAddedToScene`, the world does not load | world ready |
| `join` | client crashes with the stack above, from `MyEntityCreationThread.ConsumeResult` | client sees the station and still runs 30 s later |

Run it with `python3 tests/merge-block-physics/rig.py server|join control|fix`; the script's
docstring lists what it needs. Not tested on Windows.
