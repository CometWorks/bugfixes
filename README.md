# Bugfixes for Space Engineers

Fixes long standing bugs Keen never fixes and most likely never will. If you read this, contradict me please.

## Scope
- This plugin contains only bugfixes and does not add any new features.
- It only fixes game bugs which are present on Windows and not related to running the game on .NET 10 or Linux. 
- Fixes to issues caused by running the game on .NET 10 should go into the `dotnet-compat` plugin.
- Fixes to issues caused by running the game on Linux should go into the `linux-compat` plugin.

## Prerequisites

- [Space Engineers](https://store.steampowered.com/app/244850/Space_Engineers/)
- [Python 3.12](https://python.org) (requires 3.12 or newer)
- [Pulsar](https://github.com/SpaceGT/Pulsar) — plugin loader for Space Engineers (game client)
- [Magnetar](https://magnetar.se) — the Space Engineers server with plugin support
- [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and
  [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Bugs fixed

List the bugs fixed with the relevant PR links without further details.

- Server-absolute mod model paths in the vicinity asset preload ([#1](https://github.com/CometWorks/bugfixes/pull/1))

## Legal

Space Engineers is a product of Keen Software House and is not affiliated with this project.
