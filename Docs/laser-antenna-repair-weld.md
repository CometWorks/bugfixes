# Laser antenna link dropped when someone else repairs the antenna

**Category:** Client + Server
**Patch:** `Shared/Patches/LaserAntennaRepairWeldPatch.cs`
**Pull request:** see the README

## The bug

When a welder works on a block it doesn't own, `MySlimBlock.IncreaseMountLevel`
calls `FatBlock.OnIntegrityChanged(setOwnership: true, <welder>)`. The welder
can be a faction mate with a hand welder, a ship welder owned by someone
else, or a repair mod that goes through `IMySlimBlock.IncreaseMountLevel`.
Welders only work on blocks that are damaged or unfinished, so in practice
this is a repair. `MyCubeBlock.OnIntegrityChanged` gives an ownerless block
to the welder in that case and does nothing to a block that already has an
owner.

`MyLaserAntenna` overrides it: on every call it clears the GPS target and
sends the antenna to idle. Going idle also idles the partner antenna
(`IdleOther`) and clears the permanent flag on both ends, so the link doesn't
come back. To the players the link simply breaks, and stays broken, after a
repair. That happens far more in survival than in creative.

This is platform independent game code, settled against decompiled
1.210.014. It was reported on the Discord support channel as DIS-0004.

## The fix

A prefix skips the antenna's `OnIntegrityChanged` when the call is the
ownership hand-over (`setOwnership: true`) for a block that already has an
owner. That's exactly the case where the base method does nothing. Every
other call still idles the antenna as before: losing or regaining function
through damage, grinding or construction, and an ownerless antenna being
claimed.

## Testing

On a Magnetar dedicated server on Linux, with two DirectTransport clients,
using the DIS-0004 rig (`notes/dis-0004-laser-antenna` in the workspace). A
test probe on the server damages one antenna of a permanent link by 300 HP,
then makes the game's `IncreaseMountLevel` weld it for a joined player who
doesn't own it:

- Without the plugin, both antennas went from connected to idle, the
  permanent flag was cleared and they stayed idle. Both clients showed the
  same.
- With the plugin, the weld still reached `OnIntegrityChanged`, but both
  antennas stayed connected on the server and on both clients.
- Without the plugin, 1 HP of damage on the server and 50 HP in single
  player changed nothing: the game only calls `OnIntegrityChanged` from damage
  once the block drops below its critical integrity.
