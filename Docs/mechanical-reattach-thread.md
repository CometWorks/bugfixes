# Crash after joining or respawning near rotors and hinges while their grids stream in

**Category:** Client + Server
**Patch:** `Shared/Patches/MechanicalReattachThreadPatch.cs`

## The bug

A client crashed to desktop a few seconds after respawning on a multiplayer server, with rotors
and hinges near the spawn point (se-performance-improvements issue 161, game 1.211.018):

```
System.NullReferenceException
   at HashSet`1.AddIfNotPresent(T value)
   at MyCubeGrid.MechanicalConnectionBlockAttachUpdateStatusChanged(MyMechanicalConnectionBlockBase mechConBlock)
   at MyMechanicalConnectionBlockBase.UpdateAttachState()
   at MyMechanicalConnectionBlockBase.UpdateOnceBeforeFrame()
   at MyMotorStator.UpdateOnceBeforeFrame()
   at MyParallelEntityUpdateOrchestrator.DispatchOnceBeforeFrame()
```

Every grid keeps the rotors, hinges and pistons that wait to attach their top in a HashSet,
`MyCubeGrid.BlocksWaitingOnAttachUpdate`. The setters of `NeedReattach` and `UpdateAttach` add the
block to it or remove it through `MechanicalConnectionBlockAttachUpdateStatusChanged`. The update
thread does that all the time while a rotor waits for its head: `UpdateAttachState` takes the
block out, fails to find the head, and puts it back.

A client initializes the grids the server streams to it on `MyEntityCreationThread`, while the
game goes on updating. `MyAttachableTopBlockBase.Init` looks up its rotor, hinge or piston base by
`ParentEntityId`. The lookup also finds entities already in the world, so when the base's grid
arrived first, the head's `Init` calls the base's `MarkForReattach` from the creation thread. That
adds the base to its grid's set while the update thread changes the same set. A HashSet changed by
two threads at once gets corrupted, and a later `Add` throws in `AddIfNotPresent`.

The race is vanilla. In the stack trace the method shows up as `_Patch1` because the Performance
plugin's conveyor fix adds a postfix to it, but the postfix only drops conveyor caches and does not
touch the set.

## The fix

A prefix on `MyMechanicalConnectionBlockBase.MarkForReattach` lets the call through on the update
thread and queues it to the update thread (`MySandboxGame.Invoke`) from any other. The base gets
marked at the start of the next update instead. The head's grid is not in the world before the
creation thread's result is consumed, so the base could not attach it any sooner.

## Testing

[`tests/mechanical-reattach-thread/rig.py`](../tests/mechanical-reattach-thread/rig.py): one
server with 16 rotors near the spawn, one client joining it with a probe plugin that counts the
changes of the attach set made off the update thread. After the join the probe also hands copies
of the heads' grids to the creation thread for 20 s, then marks the rotors for reattach from a
worker thread for 20 s.

Results on Linux, game 1.211.018, Magnetar and a headless client with LinuxCompat, without the
Performance plugin:

| | control (no Bugfixes) | fix |
|---|---|---|
| attach set changes off the update thread after the join | 6, all on `Entity creation thread` | 0 |
| ... during 20 s of stream | 3,846 | 0 |
| ... during 20 s of hammer | 23,078 | 0 |
| attach sets consistent at the end | no: a set with `Count` 2 that enumerates 0 items | yes |
| heads attached | 16/16 | 16/16 |

In an earlier control run the join alone left a stator grid's set corrupted: enumerating it threw
`OverflowException`. The fix run passed all 12 checks.
