# Mod world storage in the wrong folder when the world folder is renamed

**Category:** Client + Server
**Patch:** `Shared/Patches/WorldSavePathPatch.cs`
**Off by default:** turn it on with `WorldStorageInWorldFolder` in `Bugfixes.cfg`

## The bug

Mods keep per-world data with the world storage helpers of `IMyUtilities`:
`ReadFileInWorldStorage`, `WriteFileInWorldStorage`,
`FileExistsInWorldStorage`, `DeleteFileInWorldStorage`,
`ReadBinaryFileInWorldStorage` and `WriteBinaryFileInWorldStorage`. They put
the files under `<world folder>/Storage/<mod scope>`, taking the world folder
from `MySession.WorldSavePath`.

`MySession.Load` doesn't set `WorldSavePath` to the folder it loads the world
from. It sets it to `SavesPath` plus the session name from `Sandbox.sbc`, with
`:` replaced by `-`. When the world's folder has a different name, the mods
read and write a folder that doesn't belong to the world:

- a world folder renamed by hand, which admins often do on dedicated servers
  and Torch;
- a world downloaded from the Workshop: the game copies it into a folder named
  after the item's title, but names the session `(Workshop) <title>`;
- a dedicated server whose `LoadWorld` points outside its `Saves` folder.

Storage files that came with the world aren't found, everything a mod writes
goes into a folder named after the session (created on the first write), and
two worlds with the same session name share their mod data. Only "Save as"
sets `WorldSavePath` again, so it stays wrong for the whole session.
`IMySession.CurrentPath` and `ThumbPath` aren't affected: `Load` sets
`CurrentPath` to the folder it loads from.

The game builds the path from the session name for a reason. A new world
started from a template, such as `Content/CustomWorlds/Empty World` on a
dedicated server, is loaded from the template folder and saved to
`SavesPath/<session name>` once it has loaded, so that is where its storage
belongs.

## The fix

The fix is opt-in. It changes where mods keep their files for every world
whose folder name differs from its session name, so a mod would no longer find
what it wrote to the vanilla folder before, and a mod or a server tool that
expects the vanilla folder may break. With the setting off, the default, the
game uses its own path, exactly as without the plugin.

To turn it on, set `WorldStorageInWorldFolder` to `true` in `Bugfixes.cfg` and
restart the game or the server. The plugin writes that file with the defaults
on its first start, into the game's user data folder on a client
(`%AppData%\SpaceEngineers` on Windows) and into the server's data folder
(the `-path` of a Magnetar server):

```xml
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <WorldStorageInWorldFolder>true</WorldStorageInWorldFolder>
</PluginConfig>
```

Before turning it on for an existing world, move its mod data from
`Saves/<session name>/Storage` into the world's own `Storage` folder, if a mod
already wrote some there.

A transpiler on `MySession.Load` changes the value it gives `WorldSavePath`.
When the world is loaded from somewhere other than the game's `Content`
folder, the storage helpers use the folder the world was loaded from. Worlds
loaded from `Content`, the templates, keep the game's path. When the path
changes, an info line names both folders.

Not changed:

- A client joining a server has no world folder of its own.
  `MySession.LoadMultiplayer` points `CurrentPath` and `WorldSavePath` at a
  local folder named after the session, which keeps that client's mod data
  apart from other servers'.
- "Save as" moves the world to a new folder, but the game doesn't copy the
  `Storage` folder there.

## Testing

On Linux (game 1.210.014, 2026-10-15), with a Pulsar 2.4.3 Interim headless
client in an offline world and a Magnetar 2.4.3 Interim dedicated server. A test
plugin called the storage helpers through `MyAPIUtilities` (not through
LinuxCompat's wrapper for mods, so the paths are the game's own). It did so at
three points: from `MySession.BeforeLoading`, once the session was ready, and
after `IMySession.Save()`. Before the world loaded, it put files into the
world's own `Storage` folder for the read, exists and delete calls.

- World folder `Renamed World Folder`, session name `Probe Session`, without
  the fix: `WorldSavePath` was `Saves/Probe Session` at all three points, on
  the client and on the server. The read and exists calls didn't find the
  world's files, the delete call left them alone, and the writes created
  `Saves/Probe Session/Storage`. `CurrentPath` and `ThumbPath` were right.
- The same world with the fix: every call used `Renamed World Folder`, at all
  three points, on both. The info line above appeared once per load.
- A folder named after the session, with and without the fix: every call was
  right and the fix logged nothing.
- A new server world from `Content/CustomWorlds/Empty World`, named
  `Probe Session`: with and without the fix the storage was in
  `Saves/Probe Session` all along, the folder the server saves the new world
  to.

On 2026-10-18, after making the fix opt-in, the same runs again, on Linux with
the same game, Pulsar and Magnetar versions. The probe makes 9 checks at each of
the three points, 27 per run:

- `WorldStorageInWorldFolder` on: the renamed world passed all 27 checks on
  the client and on the server, with one info line per load. The matching
  folder passed all 27 and the fix logged nothing. The template world behaved
  as without the plugin.
- The setting at its default, with `Bugfixes.cfg` written by the plugin
  (`WorldStorageInWorldFolder` false): every probe line and every storage
  file was the same as without the plugin: the renamed and the matching
  folder on the client and on the server, and the template on the server.
  The renamed world got the vanilla folder, failing 21 of the 27 checks.

Not tested: Windows (Pulsar Legacy and MagnetarLegacy on .NET Framework),
Torch, a world downloaded from the Workshop itself and a real mod. Their path
goes through the same `MySession.Load` and storage helpers.
