# Crash when a piston base without its subparts attaches its top

**Category:** Client + Server
**Patch:** `Shared/Patches/PistonAttachWithoutSubpartsPatch.cs`

## The bug

A client crashed to desktop a few seconds after joining a dedicated server:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Game.Entities.Blocks.MyPistonBase.GetTopMatrixLocal()
   at Sandbox.Game.Entities.Blocks.MyPistonBase.FillFixedData()
   at Sandbox.Game.Entities.Blocks.MyPistonBase.CreateConstraint(MyAttachableTopBlockBase topBlock)
   at Sandbox.Game.Entities.Blocks.MyPistonBase.Attach(MyAttachableTopBlockBase topBlock, Boolean updateGroup)
   at Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase.TryAttach(MyAttachableTopBlockBase top, Boolean updateGroup)
   at Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase.UpdateAttachState()
   at Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase.UpdateOnceBeforeFrame()
   at Sandbox.Game.Entities.Blocks.MyPistonBase.UpdateOnceBeforeFrame()
```

A piston base finds its three moving subparts (`PistonSubpart1`, `2` and `3`) on its current model
in `LoadSubparts`. `GetTopMatrixLocal` reads `Subpart3.WorldMatrix` without a null check, though
other methods of the class do check it. `Subpart3` stays null when the model has no subparts, which
is what a model that failed to load and was replaced by `Error.mwm` looks like. That happened in the
report: LinuxCompat's path resolution in `MyFileSystem.FileExists` got lost on the player's machine,
and every block model whose path casing differs from the disk loaded as `Error.mwm` (SE-1587).
LinuxCompat fixes that part. A mod's piston model without the subpart empties would do the same.

When such a base already holds a top, as one streamed to a joining client or loaded from a save
does, attaching it throws.

## The fix

A prefix on `MyPistonBase.Attach` refuses the attach while `Subpart3` is null. The base keeps the
top's id and tries again every frame, as it does while the top's grid is not in the world yet. Once
a model with the subparts is loaded, the top attaches. Until then the top grid is not held by the
piston.

## Testing

See [`tests/lost-linux-patches`](../tests/lost-linux-patches/rig.py) and the results in
[conveyor-tube-without-line.md](conveyor-tube-without-line.md#testing).
