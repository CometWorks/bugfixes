# Bugfixes for Space Engineers

Fixes long-standing bugs may never be fixed in the vanilla game.

## Scope
- This plugin contains only bugfixes and does not add any new features.
- It only fixes game bugs which are present on Windows and not related to running the game on .NET 10 or Linux. 
- Fixes to issues caused by running the game on .NET 10 should go into the `dotnet-compat` plugin.
- Fixes to issues caused by running the game on Linux should go into the `linux-compat` plugin.

## Prerequisites

- [Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/)
- [Pulsar](https://github.com/SpaceGT/Pulsar) — plugin loader for Space Engineers (game client)
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Bugs fixed

Each fix has its own page under [`Docs`](Docs) with the bug, the fix and how it was tested.

### Client only

- [Ore markers missing on rotated asteroids](Docs/rotated-asteroid-ore.md)
- [Absolute mod model paths in the vicinity asset preload, from servers and moved saves](Docs/vicinity-model-paths.md)
- [HUD energy-time stat dereferencing a missing resource distributor](Docs/hud-energy-time-remaining.md)
- [Laser antenna copy buttons writing GPS strings the paste button rejects](Docs/laser-antenna-copy-coords.md)
- [Render thread stalls from constant mesh id hashes](Docs/mesh-index-hash.md)
- [Blocks of a moving ship drawn one step behind for a frame](Docs/actor-world-matrix-index.md)
- [Caret in the wrong place after pasting several lines](Docs/multiline-paste-caret.md)
- [Jagged edges on block label text seen close up](Docs/text-atlas-edges.md)
- [Seat interior drawn over the outside of a cockpit or cryo chamber after its grid is hidden and shown](Docs/cockpit-interior-visibility.md)
- [Render thread crash in the LCD offscreen renderer when a render message is reused too early](Docs/offscreen-render-message-reuse.md)
- [Voice chat heard from where the speaker first spoke, not from where they are, for example sitting in a seat](Docs/voice-chat-position.md)
- [Thruster flames left lit on a ship after its pilot, another player, lost control mid-burn](Docs/remote-pilot-flames.md)
- [Crash when the "save your progress?" box, opened by closing the window while leaving a server, is answered on the main menu](Docs/save-progress-box-after-unload.md)

### Server only

- [Suited characters unable to push a resting large grid in space](Docs/character-grid-push.md)

### Client + Server

- [Crash in Havok's end-of-step contact callbacks after a listener is detached](Docs/havok-end-of-step-callbacks.md)
- [Closed voxel maps kept alive by the voxel loading wait set](Docs/voxel-clipmap-wait-set.md)
- [Laser antenna link dropped when someone else repairs the antenna](Docs/laser-antenna-repair-weld.md)
- [Clipboard preview leaving its entity creation block open](Docs/clipboard-preview-creation-block.md)
- [Debris crashing the game on an entity id that is already taken](Docs/debris-entity-id.md)
- [Blueprints with an empty construction stockpile fail to paste in a hosted world](Docs/empty-stockpile-paste.md)
- [World fails to load when a mod registers an entity with id 0](Docs/load-camera-entity-id-zero.md)
- [Mod world storage in the wrong folder when the world folder is renamed](Docs/world-save-path.md)
- [Files a mod left open staying locked after the world unloads](Docs/mod-file-handles.md)
- [Crash when a rocket hits a character whose identity was removed](Docs/character-missile-identity.md)
- [Conveyor sorters failing to load when a mod adds sorter controls early](Docs/conveyor-sorter-controls.md)
- [Crash after a clipboard or projector preview grid fails to load](Docs/clipboard-preview-failed-grid.md)
- [Crash when radiation damage is queued for a character that then leaves the world](Docs/radiation-stat-closed-character.md)
- [Programmable block scripts and mods changing VRage.Math constants like `Vector3D.Zero` for the whole game](Docs/vragemath-statics.md) (on by default, can be turned off)
- [Thruster override multiplied on every save and load when a mod sets the thrust multiplier](Docs/thrust-override-multiplier.md)
- [Crash when a pilot leaves a fast ship out of relative dampening range](Docs/cockpit-exit-dampening.md)
- [Crash when a sound block saved with a looping sound playing gets power on a server](Docs/sound-block-emitter.md)
- [Mods without a Data folder reported as failed to load](Docs/mod-without-data-folder.md)
- [Havok memory leaking from the block placement test, near projections and wheels](Docs/placement-test-shape-leak.md)
- [Crash when an event controller's block selection lists a block twice](Docs/event-controller-selected-blocks.md)
- [Crash when a grid with a merge block whose model has no merge field is added to the world](Docs/merge-block-physics.md)
- [Crash inside Havok when a physics cluster reorder removes a rigid body that is not in the world](Docs/havok-batch-removal.md)
- [Crash when a piston base without its subparts attaches its top](Docs/piston-attach-without-subparts.md)
- [Crash when a conveyor tube without a conveyor line updates](Docs/conveyor-tube-without-line.md)
- [Crash after joining or respawning near rotors and hinges while their grids stream in](Docs/mechanical-reattach-thread.md)

## Client settings

In Pulsar, open **Plugins**, double-click **Bugfixes**, and click **Settings**. Each
fix is enabled by default. Changes are saved to `Bugfixes.cfg` and take effect
after restarting the game. Existing values in that file are preserved.

If a world has a different folder name and session name, check its existing mod
data before changing the [world storage setting](Docs/world-save-path.md).

## Shared configuration

Both client and server load `Shared.Config.PluginConfig` from `Bugfixes.cfg`.
Every fix has a boolean property that defaults to `true`. Existing saved values
take precedence. `Enabled` controls the plugin as a whole.

On a dedicated server, edit the file in its data folder, then restart. For
example, `<ModFileHandlesPatch>false</ModFileHandlesPatch>` disables mod file
cleanup. Client-only properties have no effect on the server. The client dialog
shows shared and client settings, with labels and descriptions from the config
properties.

## Legal

Space Engineers is a product of Keen Software House and is not affiliated with this project.
