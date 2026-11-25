# Blocks missing their own terminal controls and actions when a mod adds controls early

**Category:** Client + Server
**Patch:** `Shared/Patches/EarlyModTerminalControlsPatch.cs`
**Setting:** `EarlyModTerminalControlsPatch` in `Bugfixes.cfg`, under **Blocks and conveyors** in
the client's settings dialog, on by default

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
that tracks which block types have executed their own `CreateTerminalControls`
in the current session, a prefix/finalizer on `MyTerminalControlFactory` entry
points synchronizes concurrent `MyAPIGateway.TerminalControls` calls from
parallel entity-loading threads with `m_createControlsLock` while a world loads,
and a finalizer on
`MySession.Unload` clears the initialized set even if a mod throws during
world unload:

1. For each uninitialized class in the block's inheritance chain from
   `MyTerminalBlock` down to the concrete block type, if `m_controls` already
   has an entry created by an early mod call, its mod-added controls and actions
   are stashed and the premature entry is removed from `m_controls`.
2. `block.CreateTerminalControls()` then runs normally, allowing every
   uninitialized class in the hierarchy to create its vanilla controls and
   actions (and populate any static control fields such as those on
   `MyConveyorSorter`).
3. The stashed mod controls and actions are then appended back onto each class
   and any already-initialized subclasses in `m_controls` (skipping
   `MyRadioAntenna` when propagating `MyTerminalBlock` controls, since
   `MyRadioAntenna.CreateTerminalControls` removes `MyTerminalBlock` controls
   with `RemoveBaseClass<MyRadioAntenna, MyTerminalBlock>`).
4. Even when the plugin is disabled, the initialized inheritance chain is still
   recorded so enabling the plugin mid-session does not re-create and duplicate
   base controls on subsequent block types.

## Testing

`tests/early-mod-terminal-controls/rig.py` runs an isolated headless client on
Linux (game 1.211.018, Pulsar Interim) with a probe plugin. Right after
`MySession.LoadDataComponents`, where mods run `LoadData` and before any block
exists, the probe reads the `IMyTerminalBlock` controls and the `IMyTimerBlock`
actions and adds a `ProbeButton` control to `IMyTimerBlock`. It loads a world
with a timer block, counts the timer's controls and actions, exits to the main
menu, loads the world again and counts again.

| Variant | Timer controls | Timer actions | `IMyTerminalBlock` controls / actions |
|---|---|---|---|
| `control`, no Bugfixes | 1 (only `ProbeButton`) | 0 | 0 / 0 |
| `off`, `EarlyModTerminalControlsPatch` false | 1 (only `ProbeButton`) | 0 | 0 / 0 |
| `fix` | 35 (vanilla ones and `ProbeButton`, each once) | 20 | 6 / 3 |

The counts were the same on both loads of each run.

`tests/early-mod-terminal-controls/mp.py` runs the same probe on a Magnetar
2.4.3.9 server and on a headless client that joins it over DirectTransport,
leaves to the main menu and joins again. Both sides touch the controls in
`LoadData` before any block exists.

| Variant | Server | Client, first join | Client, second join |
|---|---|---|---|
| `control`, no Bugfixes | 1 control, 0 actions | 1 control, 0 actions | 1 control, 0 actions |
| `fix`, Bugfixes on both | 35 controls, 20 actions | 35 controls, 20 actions | 35 controls, 20 actions |

In every `fix` run, offline and multiplayer, the game log shows the factory lock
put on when the world starts loading and taken off once it is ready, once per
load. The lock is a separate Harmony patch: it is put on in `MySession.OnLoading`
and removed when the session is ready, so the script calls that reach the
factory every tick after the load don't go through it.

Earlier tests by the original author:

On Windows (game 1.210.014, Pulsar 2.4.3 Legacy), with a test session component
that calls `MyAPIGateway.TerminalControls.GetControls<IMyTerminalBlock>` and
`GetControls<IMyTimerBlock>` in `LoadData` before any world entities load, and
then initializes a `MyTimerBlock`:

- With Bugfixes disabled, after the timer block initializes, `IMyTimerBlock` has
  0 controls and 0 actions (and `IMyTerminalBlock` has 0 controls and 0 actions).
- With the fix enabled, after the timer block initializes on the first world
  load, `IMyTimerBlock` has all 34 vanilla controls (`OnOff`, separator,
  `ShowInTerminal`, `ShowInInventory`, `ShowInToolbarConfig`, `Name`,
  `ShowOnHUD`, `CustomData`, the 20 multi-panel LCD controls from
  `MyFunctionalBlock`, `Silent`, `TriggerDelay`, `OpenToolbar`, `TriggerNow`,
  `Start`, `Stop`) and all 19 vanilla actions, and `IMyTerminalBlock` has all 6
  vanilla controls and 3 vanilla actions.
- After exiting to the menu (`MySession.Unload`) and loading the world a second
  time in the same process (with `LoadData` again touching `IMyTerminalBlock`
  and `IMyTimerBlock` before any block entity loads), `SessionUnloadPatch`
  clears `InitializedTypes` and the timer block again has all 34 controls and 19
  actions (and `IMyTerminalBlock` has all 6 controls and 3 actions).
- Toggling `Enabled` from `false` to `true` mid-session after one block type has
  initialized and before a sibling block type initializes preserves the 6
  `MyTerminalBlock` controls without duplicating them.
- Also tested in a live multiplayer session on a world with 39 Workshop mods
  where multiple mods (`Alarm Beacon`, `Work From Home`, `Lighted Conveyor Tubes`,
  `Gas Purge Valve`, `Atlas Mapping System`, `Dock & Charge`, `Ground Truth`)
  access `MyAPIGateway.TerminalControls` on `IMyTerminalBlock` and concrete block
  interfaces before entities stream in on joining clients: without the fix, a
  joining client could not see the `Name` field on blocks or the `Setup Actions`
  button on timers and event controllers; with the fix enabled, all vanilla and
  mod controls and actions appeared and worked normally throughout the session.
