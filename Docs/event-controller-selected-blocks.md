# Crash when an event controller's block selection lists a block twice

**Category:** Client + Server
**Patch:** `Shared/Patches/EventControllerSelectedBlocksPatch.cs`

## The bug

A client on a multiplayer server stopped with this about half a second after a
large ship streamed in (DIS-0075):

```
System.ArgumentException: An item with the same key has already been added. Key: 72057600061194487
   at System.Collections.Generic.Dictionary`2.Add(TKey key, TValue value)
   at SpaceEngineers.Game.Entities.Blocks.MyEventControllerBlock.ProcessSelectedBlocks()
   at SpaceEngineers.Game.Entities.Blocks.MyEventControllerBlock.UpdateAfterSimulation10()
```

`MyEventControllerBlock.Init` keeps the `SelectedBlocks` entity id list of its
object builder as the list of blocks still to be found. `ProcessSelectedBlocks`
runs on the 10th frame after `Init`, then every 100th frame while some blocks
are missing. It looks up each id and puts the blocks it finds into a dictionary
with `Add`, then takes the found ids off the list. Nothing checks for a key that
is already there, so two things crash it:

- **An id listed twice.** Saving can't produce one, since
  `GetObjectBuilderCubeBlock` collects the ids through a `HashSet`. But pasting
  and projecting remap ids one to one, so a blueprint or prefab that lists an
  id twice keeps the repeat, and every client and server that pastes it
  crashes.
- **An id that is already selected.** The `AddBlocks` event, which the terminal
  and plugins raise to select blocks, adds to the dictionary without taking
  the ids off the pending list. A client that has just received the grid holds
  every selected id as pending until its first pass. If an `AddBlocks`
  broadcast for one of them arrives before that pass, the pass adds it a second
  time.

The DIS-0075 client crashed on the first pass after the grid arrived, so its
list held the id twice or `AddBlocks` had just added it. The id came from the
server. On that server Multigrid Projector restores the event controller
selections of welded projections by raising `AddBlocks`, so the second case
fits, but the log can't tell the two apart.

## The fix

A prefix on `ProcessSelectedBlocks` removes the repeated ids and the ids that
are already selected from the pending list before the game walks it. Each
block ends up selected once, which is what the list meant. Removed ids are
logged at info level, with the event controller and its grid.

## Testing

On Linux (game 1.210.014, Pulsar 2.4.3 Interim, headless offline client,
2026-11-14), `tests/event-controller-selected-blocks/rig.py` pastes a blueprint
with an event controller whose `SelectedBlocks` lists a battery on the same
grid twice:

- Without Bugfixes the game crashed with the exception above on the first pass
  after the paste.
- With Bugfixes the game kept running, the event controller and the battery
  were working, and the log showed
  `EventControllerSelectedBlocksPatch: dropped 1 repeated block id(s) from the selection of event controller ... on grid SE1481 EC dup`.

The `AddBlocks` case goes through the same check on the dictionary, but was not
reproduced in a test.
