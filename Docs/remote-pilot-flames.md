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

On Linux (game 1.211.017, the CometWorks cluster bench on two nodes, two
headless clients, 2026-11-19), bench scenario `P2-THRUST-FLAMES` of
CometWorks/cluster: a pilot client burns forward in a small ship while an
observer client watches from 25 m, then:

- *control*: the pilot lets go;
- *handover*: the ship is handed to the other node during the burn, and the pilot
  lets go there;
- *drop*: the pilot's game is closed during the burn.

Each leg compares the node's thruster strength with the observer's
`IMyThrust.CurrentThrust` 8 s later.

| Leg | Node | Observer without the fix | Observer with the fix |
|-----|------|--------------------------|-----------------------|
| control | 0 N | 0 N | 0 N |
| handover | 0 N | 0 N | 0 N |
| drop | 0 N, no controller | 345,600 N (full) | 0 N |
