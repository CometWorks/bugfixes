# Blocks missing their own terminal controls and actions when a mod adds controls early

**Category:** Client + Server
**Patch:** `Shared/Patches/EarlyModTerminalControlsPatch.cs`

## The bug

Blocks in the Control Panel lack their own terminal controls and toolbar actions
when a mod accesses `MyAPIGateway.TerminalControls` for that block type or one
of its base classes before the first block of that type has been initialized:

- When a mod adds a control or action to `IMyTerminalBlock` or
  `IMyFunctionalBlock` in `MySessionComponentBase.LoadData` or `BeforeStart`, or
  on a joining multiplayer client before entities stream in, blocks across the
  world lose their shared base controls (`Name` textbox, `On/Off` toggle,
  `Show block in terminal`, `Show on HUD`, `Custom Data`).
- When a mod accesses `MyAPIGateway.TerminalControls` early for a specific block
  type (such as `IMyTimerBlock`, `IMyEventControllerBlock`, `IMyAirVent`,
  `IMyConveyorSorter`, `IMyShipConnector` or `IMyRadioAntenna`), that block type
  loses its own controls and toolbar actions (such as the `Setup Actions` button
  on timers, event controllers and air vents, or the filter controls on conveyor
  sorters; see also [the conveyor sorter controls fix](conveyor-sorter-controls.md)).

Each `MyTerminalBlock` class creates its own controls in its `protected virtual`
`CreateTerminalControls` method, called from
`MyTerminalBlock.BeforeGameLogicInit` when the first entity of that type is
initialized. Every `CreateTerminalControls` override starts with:

```csharp
if (MyTerminalControlFactory.AreControlsCreated<MySomeBlock>())
    return;
base.CreateTerminalControls();
```

`AreControlsCreated` checks whether `MyTerminalControlFactory.m_controls`
already contains an entry for the block type. When a mod calls
`MyAPIGateway.TerminalControls.AddControl`, `GetControls`, `AddAction` or
`GetActions` before any entity of that type has run `BeforeGameLogicInit`,
`MyTerminalControlFactory.GetList` calls `InitializeControls(type)` and inserts
`type` into `m_controls`.

`MyTerminalControls.CreateControl` and `MyTerminalControlFactory.AddBaseClass`
both attempt to create the block's own controls first by calling
`MyTerminalControlFactory.EnsureControlsAreCreated(type)` (or the same reflection
lookup in `AddBaseClass`), which searches for:

```csharp
blockType.GetMethod("CreateTerminalControls", BindingFlags.Static | BindingFlags.NonPublic)
```

Because `CreateTerminalControls` was changed from a `static` method to an
instance `protected virtual` method on `MyTerminalBlock` (and several overrides
such as `MyFunctionalBlock`, `MyLargeTurretBase`, `MyOffensiveCombatBlock`,
`MySearchlight` and `MyTurretControlBlock` access instance state on `this`),
that reflection lookup always returns `null`. The premature `m_controls` entry
then causes `AreControlsCreated` to return `true` when the first block entity
later runs `BeforeGameLogicInit`, skipping the block's `CreateTerminalControls`
for the rest of the session.

## The fix

A transpiler on `MyTerminalBlock.BeforeGameLogicInit` routes its
`CreateTerminalControls` call (inside `m_createControlsLock`) through a helper
that tracks which block types have actually executed their own
`CreateTerminalControls` in the current session:

1. For each uninitialized class in the block's inheritance chain from
   `MyTerminalBlock` down to the concrete block type, if `m_controls` already
   has an entry created by an early mod call, its mod-added controls and actions
   are stashed and the premature entry is removed from `m_controls`.
2. `block.CreateTerminalControls()` then runs normally, allowing every
   uninitialized class in the hierarchy to create its vanilla controls and
   actions (and populate any static control fields such as those on
   `MyConveyorSorter`).
3. The stashed mod controls and actions are then appended back onto each class
   and any already-initialized subclasses in `m_controls`.

## Testing

Tested on Windows (game 1.210.014, Pulsar Legacy):

- Unit harness loading `Sandbox.Game.dll`, `SpaceEngineers.Game.dll` and
  `0Harmony.dll`: simulated mods calling `MyTerminalControlFactory.AddControl`
  on `MyTerminalBlock`, `MyFunctionalBlock` and `MyConveyorSorter` before any
  block entity initialized. Verified that without the patch `MyTerminalBlock`
  had only the single mod control (missing all 6 vanilla controls including
  `Name` and `CustomData`), `MyFunctionalBlock` missed `OnOff`, and
  `MyConveyorSorter.drainAll` stayed `null`. With the patch applied,
  `MyTerminalBlock` created all 6 vanilla controls plus the mod control (7
  total), `MyFunctionalBlock` created `OnOff` plus all inherited and mod
  controls (9 total), and `MyConveyorSorter` populated its static `drainAll`
  control and merged all inherited and sorter-specific mod controls.
- Verified session unload reset when `MyTerminalControlFactory.Unload()` clears
  `m_controls`.
- Verified in a multiplayer world with 39 Workshop mods where multiple mods
  (`Alarm Beacon`, `Work From Home`, `Lighted Conveyor Tubes`, `Gas Purge Valve`,
  `Atlas Mapping System`, `Dock & Charge`, `Ground Truth`) register controls on
  `IMyTerminalBlock` and concrete block interfaces during `BeforeStart` before
  entities stream in on joining clients.
