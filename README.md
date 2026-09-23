# Bugfixes for Space Engineers

Fixes long standing bugs may never be fixed in the vanilla game.

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

- [Server-absolute mod model paths in the vicinity asset preload](Docs/vicinity-model-paths.md)
- [HUD energy-time stat dereferencing a missing resource distributor](Docs/hud-energy-time-remaining.md)

### Server only

None yet.

### Client + Server

- [Crash in Havok's end-of-step contact callbacks after a listener is detached](Docs/havok-end-of-step-callbacks.md)

## Legal

Space Engineers is a product of Keen Software House and is not affiliated with this project.
