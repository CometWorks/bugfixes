# Crash when the save progress box is answered after leaving a server

**Category:** Client only
**Patch:** `ClientPlugin/Patches/SaveProgressBoxAfterUnloadPatch.cs`
**Setting:** `SaveProgressBoxAfterUnloadPatch`

## The bug

A player left a server to the main menu. The "Do you want to save your progress?"
box showed up on the main menu, and clicking it crashed the game:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Engine.Analytics.MySpaceAnalytics.GatherMinedStatsData()
   at Sandbox.Engine.Analytics.MySpaceAnalytics.ReportMinedStats()
   at Sandbox.MySandboxGame.OnSaveWorldMessageBoxCallback(ResultEnum callbackReturn)
   at Sandbox.Graphics.GUI.MyGuiScreenMessageBox.CallResultCallback(ResultEnum val)
   at Sandbox.Graphics.GUI.MyGuiScreenMessageBox.OnClick(ResultEnum result)
```

Vanilla game 1.211.018 on Windows, no plugins.

Closing the game window runs `MySandboxGame.Window_OnManualWindowCloseRequest`.
It queues the save progress box when `MySession.Static` is set and
`Sync.IsServer` is true. `Sync.IsServer` is also true when there is no
multiplayer object. A client leaving a server disposes its multiplayer object
near the end of `MySession.Unload`, about a second before `MySession.Static` is
cleared. A window close in that second queues the box, the unload finishes, and
the box waits on the main menu with no session behind it.

The box's callback first reports the session's analytics. `ReportMinedStats`
goes on because exiting to the main menu leaves the analytics session marked as
started, and `GatherMinedStatsData` reads `MySession.Static.AmountMined` without
a null check. The emote and network stats after it, and the save on Yes, also
read `MySession.Static` without one, so a guard on the mined stats alone would
only move the crash.

## The fix

A prefix on `MySandboxGame.OnSaveWorldMessageBoxCallback` checks for a session.
With no session there is nothing to report or save, so it does only the rest of
the callback: it ends the analytics session as a window close and exits the
game, which is what the player asked for by closing the window. It logs
`Save progress box answered with no session, exiting the game`. With a session
the callback runs as before.

The box still shows. Keeping it from being queued would mean patching the
window close handler's condition for a one-second window, and answering the box
with an exit is harmless.

## Testing

`tests/save-progress-box/rig.py control|fix [no|yes]` joins a headless client to
a local Magnetar server. The probe in `tests/save-progress-box/probe` exits to
the main menu and raises the window close request right after
`MySession.UnloadMultiplayer`, while the session is still set, the moment the
player's X click landed in. The rig then clicks the box on the main menu through
the Remote plugin.
The Remote plugin catches an exception out of a click it runs, so under the rig
the vanilla game keeps running where a player's crashes. The probe logs the
callback's exception with its stack instead, and the rig checks for that.

On Linux (game 1.211.018, Magnetar dedicated server, 2026-11-22):

| run | box on the main menu with no session | after the click |
|---|---|---|
| control (no Bugfixes), No | yes | `NullReferenceException` in `MySpaceAnalytics.GatherMinedStatsData`, the stack of the report |
| fix, No | yes | `Save progress box answered with no session, exiting the game`, the game exits, no exception |
| fix, Yes | yes | the same |

In every run the probe saw `session_set=True multiplayer=False is_server=True`
at the window close request, the state the player's close request hit. The test
client has no analytics session of its own, so the probe marks one as started,
as exiting to the menu leaves it for a player. Not tested on Windows.
