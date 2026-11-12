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

Multigrid Projector 1.0.1 fixes the policy too, for players without Bugfixes. Whichever plugin
patches first changes the argument; the other one finds it changed and leaves it.

## Testing

Linux, game 1.210.014, 2026-11-12, with Multigrid Projector's `tests/ds/test_havok_leak_ds.py`: a
Magnetar server without MGP, a headless Pulsar client with MGP 1.0.0 (f427c3a, without its own fix)
sitting in the cockpit of a station with three enabled ship welders without components, next to 14
missing armor slopes and corners of a repair projection. The client's `Physics` value, one a
minute:

| Client | MB |
|---|---|
| without Bugfixes | 88, 97, 126, 156, 186 (24.5 MB/min) |
| with this fix (`MGP_BUGFIXES_REPO`, client and server) | 89, 89, 89, 89, 89 |

The server's value did not move in either run (85 and 86 MB), so this setup shows nothing about the
server's ship welders. The wheel return was not exercised in game.
