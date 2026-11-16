# Crash when a conveyor tube without a conveyor line updates

**Category:** Client + Server
**Patch:** `Shared/Patches/ConveyorTubeWithoutLinePatch.cs`

## The bug

A client crashed to desktop while joining a dedicated server, once a grid with a damaged conveyor
tube streamed in:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Game.Entities.MyConveyorConnector.UpdateOnceBeforeFrame()
   at Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator.DispatchOnceBeforeFrame()
   at Sandbox.Game.Entities.MyEntities.UpdateOnceBeforeFrame()
```

A conveyor tube (`MyConveyorConnector`) joins a conveyor line in `InitializeConveyorSegment`, and
only if `MyConveyorLine.GetBlockLinePositions` finds conveyor port dummies (`detector_conveyor...`)
in its model. Without them the segment's `ConveyorLine` stays null, and `UpdateOnceBeforeFrame`
calls `ConveyorLine.UpdateIsWorking()` without a check. A tube damaged below its critical integrity
schedules that update when its grid loads.

Every vanilla tube model has the dummies. They go missing when the model failed to load and was
replaced by `Error.mwm`, which is what happened in the report (SE-1587, see
[piston-attach-without-subparts.md](piston-attach-without-subparts.md)), or with a mod's tube model
that lacks them. The positions are cached per block definition, so after one such load every tube
of that type in the session has no line.

## The fix

A transpiler on `MyConveyorConnector.UpdateOnceBeforeFrame` returns right after the base call when
the segment has no line. Such a tube does nothing, as it has no line to carry items or power
anyway. Tubes with a line run as before.

## Testing

`tests/lost-linux-patches/rig.py` runs an isolated headless Pulsar Interim client on Linux that
loads a world offline with a probe plugin. The world is the game's Empty World with a static station
holding a conveyor tube damaged to 10 % and, except in `tube`, a large piston base with its top on a
second grid. The probe removes LinuxCompat's Harmony patches the way a lost detour leaves them:

- `files`: from `MyFileSystem.FileExists`, so block models whose path casing differs from the disk
  load as `Error.mwm`, as they did for the reporter.
- `tube`: as `files`, with only the tube on the station, so the piston can't crash first.
- `input`: from `MyVRagePlatform.get_Input2` (the LinuxCompat side of SE-1587).

Pass means the world ran 20 s with no `NullReferenceException` in the game log. Results on Linux,
LinuxCompat 1.0.39 from the hub:

| Scenario | Bugfixes 1.21.0 (hub) | This branch |
|----------|-----------------------|-------------|
| `files`  | NRE in `MyPistonBase.GetTopMatrixLocal` (reported stack) | world ran 20 s |
| `tube`   | NRE in `MyConveyorConnector.UpdateOnceBeforeFrame` (reported stack) | world ran 20 s |

A piston base built to 50 % with its top attached loaded without a crash on vanilla, so the
construction stage models are not affected.
