# Havok memory leaking from the block placement test, near projections and wheels

**Category:** Client + Server
**Patch:** `Shared/Patches/PlacementTestShapeLeakPatch.cs`

## The bug

A player sitting in a cockpit of a damaged ship with a repair projection saw the `Physics` column of
the game log's `MEMORY VALUES` lines grow by about 200 MB a minute, until the game crashed with
`System.OutOfMemoryException: Havok run out of memory` (SE-1381).

`MyCubeGrid.TestBlockPlacementArea`, the overload with 16 parameters, builds a Havok shape of the
block, queries the world with it and releases it. For a block whose model has no collision shapes and
whose cube topology is not a `StandaloneBox`, which covers armor blocks, it builds fresh convex shapes
with `MyCubeBlockCollector.AddConvexShape` and wraps them into an `HkListShape` created with
`HkReferencePolicy.None`. With that policy the list adds a reference of its own to each child.
Releasing the list at the end drops only the list's reference, so the children are never freed: one
or two convex shapes leak on every call. The game uses `TakeOwnership` wherever else it wraps fresh
child shapes, for example in `MyCharacterProxy`.

The test runs for projected blocks whenever something checks them with Havok intersections: the hand
welder, the ship welders (`MyShipWelder.FindProjectedBlocks`) and Multigrid Projector, whose client
Ship Welding checked every projected block in reach of every welder 60 times a second.

When the block touches a wheel, the method also returns `false` before it releases the shape at all.

## The fix

A transpiler on that overload:

- passes `TakeOwnership` instead of `None` to the list shape built after `AddConvexShape`, so the
  children go away with the list;
- releases the shape when `IntersectsWithWheel` returns true, before the early return.

## Testing

Linux, game 1.210.014, 2026-11-12, with Multigrid Projector's `tests/ds/test_havok_leak_ds.py`: a
Magnetar server without MGP and a headless Pulsar client with MGP sitting in the cockpit of a
station with enabled ship welders without components, next to missing armor slopes and corners of a
repair projection. The client's `Physics` value:

| Setup | Client | MB |
|---|---|---|
| 3 welders, MGP 1.0.0 (welds every frame), one sample a minute | without Bugfixes | 88, 97, 126, 156, 186 (24.5 MB/min) |
| | with this fix | 89, 89, 89, 89, 89 |
| 98 welders, MGP 1.0.1 (welds every 10th frame), one sample every 2 s for 40 s | without Bugfixes | 88 for 20 s, then 92, 100, 104, ... 136 (4 MB every 2 s) |
| | with this fix | 89 all along |

Havok takes the first tens of MB from the free space of its pool, so the value moves only after
that is used up. Bugfixes ran on the client and the server (`MGP_BUGFIXES_REPO`).

The server's vanilla welders: flat at 85 to 87 MB with 3 welders; 103 MB at the end of one run
with 15 welders without Bugfixes, 86 MB with it. The wheel return was not exercised in game.
