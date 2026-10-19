# Crash after a clipboard or projector preview grid fails to load

**Category:** Client + Server
**Patch:** `Shared/Patches/ClipboardPreviewFailedGridPatch.cs`

## The bug

When a grid of a blueprint fails to initialize in the clipboard or projector
preview ("ERROR Entity init" in the log), the game crashes as soon as it
simulates again, if the player sits in a cockpit:

```
Exception: System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Game.EntityComponents.MyTargetFocusComponent.IsTargetInRange(MatrixD shooterMatrix, MyEntity target, Double& targetCosSquaredSigned, Boolean checkAngle)
   at Sandbox.Game.EntityComponents.MyTargetLockingComponent.CanTarget(IMyTargetingCapableBlock controlledBlock, MyEntity target, Boolean checkAngle)
   at Sandbox.Game.EntityComponents.MyTargetLockingComponent.UpdateBeforeSimulation()
```

`MyGridClipboard.ChangeClipboardPreview` builds the preview grids of the
creative clipboard and of every projector, on a worker thread. It creates each
grid, keeps it in a local and then initializes it with `MyEntities.InitEntity`.
When a block's `Init` throws, `InitEntity` logs the error, sets the grid's
`EntityId` to 0 and clears its own reference, but `ChangeClipboardPreview`
carries on with its local and puts the half initialized grid into the preview.
Back on the main thread, the activation callback adds every preview grid to the
world. `MyEntities.Add` registers an entity whose id is not taken yet, so the
broken grid is registered as entity 0.

The game uses id 0 for "nothing". A character with no locked target has target
id 0, and `MyTargetLockingComponent.TargetEntity` looks that id up, so it gets
the broken grid. While the character sits in a cockpit with target locking on,
which is the default, `UpdateBeforeSimulation` checks whether that "target" is
in range and reads its `Physics`, which a preview grid doesn't have.

A player hit it in single player, pasting a rover from a projector after a
conveyor sorter of the rover failed to initialize (DIS-0033, see
[the sorter fix](conveyor-sorter-controls.md)). Their game was paused at the
time, so it crashed when they unpaused it.

## The fix

A postfix on `ChangeClipboardPreview` drops the preview grids that failed to
initialize, which are the ones with entity id 0, and hides the whole preview.
That is how the method itself handles a copied grid that comes out with no
blocks. A projector shows its "cannot find blueprint" message then, and the
creative clipboard shows nothing. Each time, an info line gives the number of
grids that failed; the log has the `ERROR Entity init` with the reason above
it.

## Testing

On Linux (game 1.210.014, Pulsar 2.4.3 Interim, headless offline client,
2026-10-19). A test plugin seats the player in a cockpit and previews a small
grid with a conveyor sorter whose `Init` the plugin makes throw, standing in for
any block that fails to load.

- Without Bugfixes, the game crashed in
  `MyTargetFocusComponent.IsTargetInRange` on the first simulation step after
  the preview was added. The projector previews of the sorter fix test crashed
  the same way.
- With Bugfixes, the preview was hidden with the info line, entity id 0 stayed
  free, and the game kept running with the player still in the cockpit.

Only the creative clipboard was tested with the failing block; a projector
builds its preview with the same method, but its "cannot find blueprint"
message was not checked.

The rig is in the workspace, `testing/windows-test-kits/tm-1155-paste-sorter-ctd`.
Not tested on Windows or on a dedicated server.
