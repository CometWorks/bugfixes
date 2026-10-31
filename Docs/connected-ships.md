# Connected ships pushing against each other

**Category:** Client + Server
**Patch:** `Shared/Patches/ConnectedShipsPatch.cs`
**Setting:** `CalmConnectedShips` in `Bugfixes.cfg`, on by default

## The bug

Two ships locked together with a connector are one rigid body to the physics, but each keeps its own
dampeners and gyros. A pilot's input only reaches the thrusters and gyros of the grid with the cockpit.
The other ships see the group speed up or turn, and their dampeners and gyros work against it, with no
idea that this is what the pilot wants.

Most of what the pilot's ship puts out is lost that way. With two equal ships, the pushing ship's force
and the other ship's dampeners add up to 12 times the force that is left to move the group, and the
group reaches 1.2 m/s after 5 s where it should reach 6.4 m/s.

A group that is only stopping has no such problem. The game already shares the group's mass between the
ships with dampeners (`MyThrusterBlockThrustComponent.CalculateMass`), and the ships' forces point the
same way.

## The fix

Once a second, and at once when a connector locks or unlocks, the server works out the rule for each set
of ships joined by locked connectors:

- A ship is a grid with everything on rotors, pistons and hinges (`MyCubeGridGroups.Mechanical`).
- A ship being flown, by a pilot or an autopilot, stays as it is.
- Of the rest, the ship with the strongest thrusters keeps its dampeners. Strongest means the maximum
  thrust added up over the six directions, for the thrusters that work, of all its grids. Ships with
  dampeners turned off aren't considered. A tie goes to the ship being flown or pushed, then to the
  lowest entity id.
- The gyros follow the same rule on their own: the ship with the most gyro force keeps its gyros
  slowing the group down. A ship with strong thrusters and no gyros would otherwise leave the group with
  none.
- All other ships go quiet: their dampeners and gyros don't slow the group down.

The ship keeping its dampeners takes over the others' mass through the game's own `CalculateMass`.

Three cases keep every ship on:

- **At rest.** Nobody pushes and the group moves slower than 1 m/s and 0.05 rad/s. A ship without
  dampeners keeps a rest velocity in the solver, 0.1 m/s while the bodies stay 2 mm apart, which kept an
  equal pair awake for 100 s instead of 15 s.
- **In gravity, if the strongest ship can't carry the group.** Dampeners also hold a ship up. In natural
  gravity a ship goes quiet only if a grid of the ship keeping its dampeners can hold the whole group with
  50% to spare, by the game's own check (`ComputeResidualMass`).
- **Held by a static grid.** The game gives such ships fixed bodies, which dampeners and gyros can't move
  (`MyCubeGrid.CheckEarlyDeactivation`). That is also why nothing here touches ships docked at a station.

Only slowing down stops. Control input, thruster override and gyro override keep working on a quiet ship.

### Nothing to restore

No setting changes. The ship's `DampenersEnabled` stays what the player or script set, is saved as such
and reads the same for scripts. The patch only changes how `ComputeBaseThrust` and `CalculateMass` read it
and skips the slowdown in `MyGridGyroSystem.UpdateGyros`. Both sets live in memory and are rebuilt from
what is connected and flown. After a disconnect, save and load, crash or cluster handover there is
nothing to restore, and nothing a player set by hand to lose.

### Multiplayer

The rule runs where the physics runs, so on the server, in an offline game and on the host of a game. A
multiplayer client does nothing: the server owns a connected ship's physics, since the game turns off
client prediction for a ship in a connector constraint (`MyCubeGrid.UpdatePredictionFlag`). So a
client without the plugin gets the same ship, though it may show flames for dampeners the server isn't
applying.

### A pilot in a weak ship

A weak ship being flown still pushes against the strongest ship, which keeps its dampeners by the rule. The
group behaves as in the vanilla game then, minus the other ships. To leave only the pilot's dampeners on,
drop the strongest ship from `Choose` when a ship in the group is flown.

## Tests

`tests/connected-ships` holds the rig: an isolated Magnetar server with no client, a probe plugin driven
through files and a real headless client for `mp_check.py`.

- `test_connected.py`: the rule, the ship's settings staying as set, undocking, a static grid, gravity
  on a planet, crash, clean restart, the setting. Prints PASS or FAIL per check.
- `mp_check.py`: a client over DirectTransport seated in the ships, flying with real input.
- `fight.py`: the numbers below, with and without Bugfixes.

Measured on Linux, game 1.210.014, on a server with no client. The first ship pushes or turns for 5 s, and
the group stops from 20 m/s and 0.2 rad/s. The ratio is the sum of the ships' forces over what's left of
them, so 1 means nobody fights. Each cell is vanilla, then with the fix.

| Ships | Speed after push (m/s) | Force ratio | Spin after turn (rad/s) | Torque ratio | Stop (s) |
|---|---|---|---|---|---|
| Two equal | 1.22, 6.41 | 11.95, 1.00 | 1.37, 2.17 | 2.45, 1.04 | 11.9, 17.2 |
| Big and small | 1.79, 4.33 | 4.33, 1.06 | 2.12, 2.26 | 1.14, 1.00 | 8.7, 8.6 |
| Chain of three | 0.55, 2.85 | 10.92, 1.09 | 0.63, 0.70 | 1.26, 1.00 | 34.3, 36.6 |
| Strongest has no gyros | 1.66, 4.95 | 5.60, 1.01 | none, none | | no stop in 60 s, both |

Stopping takes a bit longer with the fix when the ships are about equal, since fewer dampeners do the work
until the group is under 1 m/s. In the last row the ships use 60% less thrust on the way, 2.5e7 N s against
6.4e7 N s, but neither version gets the last 0.1 m/s out of the group.

With a real client flying the strongest of three ships, the group reaches 0.76 m/s after 5 s in vanilla and
3.63 m/s with the fix.
