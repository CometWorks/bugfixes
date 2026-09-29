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

On a clustered dedicated server (5 nodes, 1.210, 2026-09-29) running the same postfix in the
cluster plugin, which logs the set's count next to the number of live voxel maps every 5 minutes.
Nodes restart about every hour, so each process below had about an hour of uptime or more:

| Node | Set count | Live voxel maps | Removed by the postfix |
|---|---|---|---|
| node-1 | 1478 | 1478 | 3763 |
| node-2 | 1553 | 1553 | 7243 |
| node-3 | 1521 | 1521 | 10146 |
| node-4 | 6 | 6 | 0 (idle node) |
| node-5 | 1720 | 1720 | 15 |

The count equals the live voxel-map count at every sample. On node-3 it moved between 1521 and 1947
over two hours while the postfix removed 10,146 clipmaps of closed voxel maps. Without the fix the set
kept every voxel map the process had created: the two heap dumps before the fix showed 6,957, then
11,031, equal to the MyVoxelMap count. Every removed entry is a closed voxel map, with its mesher and
clipmap cell structure, that is now collectable.

There is no after-fix heap dump. The count line above measures the set directly. The server's managed
heap growth fell by about a third with this fix alone. The rest came from a separate retention caused
by the cluster, not by the game.
