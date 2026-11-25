# Pushing a resting large grid in space

On a dedicated server, an engineer can fly into a loose armor block and stop against its face while the block stays still. Hitting the grid with a ship first lets the engineer move it. A standalone server reproduced this with one large light armor block and a control panel. Havok woke the grid and reported the suit approaching at 16.4 m/s, but the 500 kg grid kept zero linear and angular velocity. Keeping the body awake before impact gave the same result.

Bugfixes records the incoming velocity perpendicular to the face at contact. After Havok resolves the collision, the server checks whether the same body is still at rest. If it is, the patch applies a push at the contact point using the masses of the suit and the grid. The fix applies to dynamic large grids in zero natural gravity and skips rooted grids. The server setting **Let characters push resting grids** is enabled by default.

## Regression test

The Linux rig starts an isolated Magnetar server and a headless Pulsar client. The client flies a suited character into the cube. A server probe records its displacement, clears its speed between attempts, and reports when it has gone back to sleep. Both pushes must move the cube more than half a meter in the direction of impact.

Run `uv run --no-project --with httpx python tests/character-grid-push/check.py fix`. Run the same command with `control` to exercise the stock response and see the displacement check fail. Both runs use the workspace test slots and RAM guard.
