# Character snaps back after touching a station in multiplayer

**Category:** Client only
**Patch:** `ClientPlugin/Patches/CharacterContactPredictionPatch.cs`

## The bug

On a server, flying the character with the jetpack inside a hangar or close to
station walls lags and then snaps back. Players do not have to bump into
anything: flying near a wall or a floor at a few m/s is enough.

The client predicts its own character (`MyPredictedSnapshotSync`) while it is
the controlled entity. `MyCharacter.UpdatePredictionFlag` checks every frame
whether the character capsule overlaps any grid
(`MyPhysics.GetPenetrationsShape` against `MyGridPhysics`). On the first
overlap the client sets `ForceDisablePrediction` for 10 seconds. Without
prediction the character follows the server's delayed position
(`MyAnimatedSnapshotSync`), so the player's input shows up one round trip
late. When the 10 seconds run out, the predicted sync starts again from the
current state and the character jumps by about speed times delay. Each new
touch in those 10 seconds pushes the timer out again.

Havok reports an overlap within its contact tolerance, before the capsule
visibly touches anything, so moving along a wall, a floor or a door frame
keeps prediction off for long stretches.

The vanilla check is meant for grids that move, where a client-side
prediction of a push would disagree with the server. A static grid or a grid
at rest gives nothing to disagree about.

Found on the Octahedron cluster ([SE-1527](https://tasks.cometworks.se/tasks/2468)).
Settled against decompiled 1.210.014.

## The fix

A postfix on `UpdatePredictionFlag` runs only when that call has just turned
`ForceDisablePrediction` on, on a client, for a live character not on a
ladder. It repeats the game's overlap test and checks every grid the capsule
touches. If each one is static or moves slower than 0.5 m/s at the
character's position, the postfix clears `ForceDisablePrediction` and sets
`IsClientPredicted` the way the method would have without it.

A touch with a moving grid still turns prediction off as before. So does an
excessive correction (`AverageCorrection` over 20), a separate trigger in the
same method. A mod cannot make this change: `MyCharacter` is not on the mod
script whitelist.

## Testing

On Linux (game 1.210.014, Pulsar Legacy, headless Remote test clients,
2026-11-16). A probe plugin (`SnapProbe`) logs every frame where the
predicted and the server position differ by more than 0.1 m, every change of
the prediction flags, and a per-second summary. The bot flies with the
jetpack in short taps along each axis near walls and floors. *Unpredicted*
is the share of time with prediction off.

Live cluster lobby through the real Gateway, one bot client:

| Run | Server version | Bugfixes | Time | Unpredicted | Flag changes | Largest snap |
|---|---|---|---|---|---|---|
| first live run | Octahedron 1.2.9, cluster 0.3.15 | no | 6.4 min | 33.9% | 13 switch-offs | 0.7 m |
| k1 | Octahedron 1.2.10, cluster 0.3.16 | no | 6.1 min | 0.0% | 1 | 0.76 m |
| k2 | the same | 1.23.0 | 6.4 min | 0.0% | 1 | 0.18 m |
| k3 | the same | no | 6.3 min | 2.6% | 2 | 0.52 m |
| k4 | the same | 1.23.0 | 6.4 min | 0.0% | 1 | 0.29 m |

Station hangar on a local dedicated server with the cluster's mods, 10 min
each:

| Run | Bugfixes | Unpredicted | Flag changes | Snaps on prediction restart | Largest snap |
|---|---|---|---|---|---|
| plain3 | no | 5.0% | 7 | 10 | 0.85 m |
| plain5 | 1.23.0 | 0.0% | 1 | 1 | 0.49 m |

The one flag change left in every run is prediction turning on at spawn. The
small snaps of 0.1 to 0.3 m left with the fix are the normal reconciliation of
a predicted character and happen without contact too.

How often the bot triggers the bug depends on where it happens to fly, which
is why k1 and k3 show less than the first live run. A player flying fast
along hangar walls triggers it more. The rig is
`octahedron/plugin/tests/snapback.py` and `snapback_live.py` in
CometWorks/worlds. Not tested on Windows.
