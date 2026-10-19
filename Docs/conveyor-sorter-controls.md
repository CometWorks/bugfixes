# Conveyor sorters failing to load when a mod adds sorter controls early

**Category:** Client + Server
**Patch:** `Shared/Patches/ConveyorSorterControlsPatch.cs`

## The bug

A grid with a conveyor sorter that has Drain All on fails to load, pasted or
projected from a blueprint or loaded from a save:

```
ERROR Entity init (Grid_D_Small_145 {11C1CCD8F1F8CDB})!: System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Game.Entities.MyConveyorSorter.DoChangeDrainAll()
   at Sandbox.Game.Entities.MyConveyorSorter.<.ctor>b__20_0(SyncBase x)
   at VRage.Sync.Sync`2.SetLocalValue(T newValue)
   at Sandbox.Game.Entities.MyConveyorSorter.Init(MyObjectBuilder_CubeBlock objectBuilder, MyCubeGrid cubeGrid)
```

`MyConveyorSorter` keeps its terminal controls (Drain All, the whitelist
combo box, the filter lists and their buttons) in static fields. Its
`CreateTerminalControls` fills them when the first sorter is initialized,
unless `MyTerminalControlFactory` already has an entry for the sorter type.

A mod that calls `MyAPIGateway.TerminalControls.GetControls<IMyConveyorSorter>`,
`AddControl`, `GetActions` or the like before any sorter has been initialized
creates that entry. `CreateControl` tries to create the block's own controls
first with `MyTerminalControlFactory.EnsureControlsAreCreated`, but that looks
for a static `CreateTerminalControls` method, and the sorter's is an instance
method, so it does nothing. Mods often set up their controls in a session
component's `LoadData` or `Init`, which run before the world's entities load,
so a world with such a mod gets there before its first sorter. The sorter's
static fields then stay null until the game exits, and the sorter terminal
lacks its own controls.

The sorter's sync and event handlers refresh those controls with
`UpdateVisual`:

- `DoChangeDrainAll` runs from `MyConveyorSorter.Init` when the block's
  object builder has Drain All on. The `NullReferenceException` fails the
  whole grid.
- `DoChangeBlWl`, `DoChangeListId`, `DoChangeListType` and `DoSetupFilter`
  run when a player, a programmable block script or the server changes the
  whitelist mode or the filter, and throw the same way.

A player hit it in a world with about 300 mods, pasting a rover from a
projector (DIS-0033). The failed preview grid then crashed the game, see
[the clipboard preview fix](clipboard-preview-failed-grid.md).

## The fix

A transpiler on those five handlers sends their `UpdateVisual` call through a
method that skips a control that doesn't exist. There is nothing to refresh:
the terminal can't show a control that was never created. The first time it
skips one, it logs an info line that says a mod or plugin added sorter
controls before the first sorter was initialized.

The sorter's own controls stay missing from its terminal in such a world;
that is up to the mod.

## Testing

On Linux (game 1.210.014, Pulsar 2.4.3 Interim, headless offline client,
2026-10-19), with a test plugin whose session component calls
`GetControls<IMyConveyorSorter>` before the world's entities load, as such a
mod does. The world has no sorters, so the sorter's static controls stayed
null in every run. The plugin then seats the player in a cockpit and previews
a small grid with a Drain All sorter.

- Without Bugfixes, the creative clipboard preview and a projector both logged
  the `ERROR Entity init` above and the game then crashed in
  `MyTargetFocusComponent.IsTargetInRange`, the same two exceptions as in the
  report.
- With Bugfixes, the clipboard and the projector previews loaded with Drain All
  on, the projector projected, and a creative paste created the grid with
  physics and Drain All still on. The game logged the info line once and kept
  running.

With Multigrid Projector 0.9.9 loaded as well, a projector projected a
blueprint of the test ship with a Drain All sorter added, the sorter was
built from the projection with Drain All on, and removing the blueprint left
no preview grid behind. The run logged the same steps and results as one with
Multigrid Projector but without Bugfixes and without the early sorter
controls. With the early sorter controls and without Bugfixes, the projection
crashed the game as above.

The rig is in the workspace, `testing/windows-test-kits/tm-1155-paste-sorter-ctd`.
Not tested on Windows or on a dedicated server.
