# Blocks of a moving ship drawn one step behind for a frame

**Category:** Client only
**Patch:** `ClientPlugin/Patches/ActorWorldMatrixIndexPrepatch.cs` (Cecil, from `ClientPlugin/Preloader.cs`)
**Reported in:** [SpaceGT/Pulsar#60](https://github.com/SpaceGT/Pulsar/issues/60)

## The bug

While a ship moves, single blocks or parts of it (batteries, containers,
thrusters, turret barrels) are drawn for one frame where the ship was a
simulation step earlier, then snap back. The faster the ship, the bigger the
jump: at 700 m/s it is about 12 m. Physics is not affected, only the drawn
model.

The renderer keeps a version counter on every render actor,
`MyActor.m_worldMatrixIndex`. Each `MyInstance` caches the version it last
copied the actor's matrix at. `MyInstance.UpdateWorldMatrix` refreshes its
copy only when the two differ, and when they are equal it reads
`LastWorldMatrix`, which never recomputes a dirty matrix.

When a grid moves, its child actors get `SetWorldMatrixDirty`, which marks
them dirty and bumps the version. Then `MyRenderScheduler` runs
`GeometryRenderer.Preprocess` for the main view and for every shadow cascade
as parallel jobs. Each job calls `MyInstance.UpdateWorldMatrix` on every
instance it sees, and an instance visible in several views gets called from
several worker threads at once. All of them can find the actor dirty and run
`MyActor.UpdateWorldMatrix`, and each does `m_worldMatrixIndex++`, which is a
plain read, add and write.

With three or more threads in there, the counter can go backwards. Thread A
reads `n` and stalls, B and C take it to `n + 2`, the instance caches `n + 2`,
then A writes `n + 1`. The instance is now one version ahead of its actor.
Nothing goes wrong in that frame, since all threads computed the same matrix.
On the next move, `SetWorldMatrixDirty` takes the actor to `n + 2`, the
versions match, the update is skipped, and the instance draws the matrix of
the previous step. The issue reporter's capture shows exactly this sequence
on every bad frame.

It needs three or more prepare jobs touching the same actor at once, so how
often it shows depends on how many worker threads the game starts. The game
sizes its thread pool from `MySandboxGame.NumberOfCores` (half of it per
priority group). It needs shadows (more views) and a camera moving with the
ship (its blocks in every cascade). The race is in the stock engine code. It
also exists on the vanilla game, but there it was not seen in practice.

## The fix

The three increments, in `MyActor.SetWorldMatrix`, `SetWorldMatrixDirty` and
`UpdateWorldMatrix`, become `Interlocked.Increment(ref m_worldMatrixIndex)`.
The counter can no longer lose an increment. An instance caches only values
the actor really had, so its cached version never gets ahead, and the next
invalidation always differs from it. Threads that compute the same matrix
concurrently still write the same values, as they did before.

It is a Cecil prepatch rather than a Harmony transpiler. Both
`MyActor.UpdateWorldMatrix` and its caller `MyInstance.UpdateWorldMatrix` are
`[AggressiveInlining]`, so the JIT copies the original increment into its
callers and a Harmony patch on the actor method would not reach those
copies. The prepatch changes the IL before the assembly is loaded. If a game
update changes the three increments, it changes nothing and writes a line to
the console.

## Testing

A stress harness (in the workspace note for SE-0028) drives real `MyActor`
objects from the game's `VRage.Render.dll` on .NET 10. It runs the same
protocol as `MyInstance.UpdateWorldMatrix` from 6 parallel "views" on N
worker threads, with a grid move and `SetWorldMatrixDirty` between frames.
Results are counted over 10 seconds:

| Workers | Stock: instance ahead / stale reads | Prepatched |
|---|---|---|
| 2 | 0 / 0 | |
| 3 | 1 / 6 | |
| 4 | 5 to 7 / 30 to 42 | 0 / 0 |
| 8 | 384k / 2.2M | 0 / 0 |
| 16 | 143k / 819k | 0 / 0 |

In game, the setup was Linux, Pulsar Interim, a headless client at the high
preset (shadows on, 60 FPS cap) and a pasted 2.5k-block cruiser flown in a
straight line from its cockpit at 100 m/s. A test plugin counted the same two
conditions after each frame's prepare jobs had finished: instances ahead of
their actor, and instances drawn with a dirty matrix at equal versions (the
stale frame itself). Each run was about 3 minutes, half parked and half
moving:

| Run | Threads per group | Ahead | Stale draws | Bad frames |
|---|---|---|---|---|
| Stock | 8 | 9 | 7 | 3 |
| Stock, again | 8 | 12 | 8 | 4 |
| Stock, `DOTNET_PROCESSOR_COUNT=8` | 4 | 3 | 0 | 1 |
| Prepatched | 8 | 0 | 0 | 0 |
| Prepatched, again | 8 | 0 | 0 | 0 |

Every event happened while the ship moved. The rewritten `VRage.Render.dll`
in Pulsar's Preloader folder has the three `Interlocked.Increment` calls in
`MyActor`.

Not tested: Windows and Pulsar Legacy (.NET Framework). The prepatch uses
only Cecil and `Interlocked`, the same on both.
