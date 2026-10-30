# Crash when a sound block saved with a looping sound playing gets power on a server

**Category:** Client + Server
**Patch:** `Shared/Patches/SoundBlockEmitterPatch.cs`

## The bug

A dedicated server stops with this as soon as a grid holding such a block is spawned or a world
holding one is loaded, and the block has power:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at SpaceEngineers.Game.Entities.Blocks.MySoundBlock.UpdateLoopableSoundEmitter()
   at SpaceEngineers.Game.Entities.Blocks.MySoundBlock.UpdateBeforeSimulation()
```

`MySoundBlock.Init` restores `IsPlaying` and `IsLoopableSound` from the object builder and subscribes
the block to per frame updates, but it only creates `m_soundEmitter` when not on a dedicated server.
While the block works, `UpdateBeforeSimulation` calls `UpdateLoopableSoundEmitter`, which the game's
source marks "Never runs on DS" and which ends with `m_soundEmitter.Update()`.

A sound block started on a server never gets there, since a server has no cues and so never sees a
looping sound. It takes a saved block: the `WLYC-Oberon-Nuclear-Generating-Station` encounter of mod
2390106048 has two of them, both saved playing and looping.

## The fix

A transpiler on `UpdateLoopableSoundEmitter` updates the emitter through a method that skips a null
emitter. The rest of the method stays: the loop timer runs, and the block stops playing at the end of
its loop period, as on a client. Clients and hosted games have an emitter, so nothing changes for them.
