# Crash in Havok's end-of-step contact callbacks after a listener is detached

**Category:** Client + Server
**Patch:** `Shared/Patches/HavokEndOfStepCallbackPatch.cs`
**Pull request:** [#4](https://github.com/CometWorks/bugfixes/pull/4)

## The bug

The client dies with an access violation (a `SIGSEGV` on Linux) inside
`Havok.dll`, in `hkpWorld::finishMtStep`, with no managed exception and no
Havok assert. The faulting instruction is a virtual call through an object
that has been freed:

```
hkpEndOfStepCallbackUtil::fireCallbacks
  mov  rdi, [r13+0x10]      ; m_collisions.data
  add  rdi, rsi             ; entry i
  mov  r14, [rdi]           ; entry.mgr, a hkpSimpleConstraintContactMgr
  mov  rax, [r14]           ; its vtable: already recycled memory
  call [rax+0x88]           ; getConstraintInstance -> crash
```

`hkpEndOfStepCallbackUtil` is Havok's list of contact callbacks that are
fired once per step, single threaded, from `FinishMtStep`. Space Engineers
uses it for the `ManifoldAtEndOfStep` contact point events that grid damage
and most other contact handlers run on. Each entry is
`{ contact manager, listener, source }`.

Keen's native `Havok::HkContactListener`, one per `HkEntity`, registers every
collision it is told about in its `collisionAddedCallback` and unregisters it
in its `collisionRemovedCallback`, and only there. Havok delivers those
callbacks to the listeners attached to the entity at that moment. So:

1. A listener is detached while its body touches something. The game does
   this through `HkEntity.ContactPointCallbackEnabled = false`, and
   `HkEntity.Dispose` does it before releasing a body that is still in the
   world.
2. The contact ends before the next step: the other body is removed from the
   world, or agents are rebuilt by a shape change. Havok destroys the contact
   manager and tells the attached listeners. The detached one is not told and
   never unregisters.
3. `FinishMtStep` walks the list and calls through the freed manager.

Havok itself handles a detached listener whose contact manager is still
alive: the fire loop drops entries whose listener is no longer attached to
the entity. It needs the manager to find that entity, so it cannot handle the
manager dying first.

The core that started this (SE1-0015, Linux client, MES combat, 20 minutes
in) shows the pattern exactly: the dead entry's listener is intact, detached,
and still records the collision; its body is a door panel (dynamic body with
a convex-translate shape, the shape door panels get from their model); the
other body is a missile (capsule shape, sphere inertia) already out of the
world. `MyDoorBase` turns its panels' contact callbacks off whenever the door
is open, opening, unpowered or not working, in a deferred main-thread invoke,
and missiles are closed by `MyEntities.DeleteRememberedEntities` after the
simulation phase of the same frame. Combat near doors hits the window
constantly.

Nothing in the chain is platform or loader specific. The listener, the util
and the fire loop are Keen's and Havok's native code, so Windows clients
crash the same way, and so does the dedicated server, which steps the same
worlds with the same doors and missiles.

## The fix

Harmony prefixes on the two places that detach the contact listener,
`HkEntity.ContactPointCallbackEnabled`'s setter (when turning it off while it
is on) and `HkEntity.Dispose(bool)` (when it is on), run before the native
detach and drop the listener's live registrations from the util's lists:

- The listener's own record array (`+0x20`, 32-byte `hkpCollisionEvent`
  copies) says which collisions it registered: the contact manager and the
  source side of each.
- The world's util is found through the world's extension list (extension id
  1001, the util embedded at `+0x28`), and its `m_collisions` and
  `m_newCollisions` arrays are compacted in place, dropping every
  `{ manager, listener, source }` match and keeping the order.
- The listener's records are left alone, so when the collision really ends
  the game still receives its `CollisionRemoved` event.

Nothing walks those lists between steps, so a detach on the main thread
outside a step edits them directly. A detach from inside a step, or from
another thread (the finalizer disposing a body), is queued and applied on the
main thread around that world's next `FinishMtStep` or `StepSimulation`,
before the list is fired; queued work for a world that gets disposed is
dropped.

No call into Havok is needed, which is what makes one implementation serve
Windows, Linux, the client and the dedicated server: on Linux managed code
cannot call the Microsoft-ABI `Havok.dll` directly.

The memory layout comes from the crash core and the disassembly of the
Havok.dll shipped with the game (1.210.014), and is verified before anything
is written: the MSVC RTTI behind the listener's vtable must name
`Havok::HkContactListener`, and the extension and util vtables must name
`hkpCollisionCallbackUtil` and `hkpEndOfStepCallbackUtil`. The complete
object locator carries its own image-relative address, so the image base
comes out of the pointer itself, on either OS. Any mismatch logs one warning
and switches the fix off for the session. The patch is gated on
`Config.Enabled` like the others.

## What this does not fix

A synchronous world change from inside an end-of-step contact callback,
such as removing an entity or replacing its shape, destroys contact managers
while Havok is still walking the same list, and crashes the same way. Vanilla
handlers defer all such work (`MyEntities.Close` only queues, damage and
explosions go through `Invoke`), so it takes a mod or plugin subscribing to
`MyPhysicsBody.ContactPointCallback` or `MyMissiles.OnMissileCollided` to
trigger it.

## Testing

The bug and the fix were reproduced outside the game with a native stress
harness against `Havok.dll` (`linux-native-wrappers/tests/havok_endofstep.cpp`).
It steps a pile of boxes with the game's world settings and step sequence,
churns bodies with the lifetime sequences the game uses, and checks the
util's list before every `FinishMtStep`:

- Detaching a listener before its body's contact ends produces a dead entry
  on the first occurrence, and the field-like scenario (listeners toggled
  off at random, bodies removed in the game's order) after a few thousand
  steps; the same scenarios crash inside `FinishMtStep` with the field stack
  once the freed memory is reused.
- With the compaction applied at detach time, one hour of the same
  scenarios with fresh seeds every 200k steps (81 runs, 16.2 million steps)
  is clean. The plugin's in-place algorithm was soaked the same way.

In game: the plugin loads and patches on the dedicated server and on the
headless client, and the RTTI gate passes on the first detach.
