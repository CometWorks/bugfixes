# Closed voxel maps kept alive by the voxel loading wait set

**Category:** Client + Server
**Patch:** `Shared/Patches/VoxelClipmapWaitSetPatch.cs`
**Pull request:** see the README

## The bug

`MyRenderComponentVoxelMap.AddRenderObjects` creates the voxel map's clipmap
and adds it to the static set
`MyRenderComponentVoxelMap.VoxelLoadingWaitStep.Clipmaps`. The loading screen
polls that set to know when terrain is ready. The only way out of the set is
the clipmap's own `Loaded` event (`RemoveClipmap`). Removing the render
objects does not take the clipmap out, whether the voxel map leaves the scene
or is deleted, and neither does closing the entity. A clipmap dropped before
it reports `Loaded` stays in the set for the life of the process. Through its
`MyVoxelMesherComponent` it holds the entity's component container, and so
the closed `MyVoxelMap` with its whole clipmap cell structure.

On the dedicated server nothing renders, so `Loaded` never fires, and the set
holds every voxel map the server ever created. Procedural asteroids are
created and closed continuously around moving players, so this is a steady
leak. Two full heap dumps of one clustered server, 38 minutes apart
(2026-09-29), showed:

- the set's count equal to the `MyVoxelMap` count (6,957, then 11,031);
- 84% of those maps closed (sampled `Closed` flag);
- all of them procedural `Asteroid_…` storages;
- the voxel clipmap types as the largest live growth of the managed heap.

`gcroot` of a closed map ends at a strong static handle through this set and
nothing else. A game client hits the same case for a voxel map that leaves
the scene before its clipmap has loaded. Settled against decompiled
1.210.014.

## The fix

A shared postfix on `MyRenderComponentBase.RemoveRenderObjects` removes the
component's clipmap from the set through the game's own `RemoveClipmap`. A
set that becomes empty therefore still signals `ClipmapsReady`, exactly as a
finished load would. `MyRenderComponentVoxelMap` does not override
`RemoveRenderObjects`, and `MyRenderComponentPlanet` calls the base method, so
patching the base method covers both. A voxel map added back to the scene
gets a new clipmap from `AddRenderObjects`, which goes into the set again.

## Testing

Pending: a clustered dedicated server with the same postfix (cluster plugin
backstop, which logs the set's count every 5 minutes) must show the count
tracking the live voxel maps, with a before/after heap comparison. The
results will be added here before this PR leaves draft.
