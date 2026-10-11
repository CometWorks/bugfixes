# Render thread stalls from constant mesh id hashes

**Category:** Client only
**Patch:** `ClientPlugin/Patches/MeshIndexHashPatch.cs`
**Pull request:** [#13](https://github.com/CometWorks/bugfixes/pull/13)

## The bug

`MeshId.GetHashCode` returns `MeshId.Comparer.GetHashCode()`. That is the
hash of the static comparer object, not of the mesh, so every `MeshId` has the
same hash code. The comparers of three `MyMeshes` index keys build on it:

- `MyMeshPart` shifts `MeshId.GetHashCode()` left by 20 bits and ORs in the
  LOD and the part number. `MyMeshSection` does the same with the section
  name's hash, which spreads its keys somewhat.
- `MyLodMesh` takes the comparer object's hash directly, shifts it left by 16
  bits and truncates it to 16 bits, which leaves only the LOD.

So `PartIndex`, `VoxelPartIndex` and `LodMeshIndex` put every model part and
voxel cell into a few buckets, and every lookup walks a collision chain about
as long as the number of meshes. The cost grows with the square
of the number of voxel cells. `MeshVoxelInfo` and `InterSessionData` use
`MeshId.Comparer`, which hashes the index correctly, and are not affected.

At the "photo mode" voxel quality a planet surface has enough cells to freeze
the game. After a voxel batch finishes meshing, the render thread spends tens
of seconds in `MyMeshes.ResizeVoxelParts` (called from `RemoveVoxelCell` and
`UpdateVoxelCell` in `MyRenderVoxelActor.EndBatch`), and the update thread
waits for it in `MySandboxGame.AfterDraw`. Platform independent game code,
settled against the IL of `VRage.Render11` in 1.210.014.

## The fix

The game's hash methods are left alone. Patching `MeshId.GetHashCode` at
runtime was tried first and crashed the client with a `KeyNotFoundException`
in `SectionIndex`: entries stored under the old hash no longer matched.

Instead, a prefix on `MyRenderVoxelActor.UpdateQueued` runs once, on the
render thread that owns these indexes. It gives the four dictionaries
comparers that hash the mesh index together with the LOD and part (or
section), and adds their entries back under the new hashes. Equality is the
same as the game's. The dictionary's private comparer field is `_comparer` on
.NET and `comparer` on .NET Framework; if neither is found the patch logs a
warning and changes nothing.

The patch needs access to internal `VRage.Render11` types, so this change
also enables the template's publicizer support for that one assembly in the
client project.

## Testing

Linux, Pulsar Interim, isolated headless client with rendering on at
1280x720, `VoxelQuality` 3 (photo mode), world `RemoteAPITestEarthPlanet`
(SE1-0054, 2026-10-11). Both runs below also had the linux-compat fix for the
voxel precalc time budget, which removes a separate Linux-only slowdown that
otherwise hides this one. The machine was shared with other test sessions, so
the times are indicative; the gaps are what the fix removes.

Joining an isolated Magnetar server (DirectTransport) as a new player:

| | Loading screen | Spawn: Join click to clipmaps ready | Update thread stalls over 4 s |
|---|---|---|---|
| Without this fix | 45.5 s | 127 s | 13 s, 29 s, 65 s |
| With this fix | 32.2 s | 31 s | none |

Single player load of the same world: 42.5 s without and 28.7 s with the
fix. The 10 s stall at the end of the load is gone.

The patch logged `Replaced the comparers of the mesh indexes` from the
render thread at startup, and the client kept running through the load, the
spawn and the following meshing waves (about 550,000 voxel jobs in total)
without errors.
