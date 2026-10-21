# Crash when radiation damage is queued for a character that then leaves the world

**Category:** Client + Server
**Patch:** `Shared/Patches/RadiationStatClosedCharacterPatch.cs`

## The bug

A Magnetar dedicated server in a cluster load test stopped with this, and every
player on it lost their session:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at SpaceEngineers.Game.EntityComponents.GameLogic.MyRadiationStat.<DoRadiationDamage>b__12_0()
   at Sandbox.MySandboxGame.Update()
```

The game log only showed the clean shutdown that followed.

Every character has a `MyCharacterStatComponent` whose stat logic scripts include
`MyRadiationStat`. The component runs `Update100` of its scripts from
`UpdateAfterSimulationParallel`, on a worker thread. In survival,
`MyRadiationStat.DoRadiationDamage` then queues a lambda with
`MySandboxGame.Static.Invoke`, and the main thread runs it at the start of a later
`MySandboxGame.Update`. The lambda reads the radiation stat through
`m_radiationStat`, which looks the stat up in the stats dictionary that the
component shares with its scripts, and returns null when it is not there.

When the character leaves the world between the queueing and the main thread
running the lambda, through death, respawn, a closed entity or a handover to
another server in a cluster, `MyEntityStatComponent.OnBeforeRemovedFromContainer`
clears that dictionary. The lambda then dereferences null. Nothing catches an
exception out of a queued invoke, so the game or server stops.

## The fix

A prefix on the lambda skips it when the radiation stat is no longer in the
dictionary or the character is closing. There is nobody left to damage then. Each
skip is logged at info level. A character that is still in the world takes its
radiation damage as before.

## Testing

On Linux (game 1.210.014, Magnetar 2.4.2 dedicated server, an empty space world
in survival with radiation on, 2026-10-21). A test plugin spawns a character,
sets its radiation to the maximum and queues `DoRadiationDamage` in three ways:

- *alive*: nothing else happens to the character.
- *close*: the character is marked for close, and the damage is queued at the
  end of the next frame's simulation just before `MyEntities.DeleteRememberedEntities`
  closes it. That is the order of a stat update on the frame the character is
  deleted.
- *clear*: the character's stat component is removed right after the damage is
  queued.

Without Bugfixes, *close* and *clear* each stopped the server with the
`NullReferenceException` in `<DoRadiationDamage>b__12_0` from
`MySandboxGame.ProcessInvoke`, the trace of the report. *alive* took the
character from 100 to 90 health.

With Bugfixes, all three ran in one server and nothing crashed. *alive* took the
character from 100 to 90 health as before, and *close* and *clear* logged
`Skipped the radiation damage of character ...`.

The rig is in the CometWorks workspace on polaris, `~/work/ticket-1289`
(`rig.py`, `run-all.sh`, `probe/`). Not tested on Windows or on a client.
