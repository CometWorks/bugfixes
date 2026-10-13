# World fails to load when a mod registers an entity with id 0

**Category:** Client + Server
**Patch:** `Shared/Patches/LoadCameraEntityIdZeroPatch.cs`

## The bug

A dedicated server saves its world with `<CameraEntity>0</CameraEntity>` in
`Sandbox.sbc`, since it has no camera. The next load of that save fails with:

```
Checkpoint.CameraAttachedTo: 0
Error: Error during loading session:System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Game.World.MySession.set_CameraController(IMyCameraController value)
   at Sandbox.Game.World.MySession.SetCameraController(MyCameraControllerEnum cameraControllerEnum, IMyEntity cameraEntity, Nullable`1 position)
   at Sandbox.Game.World.MySession.LoadCamera(MyObjectBuilder_Checkpoint checkpoint)
   at Sandbox.Game.World.MySession.LoadWorld(MyObjectBuilder_Checkpoint checkpoint, MyObjectBuilder_Sector sector)
```

This happens when a mod has registered an entity under entity id 0 by the
time the camera loads. `MySession.LoadCamera` treats the checkpoint's 0 as a
real id: when nothing controls an entity, it asks `MyEntities.EntityExists(0)`
and, if that is true, puts the camera on `GetEntityById(0)`. No game entity
has id 0, but `MyEntities.Add` registers an entity that has no id yet under 0.

WeaponCore does this. When the first entity is created in a session, it
builds its weapon definitions, and each ammo type with a model fills a pool
with 64 `MyEntity` instances that it adds with `MyEntities.Add`. A new entity
normally gets an id in `MyEntity.Init`, but `MyEntities.Load` suspends id
allocation while it loads the save's entities. When WeaponCore's first trigger
is one of those entities, its pool entities have no id, and the first one is
registered as entity 0.

`LoadCamera` then calls `SetCameraController(Entity, thatEntity)`. That entity
is not a camera controller and has no respawn component, so the game uses the
local character instead. That is null on a dedicated server, and on a client
that controls nothing, so the setter assigns null and then calls a method on
it.

A new world can still start: the Alien Planet template, for example, has a
real camera entity in its checkpoint. The server's first save writes 0, so
admins see the failure at the first restart of a new modded server, often an
auto-restart. Reported in DIS-0018 with the Alien Planet template and 62 mods,
WeaponCore among them.

A client runs the same `LoadCamera`. It fails the same way when it loads such
a save while it controls nothing, for example a dedicated server's world
copied to the client, if WeaponCore starts up during the entity load. In the
tests the client did not hit it on its own: `MyFloatingObjects.LoadData` spawns a
throwaway floating object before the entity load, which started WeaponCore
with id allocation on. That depends on the order the session components load
in, so the patch applies to clients too. A client joining a server loads the
camera before the world's entities arrive, so it did not hit this.

## The fix

A transpiler on `LoadCamera` sends its `SetCameraController` call through a
method that drops a camera entity with id 0 and uses the spectator camera.
`LoadCamera` does the same when the checkpoint's camera entity doesn't exist.
Each time it happens, an info line names the entity's type and model, which
points at the mod that registered it.

On Magnetar the plugin's patches are applied in `IPlugin.Init`, so this works
for the initial world from Magnetar 2.4.3.3 on, which loads that world after
plugin `Init`.

## Testing

On a Linux Magnetar 2.4.3.4 dedicated server (game 1.210.014, 2026-10-13, on
two machines), with the reported world and its mods:

- Without the fix every load of the save failed with the exception above, the
  same on Magnetar 2.4.3.2. A diagnostic patch showed `LoadCamera` passing
  WeaponCore's `Models\Weapons\Projectile_Missile.mwm` entity with id 0, which
  `MyEntities.Add` had registered from `AmmoConstants.PrimeEntityActivator`
  on a worker thread of `MyEntities.Load`.
- With the fix the log showed
  `The saved camera entity resolved to an entity with id 0 (VRage.Game.Entity.MyEntity, model Models\Weapons\Projectile_Missile.mwm), using the spectator camera instead`,
  then `Session loaded` and `Game ready`. This held for cold starts of the
  save, and for three rounds of an Essentials auto command that saves and
  restarts the server every minute, with Essentials, Performance and
  Concealment loaded.

On a headless Linux Pulsar 2.4.1 client (Interim) with the same mods:

- Loading the save offline worked without the fix: the floating object
  warm-up had started WeaponCore before the entity load, and its entities got
  ids. With a test patch that skips that warm-up spawn, the load failed with
  the same exception; with the fix it logged the line above and loaded.
- Joining the fixed server over DirectTransport worked with and without the
  client fix, also with the warm-up skipped: the camera loaded before
  WeaponCore created any entity.

Not tested on Windows.
