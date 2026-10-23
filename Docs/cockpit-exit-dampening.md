# Crash when a pilot leaves a fast ship out of relative dampening range

**Category:** Client + Server
**Patch:** `Shared/Patches/CockpitExitDampeningPatch.cs`

## The bug

A Magnetar dedicated server stopped with a `NullReferenceException` in
`MyCockpit.RemovePilot`, called by the trash collector
(`MySessionComponentTrash.CheckIdentity` -> `TryRemoveAbandonedCharacter` ->
`RemoveCharacter` -> `MyCockpit.RemovePilot`) on the main thread.

`MyCockpit.AttachPilot` stores the pilot's world matrix relative to the seat,
taken from wherever the character was when it boarded, and `RemovePilot` puts
the character back at that spot. The game client only lets a player board from
next to the seat, but the server does not check the distance in
`AttachPilotEvent`, so a modified or bot client can board from hundreds of
meters away.

When the ship moves faster than 10 m/s, `RemovePilot` turns on the pilot's
jetpack dampeners, sets the ship as the pilot's relative dampening entity and
sends its `EntityId` to the clients. The `RelativeDampeningEntity` setter stores
null when the character is out of relative dampening range of the ship
(`MyEntityThrustComponent.IsInRangeOfRelativeDampening`, 100 m by default), and
reading the `EntityId` then throws halfway through `RemovePilot`:

- On the pilot's own exit, packet processing catches the exception and the
  server disconnects that client. The body stays in the seat.
- When the player has been gone for `PlayerCharacterRemovalThreshold` minutes,
  the trash collector removes the abandoned character through `RemovePilot` on
  the main thread. Nothing catches the exception and the server stops. The
  seated body is in the save, so the crash comes back after every restart.

Found on a bench server with a bot client ([DF-684](https://tasks.cometworks.se/tasks/1456)). Settled against
decompiled 1.210.014.

## The fix

A transpiler reads the dampening entity's id in `RemovePilot` through a method
that returns 0 when the entity is null, and logs that at info level. 0 tells the
clients that the character has no dampening entity, which is what the server
has too, the same as `MyPlayerCollection` sends in that case. `RemovePilot`
then finishes like any other exit: the character stands at the stored spot with
the ship's velocity and its jetpack on. A pilot that leaves within range gets the
ship as its dampening entity as before.

The fix does not move the exit spot or refuse boarding from far away; both
would change game behavior beyond the crash.

## Testing

On Linux (game 1.210.014, Magnetar dedicated server, an empty space world,
2026-10-23). A test plugin spawns a small grid with one cockpit and a
character DIST meters from it, attaches the character with
`MyCockpit.AttachPilot`, sets the grid coasting at SPEED m/s, and then removes
the pilot either with `RemovePilot` (*exit*, what the pilot's own F runs) or
with the private `MySessionComponentTrash.RemoveCharacter` (*trash*, what the
abandoned-character cleanup runs). It catches and logs any exception.

| Scenario | DIST | SPEED | Without Bugfixes | With Bugfixes |
|---|---|---|---|---|
| near, exit | 2 m | 30 m/s | exits, ship is the dampening entity | the same |
| slow far, exit | 500 m | 5 m/s | exits at 499 m, no dampening entity | the same |
| far, exit | 500 m | 30 m/s | `NullReferenceException` in `RemovePilot`, body stays seated | exits at 499 m, no dampening entity, fix logged |
| far, trash | 500 m | 30 m/s | `NullReferenceException` in `RemovePilot` from `RemoveCharacter`, body stays seated | pilot removed, character closed, fix logged |

The rig is in the CometWorks workspace on polaris, `~/work/ticket-1456`
(`rig.py`, `run-all.sh`, `probe/`, `result-*.txt`). It does not let the
exception escape to show the server exit itself; the report's bench server did
that. Not tested on Windows or on a client.
