# Thruster override multiplied on every save and load when a mod sets the thrust multiplier

**Category:** Client + Server
**Patch:** `Shared/Patches/ThrustOverrideMultiplierPatch.cs`

## The bug

A thruster keeps its override as a percentage of its force. Mods can scale a
thruster's force at runtime with `IMyThrust.ThrustMultiplier`. MES does it in
`NpcThrusterLogic` for every NPC thruster it manages, for example with
`NpcAtmoThrustForceMultiply`. The multiplier is not saved, and the mod sets it
again after every load.

The saved override and the loaded override don't use the same unit:

- `MyThrust.GetObjectBuilderCubeBlock` saves `ThrustOverride` in Newtons
  *including* the multiplier: override% x `ThrustMultiplier` x `ForceMagnitude` / 100.
- `MyThrust.Init` reads it back as override% = `ThrustOverride` x 100 /
  `ForceMagnitude`, clamped to `ForceMagnitude`, *without* the multiplier.

So with a multiplier of 2, every save and load doubles the override
percentage. Once the mod sets the multiplier again, the thruster pushes twice
as hard as before the save. Repeated saves keep doubling it until the clamp at
100 %.

On a Magnetar cluster every partition handover is a save and load, so the
target node's thrusters come back with twice the override (19.68 N -> 39.36 N
saved), and the handover check refuses the import (DF #1382). Settled against
decompiled 1.210.014.

## The fix

A postfix on `MyThrust.GetObjectBuilderCubeBlock` saves the override in the unit
`Init` reads, override% x `ForceMagnitude` / 100, for a thruster whose
multiplier isn't 1. A save and load then restore the same percentage whatever
the multiplier. A thruster with multiplier 1, which is every thruster without
such a mod, is left alone, so its saved value doesn't change at all.

Saves written before the fix still load as before, multiplied once.

## Testing

On Linux, game 1.210.014, Magnetar dedicated server (rig on polaris:
`~/work/ticket-1382/rig`). A test plugin spawns a small grid with an ion
thruster, sets its multiplier and a 25 % override, copies the grid through
`GetObjectBuilder` and `CreateFromObjectBuilderAndAdd`, sets the multiplier
again on the copy the way MES does after a load, and saves the copy again:

| thruster | without Bugfixes | with Bugfixes |
| --- | --- | --- |
| multiplier 2 | override 25 % -> 50 %, saved 7200 N -> 14400 N | 25 % -> 25 %, saved 3600 N -> 3600 N |
| multiplier 1 (control) | 25 % -> 25 %, saved 3600 N -> 3600 N | the same, same saved value |

Not tested on Windows or on a client.
