# Seat interior drawn over the outside of a cockpit or cryo chamber

**Category:** Client only
**Patch:** `ClientPlugin/Patches/CockpitInteriorVisibilityPatch.cs`
**Reported in:** DIS-0010 (CometWorks Discord support)

## The bug

Cryo chambers, cryo rooms and the cockpits that have an interior model show two
copies of the block from outside, fighting for the same pixels. One of them may
still have the block's old color after a repaint. Using the seat and leaving it
clears it. The report came from a client at low model quality, where it is
easiest to see.

A seat block with an `InteriorModel` gets two render objects:
`MyRenderComponentCockpit.AddRenderObjects` creates the exterior and the
interior, the one drawn when the camera sits in the seat, and hides the
interior. `MyCockpit.UpdateCockpitModel` then shows one of the two, depending on
where the camera is.

`MyRenderComponentBase.UpdateRenderObjectVisibility(true)` shows every render
object of a component, and the cockpit component doesn't override it. It runs
when the block's `Render.Visible` (`IMyEntity.Visible`) goes from false to true,
or when its grid's does (the setter recurses into the children), and when a
block re-enters the scene with its render objects still there. Plugins and mods
that hide a grid for a while and show it again do this, for example Impostors,
the cutaway of Sections and the effects of Space Jump, and so does the game's
own `MyUpdateTriggerComponent`. After that, the interior stays visible until the
camera enters or leaves the seat, because only then does `UpdateCockpitModel`
run again.

The interior sits in the same place as the exterior. At low model quality the
exterior is drawn from its LOD1 model even up close, while the interior models
have no LODs, so the two surfaces differ slightly and Z-fight in plain view. A
repaint recolors only the first render object
(`MyRenderComponentBase.UpdateRenderEntity`), so the interior keeps the old
color.

The control station (`LargeBlockCockpit`) has no interior model, so this does
not explain the report's mention of it.

Settled against decompiled 1.210.014.

## The fix

A postfix on `MyRenderComponentBase.UpdateRenderObjectVisibility` calls
`MyCockpit.UpdateCockpitModel` when the render objects of a seat with an
interior model have been shown. That hides the interior again unless the
camera is in the seat, the same as entering or leaving the seat does.

## Testing

On Linux (polaris, game 1.210.014, Pulsar Interim, 2026-11-04), an isolated
headless client rendering at 1280x720 with the low graphics preset and model
quality low. An offline creative world has a cryo chamber, a cryo room, a
control station and an industrial cockpit, each on its own static grid. For
each seat the rig takes a screenshot, hides and shows the grid through
`Render.Visible`, takes another, paints the block red, takes another, then uses
the seat, leaves it and takes the last one. A test plugin logs which interior
render objects were last told to be visible while the camera is not in their
seat.

| Step | Without Bugfixes | With Bugfixes |
|---|---|---|
| Loaded | no interior visible | no interior visible |
| Grid hidden and shown | interiors of the cryo chamber, cryo room and industrial cockpit visible | none |
| Painted red | the same three, with white patches of the interior over the red block | none, all red |
| After using the seat | none | none |

The control station showed no second copy in either run. The rig is in the
CometWorks workspace on polaris, `~/ws/se/tmp/se993` (`rig993.py`,
`SeatProbe/`), with the screenshots in `~/.se-test/SE-993-shots`. Not tested
on Windows, in multiplayer, or with the plugins that hide grids; the rig hides
the grid the way they do.
