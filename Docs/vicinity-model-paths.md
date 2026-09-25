# Server-absolute mod model paths in the vicinity asset preload

**Category:** Client only
**Patch:** `ClientPlugin/Patches/MySessionVicinityModelPathPatch.cs`
**Pull request:** [#1](https://github.com/CometWorks/bugfixes/pull/1)

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

## Testing

Joined the same server from the Linux client with this plugin loaded:

```
Info: Bugfixes: Vicinity preload: of 21 model paths sent by the server,
      remapped 3 onto this client's mods and dropped 0 as unreachable
```

and `VRageRender-DirectX11.log` contains no `Mesh asset … missing` line at
all, against three in the same place before.

The separator handling was checked on the Linux client with a single-player
world whose saved vicinity cache holds
`G:\Space\Instance\content\244850\1359954841\Models\Cubes\RotaryAirlockTB.mwm`
(a workshop mod the world loads) and the same path under a mod id the world
doesn't have. A single-player load preloads that cache the same way a join
does. The plugin logged `remapped 1 ... and dropped 1`, and the render model
factory entry was exactly the string of the client's own block definition.
Without the plugin, the factory held the server path with the drive letter
stripped.

Not yet checked on a Windows client, where the verbatim `.sbc` spelling is
what makes the preloaded entry the one the placed block uses.
