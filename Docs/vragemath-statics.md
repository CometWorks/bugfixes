# Scripts and mods changing VRage.Math constants for the whole game

**Category:** Client + Server
**Code:** `Shared/Rewriter/MathStaticsRewriter.cs`, `Shared/Rewriter/StructMutation.cs`,
`Shared/Patches/ScriptCompilerNamePatch.cs`, `Rewrite` in both `Plugin.cs`
**Setting:** `IsolateMathStatics` in `Bugfixes.cfg`, on by default

## The bug

The vector, matrix and quaternion types of VRage.Math keep their constants in plain
static fields: `Vector3D.Zero`, `Vector3D.Up`, `MatrixD.Identity`, `Quaternion.Identity`
and about a hundred more. None of them is `readonly`. The script whitelist allows the
whole `VRageMath` namespace for programmable block scripts and for mods, and it only
checks which members a script uses, not whether it writes them.

So one line in a programmable block, such as `Vector3D.Zero = new Vector3D(1, 0, 0);`,
changes `Vector3D.Zero` for every other script, every mod and the game itself, on the
client or the dedicated server that runs it, until the process exits. Reloading the world
doesn't undo it. The same happens through a member (`Vector3D.Up.X = 42`), a `ref` or
`out` argument, or a method that changes its struct (`Vector3D.One.Normalize()`).

The game never writes these fields after initializing them, but it reads them
everywhere and passes some of them by `ref`, for example `ref MatrixD.Identity` in the
particle and billboard code. After such a write the game computes wrong results
silently: nothing throws, the numbers are just off.

