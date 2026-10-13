# Absolute mod model paths in the vicinity asset preload

**Category:** Client only
**Patches:** `ClientPlugin/Patches/MySessionVicinityModelPathPatch.cs`,
`ClientPlugin/Patches/MyLocalCacheVicinityModelPathPatch.cs`
**Pull requests:** [#1](https://github.com/CometWorks/bugfixes/pull/1),
[#5](https://github.com/CometWorks/bugfixes/pull/5),
[#16](https://github.com/CometWorks/bugfixes/pull/16)

## The bug

When a client joins a multiplayer server, the server answers the client's
vicinity-cache request with the model paths taken from its own
`MyModel.AssetName` values (`MySession.GatherVicinityInformation` →
`AddAllModels`). For vanilla blocks those are Content-relative and resolve on
any client. For **mod** blocks they are the server's absolute on-disk paths,
which name nothing on the machine receiving them.

Joining `SpiroGames-JunkYard Paradise` (Torch, instance root
`G:\Space\Season4\Pertam\Instance`):

```
Mesh asset g:\space\season4\pertam\instance\content\244850\3768506853\models\cubes\small\retrocockpit.mwm missing
Mesh asset g:\space\season4\pertam\instance\content\244850\3770888593\models\cubes\small\smallscrapbeacon.mwm missing
Mesh asset g:\space\season4\pertam\instance\content\244850\3770315903\models\cubes\small\retractablesolarpanel.mwm missing
```

Preload only: the blocks still render, because real model loading goes
through the client's own definitions. What is lost is the prewarm the
vicinity cache exists for, so modded blocks near the spawn point pop in later
than they should.

It is a game bug, not a loader or platform one. Both a vanilla Windows client
and Pulsar Interim on Windows produce the identical three failures, at
`RequestRespawn` → `MyGuiScreenMedicals` → `PreloadVicinityCache`, so it
belongs here rather than in `linux-compat` or `dotnet-compat`.

## The fix

Both sides hold the same published workshop items, so only the layout *above*
the mod folder differs: the same client that was sent
`g:\…\content\244850\3768506853\…` holds that mod at
`C:\Program Files (x86)\Steam\steamapps\workshop\content\244850\3768506853`.

A prefix on `MySession.PreloadVicinityCache` rewrites `models` and
`armorModels` in place:

- A path segment naming a mod in `MySession.Static.Mods` by `PublishedFileId`
  is replaced, together with everything above it, by this client's own folder
  for that mod, as `ModItem.GetPath()` returns it.
- The part below the mod folder must match the client's own block definition
  string, since the render model factory keys its entries by that string (only
  lowercased on Windows). A differently spelled path loads a second copy that
  no block uses, and the prewarm is lost. On Windows the definition is
  `Path.Combine(mod folder, <.sbc text>)`, and the server sends the same `.sbc`
  text, so it is kept verbatim. On Linux, `linux-compat` normalizes definition
  paths to `/`, so the server's backslashes are turned into `/` there.
- What cannot be matched that way and is still rooted names a location on
  the sender's own disk, usable only while the sender is this machine, so it
  is kept when it exists locally (hosting) and dropped otherwise. The preload
  becomes a no-op instead of a failed load.
- Content-relative vanilla paths are untouched.

Gated on `Config.Enabled`, like the other patches.

## Moved saves

A single-player world saves the same kind of list. When saving,
`MySession.GatherVicinityInformation` writes the `MyModel.AssetName` of every
model near the player into `VicinityModelsCache` and
`VicinityArmorModelsCache` in `Sandbox.sbc`, and `PrepareBaseSession` hands
them to `PreloadVicinityCache` on the next load. Mod models are saved as this
machine's absolute paths:

```
/home/<user>/.local/share/Steam/steamapps/workshop/content/244850/3777764126/Models/Cubes/large/IceFurnace.mwm
Z:\media\...\steamapps\workshop\content\244850\467170527\Models\Cubes\AzimuthBatteries\AzBatteryLarge.mwm
```

Moved to another machine, these name nothing. Moved from Linux to Windows or
Proton, they can crash the load: on the same machine Proton finds `/home/...`
through its `Z:` drive, so the mesh loads, and then `MyMeshes.GetContentPath` takes the folder
above `Models` as the texture root and `MyResourceUtils.NormalizePath` throws
`UriFormatException` on it in `new Uri(path)`. Reported as DIS-0019.

The load side above already covers this when the world's mods are matched by
id. Two changes close the rest:

- A prefix on `MyLocalCache.SaveCheckpoint` rewrites every rooted entry that
  has a path segment naming one of the world's workshop mods into
  `<publishedFileId>\<path in the mod>`, with backslashes, just before the
  checkpoint is written. Rooted entries outside the world's workshop mods are
  dropped. Content-relative vanilla entries are untouched. The load-side
  remap maps the new form onto the loading machine's mod folder like any
  other path holding the mod id.
- The load side drops a rooted path that `Uri.TryCreate` rejects even when it
  exists, so an older save with another OS's path can't reach
  `NormalizePath` when its mod isn't matched.

The save format is unchanged. A game without this plugin reads the portable
entry as a missing Content-relative mesh, logs `Mesh asset ... missing` and
goes on, as it does with the server paths of every modded multiplayer join.
It loses only the prewarm of the mod models, and its next save writes
absolute paths again.

Dedicated servers don't save the cache (`GatherVicinityInformation` returns
early there), so server worlds are not affected.

## Path forms in the vicinity cache

`VicinityModelsCache` and `VicinityArmorModelsCache` hold the `MyModel.AssetName` strings of the models near the player, unchanged. `VicinityVoxelCache` holds voxel material subtype names, which are portable. Paths that aren't rooted are resolved against the game's `Content` folder. Rooted ones are used as they are.

What gets written, and by whom:

| Who | Model | Form | Relative to | Example |
|---|---|---|---|---|
| Any client, saving | vanilla block | relative, `\` | game `Content` | `Models\Cubes\Large\Ramp.mwm` |
| Linux client (linux-compat), saving | workshop mod | absolute, `/` | | `/home/arthur/.local/share/Steam/steamapps/workshop/content/244850/3777764126/Models/Cubes/large/IceFurnace.mwm` |
| Windows or Proton client, saving | workshop mod | absolute, `\` | | `Z:\media\Samsung 970 1TB NVMe\Games\Steam Library\steamapps\workshop\content\244850\467170527\Models\Cubes\AzimuthBatteries\AzBatteryLarge.mwm` |
| Windows or Proton client, saving | legacy `.bin` workshop mod | absolute, `\` | | `Z:\...\244850\420351819\975477922448755271_legacy.bin\Models\ForceFields\LG_SC_ForceField_Ind_Corner.mwm` |
| Any client, saving | local mod | absolute | | `<user data>\Mods\<mod name>\Models\...` |
| Client with Bugfixes 1.5.0, saving | workshop mod of the world | relative, `\` | that mod's own folder, found by the published file id in the path | `3777764126\Models\Cubes\large\IceFurnace.mwm` |
| Client with Bugfixes 1.5.0, saving | any other rooted path (local mod, mod not in the world) | dropped | | |
| Dedicated server | any | nothing saved, `GatherVicinityInformation` returns early on a DS | | |
| Dedicated server, answering a joining client | vanilla block | relative | game `Content` | `Models\Cubes\Large\Ramp.mwm` |
| Dedicated server, answering a joining client | workshop mod | absolute, the server's own path | | `G:\Space\Season4\Pertam\Instance\content\244850\3768506853\Models\Cubes\small\RetroCockpit.mwm` |

How a loading client reads each form (a single-player load and the answer to a join take the same path, `PreloadVicinityCache`):

| Entry | Vanilla client | Client with Bugfixes 1.5.0 |
|---|---|---|
| `Models\...` | loads from `Content` | same |
| `<id>\Models\...` (Bugfixes 1.5.0 save) | looks for `Content\<id>\Models\...`, logs `Mesh asset ... missing`, goes on | remapped to this client's folder of mod `<id>`: `<mod folder>\Models\...` on Windows, `<mod folder>/Models/...` on Linux. Mod not in the world: left as is and logged missing, like vanilla |
| absolute path that exists on this machine (same machine saved it) | loads | remapped by id if the world has the mod, otherwise kept |
| absolute path of another machine that doesn't exist here | logs missing, goes on | remapped by id if the world has the mod, otherwise dropped |
| Linux `/home/...` path, read under Proton on the same machine | exists through `Z:`, mesh loads, then `UriFormatException` on the texture folder (the reported crash) | remapped by id, otherwise dropped by the `Uri.TryCreate` check. Bugfixes 1.4.0 kept it and crashed. |
| Linux `/home/...` path, read on native Windows | expected to resolve to `C:\home\...` and log missing (not tested) | remapped by id or dropped |
| Windows drive path, read on Linux | linux-compat maps or strips the drive: loads only if the same files are there, otherwise logs missing | remapped by id or dropped |

The texture root of a rooted model is the part of its path before `Models` (`MyMeshes.GetContentPath`), and `MyResourceUtils.NormalizePath` passes it to `new Uri(...)`. That is where a Linux path throws on Windows. A path that isn't rooted gets no texture root of its own, so it can't hit this.

## Testing

Joined the same server from the Linux client with this plugin loaded:

```
Info: Bugfixes: Vicinity preload: of 21 model paths sent by the server,
      remapped 3 onto this client's mods and dropped 0 as unreachable
```

and `VRageRender-DirectX11.log` contains no `Mesh asset … missing` line at
all, against three in the same place before.

The separator handling was checked with a single-player world that holds
four blocks of a workshop mod (Rotary Airlock, 1359954841) near the spawn
point. Its saved vicinity cache lists
`G:\Space\Instance\content\244850\1359954841\Models\Cubes\RotaryAirlockTB.mwm`
and the same path under a mod id the world doesn't have. A single-player load
preloads that cache the same way a join does. Every run logged
`remapped 1 ... and dropped 1`. After 60 seconds:

| Client | Build | Factory entries for the model | Blocks stuck on the loading dummy |
|---|---|---|---|
| Windows (Proton, Wine Mono) | before this fix | two: the preload's `Z:/…` and the block's `Z:\…` | 0 of 4 |
| Windows (Proton, Wine Mono) | this fix | one, the block's own string | 0 of 4 |
| Linux, linux-compat 1.0.20 (no render path fix) | this fix | one, the block's own string | 0 of 4 |
| Linux, linux-compat 1.0.20 (no render path fix) | backslashes kept on Linux | one, a different spelling | 4 of 4 |
| Linux, linux-compat with the render path fix | this fix | one | 0 of 4 |
| Linux, linux-compat with the render path fix | backslashes kept on Linux | one | 0 of 4 |

So on Windows the preload now shares its entry with the block instead of
loading a copy nobody uses. On Linux, keeping the server's backslashes would
leave the mod blocks invisible unless linux-compat has its render model path
fix (planned for 1.0.27); with that fix either spelling works. Not yet tested
on a native Windows install or against a real server.

### Moved saves

Tested on 2026-10-13 with the same Rotary Airlock world, its cache seeded
with Linux paths.

Pulsar Legacy under Proton (Wine Mono), Steam mode. The cache held the mod's
own Linux path and the Linux path of an installed workshop mod the world
doesn't use. Both exist through `Z:`:

| Bugfixes | Load | Saved cache |
|---|---|---|
| none | `UriFormatException` in `NormalizePath`, crash | |
| 1.4.0 | remapped 1, dropped 0, then the same crash on the kept path | |
| this fix | remapped 1, dropped 1, loads | `1359954841\Models\Cubes\RotaryAirlockTB.mwm` |
| none, loading that save | loads | the absolute `Z:\...` path again |

Pulsar Interim on Linux, offline, the mod staged as a local folder named by
its id. Seeded with the mod's path under another user's home and a `Z:` path
of an unknown mod: remapped 1 and dropped 1, and the save wrote the
mod-relative form. Reloading that save remapped it again with no
`Mesh asset ... missing` line. Without Bugfixes the same save loaded, logged
the mod mesh as missing under `Content`, and saved the absolute path, which
the next save with Bugfixes turned back into the mod-relative form. No
exceptions in any of the logs.
