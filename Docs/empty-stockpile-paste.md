# Blueprints with an empty construction stockpile fail to paste in a hosted world

**Category:** Client + Server
**Patch:** `Shared/Patches/EmptyStockpilePastePatch.cs`

## The bug

A blueprint of a grid with a partially built block whose stockpile holds no
components has `<ConstructionStockpile />` in its `bp.sbc`. The game writes it
that way itself. `MyObjectBuilder_ConstructionStockpile.Items` has an empty
array initializer, but the pre-generated XML reader in
`VRage.Game.XmlSerializers` overwrites it with null whenever the element has
no `Items` children. That happens for the self-closing form and for an empty
open/close pair alike.

`MyCubeGrid.TryPasteGrid_Implementation` is a replicated event. In any world
that isn't `OFFLINE`, its `MyPasteGridParameters` go through VRage's binary
event serializer, even on the host, and the serializer refuses the null:

```
Error serializing MyObjectBuilder_ConstructionStockpile.Items, member contains null, but it's not allowed
```

The paste is lost. Offline single player invokes the event locally without
serializing it, so it never shows the bug.

Not every paste route is affected. The clipboard (Ctrl+V, F10) clones the
grids first, and the clone restores the empty array. `MyGridStorageHelper`
(the grid store of the station services terminal), `MyVisualScriptLogicProvider`
(scenario scripts) and plugins that paste blueprints raise the event on the
builders freshly read from XML.

This is platform independent game code, settled against decompiled 1.210.014.
It reproduces on the Windows binaries under Proton without any Linux plugin.
8 of the 401 most subscribed workshop blueprints carry the empty form.

## The fix

Every route builds `MyPasteGridParameters` through its only constructor. A
prefix on it replaces a null `Items` with an empty array in every block of
every grid, projected grids included. An empty array means the same as a
stockpile with no components and serializes as zero items, so nothing changes
on the wire.

The fix was first written in the Linux compatibility plugin
(CometWorks/linux-compat#36) and moved here once it turned out to be a game bug.

## Testing

On a headless Pulsar client on Linux hosting the Remote test space world as a
`PRIVATE` lobby over DirectTransport (rig: `se1/notes/se1-0137-empty-stockpile-paste`
in the workspace). It pastes three one-grid blueprints through the Remote
plugin's paste endpoint, which raises `TryPasteGrid_Implementation` on builders
freshly read from XML:

| blueprint | plugin disabled in `Bugfixes.cfg` | plugin enabled |
| --- | --- | --- |
| half built armor block with `<ConstructionStockpile />` | refused, `Error serializing ...Items` | pasted |
| the same block without the element (control) | pasted | pasted |
| a projector whose projected grid holds that block | refused, `Error serializing ...Items` | pasted |

The Windows attribution comes from an earlier run of the same failure on the
Windows binaries under Proton. The fix itself was not run on Windows.
