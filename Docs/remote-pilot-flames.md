# Thruster flames left lit on a ship after its pilot, another player, lost control mid-burn

**Category:** Client only
**Patch:** `ClientPlugin/Patches/RemotePilotFlamesPatch.cs`

## The bug

A ship flown by another player shows lit thruster flames that do nothing. The
ship drifts at the speed it had and burns no hydrogen, and the flames stay on
until someone takes the controls again. Seen on the Octahedron cluster on NPC
ships (bot pilots) and player ships ([SE-1694](https://tasks.cometworks.se/tasks/2658)).

A grid's `MyEntityThrustComponent.ControlThrust` is the pilot's throttle. The
current ship controller adds the pilot's move indicator to it every frame
(`MyShipController.MoveAndRotate`) and sets it back to zero after the frame
(`MyShipController.UpdateAfterSimulation`), but only while it is the control
system's current ship controller. When the pilot's control is released,
`MyShipController.OnControlReleased` clears the throttle on the server and on
the pilot's own client only. Every other client just drops the controller
from the grid's control system. Nothing zeroes the throttle there any more,
and the thrust component keeps setting the thrusters' strength from the last
frame's value. The server has no thrust, so the ship coasts.

It happens whenever control goes away during a burn: the pilot's game crashes
or disconnects, a bot client is restarted, or on a clustered server a pilot's
control moves to another node. A pilot who lets go of the keys before leaving
the seat sends a zero throttle first, which is why it is rare in plain
multiplayer.

Settled against decompiled 1.211.017.

## The fix

A prefix on `MyEntityThrustComponent.UpdateBeforeSimulation` zeroes the
throttle of a grid that has no current ship controller, no turret in control
and no autopilot. Nothing else writes the throttle of such a grid, so this is
the value the server has. The thrust component then turns the flames off on its
own. Ships that are being flown are not touched.

## Testing

`tests/thrust-flames/rig.py control|fix` runs one Magnetar server and two
headless clients on an empty space world with a small ship: a seat, a battery
and an ion thruster on each of its six faces, dampeners on. The pilot client
sits in the seat and the observer watches from 30 m. The pilot holds each of
W, S, A, D, Space and C for 1.5 s and lets go. Both clients must show a flame
on the one thruster that pushes the ship that way, and none at all once the
ship has stopped. Last, the pilot's game closes mid-burn, and the observer
must show no flame once the ship has stopped. `control` runs the clients
without Bugfixes, `fix` with this working copy. See the script for its
environment variables.

On Linux (game 1.211.018, polaris, 2026-11-21), each variant ran 28 checks:

| Step | Without the fix | With the fix |
|------|-----------------|--------------|
| Each key, both clients: only the thruster pushing that way is lit | pass | pass |
| Stopped after each key, both clients: no flame | pass | pass |
| Pilot dropped out mid-burn, observer: no flame | **fail**, Backward thruster at full thrust | pass |

Before that, on 2026-11-19, the same drop leg on the CometWorks/cluster bench
showed the observer at 345,600 N (full) without the fix and 0 N with it,
while the server held no thrust. A handover of the ship to another node
during a burn showed correct flames without the fix.
