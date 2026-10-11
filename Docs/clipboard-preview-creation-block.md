# Clipboard preview leaving its entity creation block open

**Category:** Client + Server
**Patch:** `Shared/Patches/ClipboardPreviewCreationBlockPatch.cs`

## The bug

Entities built on a worker thread register their ids in a registry of that
thread, not in the game's main one. `MyEntityIdentifier.LazyInitPerThreadStorage`
gives the thread that registry at the start of an "entity creation block", and
at the end the block takes the new entities out with `GetPerThreadEntities`
and empties it with `ClearPerThreadEntities`. The registry stays with the
thread, so the next block on it starts from whatever is left in it.

`MyGridClipboard.ChangeClipboardPreview` builds the preview grids of the
clipboard and of every projector in such a block. When a copied grid comes out
with no blocks, typically because all of its blocks come from a mod that is
not loaded, or is not a grid at all, it hides the preview with a recursive
call and returns from the middle of the block. Every entity of the preview
grids built before that one stays in the thread's registry, and
`InEntityCreationBlock` stays true on the thread.

The next creation block that runs on that worker takes those entities as its
own:

- `MyForageableEnvironmentProxy` casts everything it collects to
  `MyForageableEntity`. A preview grid in the list stops the game with
  `InvalidCastException: Unable to cast object of type
  'Sandbox.Game.Entities.MyCubeGrid' to type
  'Sandbox.Game.Entities.MyForageableEntity'` in
  `CreateForageableEntitiesData.FinishEntitiesCreation`. On a dedicated server
  with a planet this tends to happen within a second of `Game ready`, because
  projectors build their previews while the world loads.
- A prefab spawn registers them in the main registry as if they were part of
  the new prefab.
- Debris creation throws them away.

## The fix

A prefix notes whether a creation block was already open when
`ChangeClipboardPreview` was entered. A finalizer, which also runs when the
method throws, empties the thread's registry and closes the block when this
call opened it and left it open. Those entities belong to a preview that was
just hidden, and the game registers preview entities only for a visible
preview. The
recursive call that hides the preview sees an open block on entry and leaves
it to the outer call.

On the main thread the per-thread registry is the one that holds the whole
world, so the finalizer leaves it alone there.

## Testing

On a Linux Magnetar dedicated server (game 1.210.014, 2026-10-11) with a
modded survival save that has planets and a projector whose blueprint holds a
grid made only of blocks from a missing mod. A test plugin logged what each
creation block found in its registry when it started.

- Without the fix, all three runs left the same 585 entities (the preview
  grids "Arrow 01." and "Large Grid 8559" with their blocks and subparts) in a
  worker's registry. In one run the next block on that worker was a forageable
  one: it collected all of them and the server stopped with the exception
  above, a second after `Game ready`. In the other two a later projector
  preview collected them and the server kept running.
- With the fix, in two runs, no block on a worker started with leftover
  entities. In one of them the forageable block ran on the same worker right
  after the projector preview, the case that crashed before, and the server
  kept running.

The client loads the same patch; a headless Pulsar client loaded an offline
test world with it without errors. That world has no projector with a broken
blueprint, so the early exit itself was only exercised on the server.