Shown in game on a client and on a Magnetar dedicated server: a PB wrote all four kinds
above, and a newly compiled script as well as VRage.Math's own
`MatrixD.CreateFromDir` saw the changed values ([SE-628](https://tasks.cometworks.se/tasks/1466)).

## The fix

Pulsar and Magnetar hand every script and mod compilation to the plugin's `Rewrite`
method before the game's own rewriters and the whitelist check run. Bugfixes rewrites
programmable block scripts and mods there; other compilations are left alone.

For each non-readonly static field of VRage.Math that the compilation may write, every
use of that field in the compilation is redirected to a copy in a class added to the
compilation, `__BugfixesVRageMathStatics`, initialized from the real field. A write then
changes only that script's or mod's own copy, and the script still sees its own write
wherever it reads the field. A compilation that only reads the fields is not changed at
all, and neither are the fields it only reads.

A use counts as a possible write when it is

- assigned, compound-assigned, incremented or decremented, directly or through a member,
  like `Vector3D.Up.X = 42`;
- passed as `ref` or `out`, taken by `ref` (C# 7 `ref` locals), or deconstructed into;
- the target of a method, property or indexer call that may change its struct.

Calling a method on a static struct field passes the field's address, so whether such a
call changes it depends on the method. `StructMutation` reads the IL of the VRage.Math
methods and follows the address of `this` through each one. It may be read, or handed to
another method that doesn't change it either, which is checked the same way. Anything else
counts as a change: a store, keeping the address in a local, passing it on by `ref` to
code without IL, and so on. So `Vector3D.One.Length()` and `MatrixD.Identity.Translation`
stay reads, while `Vector3D.One.Normalize()` and `MatrixD.Identity.Translation = v`
redirect the field. When the analysis can't decide, the field is redirected, which is
always safe: the script just works on its own copy.

Each possible write is logged at info level with the script or mod and the line of code,
for example:

```
Bugfixes: SE529Probe may change the shared VRageMath.MatrixD.Identity, it gets its own copy instead: /path/to/Mods/SE529Probe/Data/Scripts/SE529Probe/Probe.cs line 19: MatrixD.Identity.M44 = 7;
Bugfixes: PB 111703061066241592 may change the shared VRageMath.Vector3D.Zero, it gets its own copy instead: Program line 7: Vector3D.Zero = new Vector3D(1, 0, 0);
```

The name is the one the game compiles the script under, `PB: <block name> (<entity id>)`
for a programmable block. Blocks compiled while the world loads, before the plugin's
`Init` runs, are named by their entity id only.
A compilation logs at most 20 such lines, then one line with the number of the rest.

On by default. To turn it off, set `IsolateMathStatics` to `false` in `Bugfixes.cfg` and
restart the game or the server. The file is in the game's user data folder on a client
(`%AppData%\SpaceEngineers` on Windows) and in the server's data folder (the `-path` of a
Magnetar server). A file written by an older Bugfixes has no such line, which means on.

```xml
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <WorldStorageInWorldFolder>false</WorldStorageInWorldFolder>
  <IsolateMathStatics>false</IsolateMathStatics>
</PluginConfig>
```

`Enabled` set to `false` turns it off too, like the other fixes. The scripts of the first
world can be compiled before the plugin's `Init` loads the config, always so on a dedicated
server, so `Rewrite` reads `Bugfixes.cfg` itself until then.

Limits:

- It needs a loader that calls `Rewrite`: Pulsar 2.3.3 or later, Magnetar 2.4.1.1 or later.
  Older loaders don't call it, and scripts compile as before.
- Fields of a generic type used with the script's own type parameters, like
  `MyLineSegmentOverlapResult<T>.DistanceComparer` inside a generic method, are not
  redirected.
- Plugins and the game itself are not compiled through this path and are not affected.

## Testing

On Linux, game 1.210.014, Pulsar 2.4.3 (Interim) and Magnetar 2.4.2.

`tests/vragemath-statics` compiles small scripts against the game's `VRage.Math.dll` with
the Roslyn version the game ships, runs the rewriter, and runs the result:

```
dotnet run -c Release --project tests/vragemath-statics
```

It checks that reads are left alone (including calls like `Length()`, `GetDim()`,
`Equals()` and the `Translation` and `Forward` getters), that each kind of write is redirected (assignment,
member write, increment, compound assignment, `ref`, `out`, `ref` locals, deconstruction,
changing methods, property setters, `using static`, a reference-type field set to null, a
mod with the write and the read in different files), that `nameof` keeps the field's name,
that the log line names the block and the script line, and that after running each
rewritten script the real fields still hold their values. A control compiles the same
write without the rewriter and sees it reach the real field. A cross-check calls every
VRage.Math method the IL scan judges harmless on every static value of its type (2466
calls) and checks that none changed it; the same check over the methods judged changing
finds 292 that did, so it would notice a miss.

In game, with the rig of [SE-628](https://tasks.cometworks.se/tasks/1466):

- Client (isolated headless Pulsar Interim): a local test mod writes `MatrixD.Identity.M44`
  and `Vector3D.Down`, and a programmable block writes `Vector3D.Zero` (assignment),
  `Vector3D.Up.X` (member), `MatrixD.Identity` (`ref`) and `Quaternion.Identity` (`out`).
  The mod and the block each see their own writes. A newly compiled script after each
  write, and VRage.Math's own `MatrixD.CreateFromDir`, see the original values. The log
  names the mod file and line and the block. Without Bugfixes, the same run shows every
  write in the next script and in `CreateFromDir`.
- Pulsar 2.4.1 Legacy under Proton (Wine Mono 6.13), same mod, block and checks: the same
  results, and every write leaking when Bugfixes isn't loaded.
- Magnetar dedicated server: the same block writes, saved through autosave, show the
  block's own copies changed while `MatrixD.CreateFromDir` and `GetOrientation` on the
  server still see the original `Vector3D.Zero` and `MatrixD.Identity`. Before the fix
  `CreateFromDir` returned the block's `(1, 0, 0)` there. The server refuses local mods, so
  mods were only tested on the client; they go through the same compile path.

- `IsolateMathStatics`: with the line missing, the client run above passes; with `false`,
  every write leaks and nothing is logged, as without Bugfixes. On Magnetar, where the
  block compiles before `Init`, `false` read from `Bugfixes.cfg` leaves the block's writes
  leaking into `CreateFromDir`, and `true` keeps them in the block.

Not tested on Windows yet.
