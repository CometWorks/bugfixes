# Mods without a Data folder reported as failed to load

**Category:** Client + Server
**Patch:** `Shared/Patches/ModWithoutDataFolderPatch.cs`

## The bug

A mod doesn't need a `Data` folder. One that only carries scripts in another
folder, models, textures or data files read by another mod works without it.
The game still reports such a mod as broken. The game log gets six of these,
one per definition load phase:

```
MOD_CRITICAL_ERROR: PackNoData
  in file: Unknown
  MOD PARTIALLY SKIPPED, LOADED ONLY 1/6 PHASES, Following Error occured:
Object reference not set to an instance of an object
```

and the client shows "Some mods were not loaded because of errors" when the
world loads. The Impostors asteroid and grid pack mods on the Octahedron
cluster did this for every Windows player who joined.

`MyDefinitionManager.LoadDefinitions` collects the definition files of each mod
into a list. For a mod without a `Data` folder it adds `null` instead of a
list, then runs the 6 load phases, each looping over the files of every mod.
The loop over `null` throws a `NullReferenceException`, which `FailModLoading`
turns into the critical error above.

## The fix

A transpiler on `LoadDefinitions` adds an empty list for a mod without a `Data`
folder instead of `null`. The phases have nothing to load for that mod, so no
error is logged and the client shows no warning.

LinuxCompat fixes the same bug on Linux by replacing the whole method, so the
code patched here doesn't run there. Bugfixes fixes it on Windows and under
Proton.

## Testing

On Linux (2026-11-03, game 1.210.014), Pulsar Legacy under Proton Experimental,
offline world with two local mods: `PackNoData` holding a single data file and
`PackWithData`, the same with a `Data/README.txt`.

- Without Bugfixes: 6 `MOD_CRITICAL_ERROR: PackNoData` entries, phases 1 to 6.
- With Bugfixes: none, and the world loaded with both mods listed.

Not tested on Windows or on a dedicated server.
