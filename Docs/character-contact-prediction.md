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
prediction of a push would disagree with the server. A static grid cannot be
pushed, so it gives nothing to disagree about.

Found on the Octahedron cluster ([SE-1527](https://tasks.cometworks.se/tasks/2468)).
Settled against decompiled 1.210.014.

## The fix

A postfix on `UpdatePredictionFlag` runs only when that call has just turned
`ForceDisablePrediction` on, on a client, for a live character not on a
ladder. It repeats the game's overlap test and checks every grid the capsule
touches. If each one is static, and the capsule's axis is not deeper than half
the capsule's radius in any grid, the postfix clears `ForceDisablePrediction`
and sets `IsClientPredicted` the way the method would have without it.

Two cases stay as in vanilla, because prediction makes them worse:

- A loose grid, even one at rest. With prediction on, the client pushes its
  own copy of the grid and the server moves it back. In the test below a
  character bumping a resting armor cube stayed inside it for 1.4 s and was
  then thrown out at 36 m/s.
- A character a server correction pushed deep into a wall or window. Then the
  client shows the server's position, which is outside.

An excessive correction (`AverageCorrection` over 20), a separate trigger in
the same method, also still turns prediction off. A mod cannot make this
change: `MyCharacter` is not on the mod script whitelist.

On by default. Both settings are in `Bugfixes.cfg`, in the game's user data
folder (`%AppData%\SpaceEngineers` on Windows). `CharacterContactPredictionPatch`
set to `false` turns the fix off, also in the Bugfixes settings dialog under
Characters. `CharacterContactRestSpeed` 0 (the default) means static grids
only. Above 0, loose grids slower than that many m/s at the character also
keep prediction on, with the phasing risk above. Restart the game after
changing them.

```xml
  <CharacterContactPredictionPatch>true</CharacterContactPredictionPatch>
  <CharacterContactRestSpeed>0</CharacterContactRestSpeed>
```

## Testing

### Bumping into the Octahedron Large Station

On Linux (nas, game 1.211.18, Pulsar Interim, Magnetar 2.4.3.11 dedicated
server, the client connected over DirectTransport, 2026-11-26). The rig is
`octahedron/plugin/tests/snapback.py` in CometWorks/worlds (`SNAP_MODE=bump`).
A headless client flies from four rooms inside Gamma Station, steered along
the station's axes and at a resting loose armor cube placed in each room. It
pushes into walls, floors and the cube with dampeners on, and coasts into them
at about 4 and 10 m/s with dampeners off. Bugfixes runs on the client and the
server; the control has `CharacterContactPredictionPatch` off. The server had
`CharacterGridPushPatch` (#62) on, its default before 1.26.0. Network delay
is added on the client by SE Dev Tools, the whole round trip. Each run is 15
minutes.

The `SnapProbe` plugin measures, per frame:

- *Prediction off*: the share of time the client shows the server's delayed
  position. It includes 10 s after each of the rig's own teleports back to a
  room (about 22 per run, in both columns).
- *Contact switch-offs*: prediction turned off within 0.2 s of a contact.
- *Phasing*: the capsule's axis within half the capsule's radius of a grid's
  collision. *Longest* is the longest such episode.
- *Key response p95*: the 95th percentile of the time from a key press to the
  character's speed changing by 0.5 m/s.

| Run | Delay (RTT/sigma) | Prediction off | Contact switch-offs | Longest phasing | Key response p95 |
|---|---|---|---|---|---|
| plain | none | 10.1% -> 3.2% | 7 -> 1 | 0.02 s / 0.02 s | 33 -> 17 ms |
| side-load (40 tumbling ships) | none | 6.5% -> 14.2% | 2 -> 5 | 0.02 s / 0.02 s | 17 -> 17 ms |
| Europe | 50/20 ms | 27.2% -> 13.5% | 23 -> 4 | 0.02 s / 0.02 s | 117 -> 17 ms |
| US | 100/30 ms | 48.7% -> 20.9% | 34 -> 16 | 0.02 s / 0.02 s | 233 -> 17 ms |
| China | 250/50 ms | 62.4% -> 51.5% | 32 -> 21 | 0.02 s / 0.02 s | 450 -> 433 ms |
| Australia | 300/50 ms | 57.1% -> 61.4% | 34 -> 29 | 0.02 s / 0.02 s | 483 -> 467 ms |

Each cell is without the fix -> with it. No run had phasing longer than one
frame (0.02 s). Phasing episodes are more frequent with the fix (for example
16 vs 5 with the US delay), all of one frame. At 250 ms and more the
corrections are large, so the deep-penetration fallback and the excessive
correction rule keep prediction off most of the time either way, and the
fix gains little.

Before the two vanilla cases were kept, the same rig showed the 1.4 s phasing
in the resting cube, and with the Europe delay up to 0.22 s inside windows.

### Flying near walls and floors

These runs used the first version of the fix, which also kept prediction on
for loose grids at rest.


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
| k2 | the same | yes | 6.4 min | 0.0% | 1 | 0.18 m |
| k3 | the same | no | 6.3 min | 2.6% | 2 | 0.52 m |
| k4 | the same | yes | 6.4 min | 0.0% | 1 | 0.29 m |

Station hangar on a local dedicated server with the cluster's mods, 10 min
each:

| Run | Bugfixes | Unpredicted | Flag changes | Snaps on prediction restart | Largest snap |
|---|---|---|---|---|---|
| plain3 | no | 5.0% | 7 | 10 | 0.85 m |
| plain5 | yes | 0.0% | 1 | 1 | 0.49 m |

The one flag change left in every run is prediction turning on at spawn. The
small snaps of 0.1 to 0.3 m left with the fix are the normal reconciliation of
a predicted character and happen without contact too.

How often the bot triggers the bug depends on where it happens to fly, which
is why k1 and k3 show less than the first live run. A player flying fast
along hangar walls triggers it more. The rig is
`octahedron/plugin/tests/snapback.py` and `snapback_live.py` in
CometWorks/worlds. Not tested on Windows.
