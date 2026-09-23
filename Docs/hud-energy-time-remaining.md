# HUD energy-time stat dereferencing a missing resource distributor

**Category:** Client only
**Patch:** `ClientPlugin/Patches/HudEnergyTimeRemainingStatPatch.cs`
**Pull request:** [#3](https://github.com/CometWorks/bugfixes/pull/3)

## The bug

`MyStatControlledEntityEnergyEstimatedTimeRemaining.Update` walks
`MySession.Static.ControlledGrid.GridSystems.ResourceDistributor` with only
`ControlledGrid` null-checked. The `NullReferenceException` lands on the
update thread and takes the client down.

`ResourceDistributor` belongs to a logical group, not to the grid.
`MyCubeGridSystems`' constructor never assigns it; `OnAddedToGroup` takes it
from the group and `OnRemovedFromGroup` nulls it again, while `GridSystems`
itself is assigned once and never nulled. "`GridSystems` non-null,
`ResourceDistributor` null" is therefore the designed shape of a grid outside
a logical group, not a startup artifact. The window opens at
`OnRemovedFromScene` → `RemoveNode(Logical)` → `OnRemovedFromGroup` and
closes at `OnAddedToScene` → `AddNode` → `AcquireGroup` → `OnAddedToGroup`.
`MySession.ControlledGrid` is computed live from `ControlledEntity.CubeGrid`
with no caching, so it keeps returning the grid across that window.

The driver, `MyHud.UpdateBeforeSimulation` → `m_Stats.Update`, is gated only
on `!Sync.IsDedicated`. It is a simulation-phase update, not a draw call,
with no platform branch in `MyHud.cs`, so a rendering Windows client runs it
on the same schedule as a headless Linux one. Vanilla guards this same null
in three other places, one of them `MyStatControlledEntityPowerUsage`, which
reads the same field off the same controlled entity in the same stats sweep.
This stat is the only one that omits the check, which makes it a vanilla
inconsistency rather than a platform or loader artifact. Settled against
decompiled 1.210.014.

How often a player hits the window is another question: taking control
programmatically, right after replication, reaches it far more often than
walking to a cockpit does. Closing the controlled grid does not: the game
clears the controller before the systems lose their distributor, checked by
instrumenting the prefix and closing a grid from under a seated character.

## The fix

A prefix skips the stat for that frame when the distributor is missing, so
the previous value stands until it appears; the game recomputes it on the
next update.

The patch lives in `ClientPlugin`, not `Shared`: the type name is present in
the dedicated server build so a patch would resolve, but a dedicated server
has no HUD and never runs that update.

The fix was first written in the Remote plugin (SE1-0010) and ported here,
where a vanilla game bug belongs. Remote has dropped its copy, so the two
never patch the same method in one process.

## Testing

Run in game under Pulsar (headless Linux client, `Earth Rover Test`):

- The Harmony IL dump shows the prefix installed on
  `MyStatControlledEntityEnergyEstimatedTimeRemaining::Update()`, exactly
  once, and the plugin loads with no patch failure.
- With the prefix temporarily instrumented, it runs on every frame, including
  with a real controlled grid while seated in a cockpit, and never skipped the
  stat in normal play, so the guard costs nothing there.
- Behaviour is identical with the fix enabled and disabled in normal play.
