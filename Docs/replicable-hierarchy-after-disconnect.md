# World left half unloaded after the server refuses a client in a cockpit

**Category:** Client + Server
**Patch:** `Shared/Patches/ReplicableHierarchyAfterDisconnectPatch.cs`

## The bug

A client sitting in a cockpit was sent back to the main menu by the server, then could not join
again. The first join failed while loading, the second one dropped the client right after it got
into the world:

```
ERROR: Failed to cleanly unload session:
   at Sandbox.Game.World.MySessionLoader.UnloadAndExitToMenu()
   at Sandbox.Engine.Multiplayer.MyMultiplayerClient.OnUserJoined(JoinResultMsg& msg)
   at Sandbox.Engine.Multiplayer.MyMultiplayerClient.OnJoinResult(MyPacket packet)
Exception occurred: System.NullReferenceException: Object reference not set to an instance of an object.
   at Sandbox.Game.Replication.MyCharacterReplicable.OnParentChanged(MyHierarchyComponentBase oldParent, MyHierarchyComponentBase newParent)
   at VRage.Game.Components.MyHierarchyComponentBase.RemoveChild(IMyEntity child, Boolean preserveWorldPos)
   at Sandbox.Game.Entities.MyCockpit.m_pilot_OnMarkForClose(MyEntity obj)
   ...
   at Sandbox.Game.Entities.MyEntities.CloseAll()
```

When the server answers with a join result other than OK, here `UserNotConnected` from a cluster
Gateway that gave up on a client, `MyMultiplayerClient.OnUserJoined` disposes the multiplayer
session first and calls `MySessionLoader.UnloadAndExitToMenu` after it. With `MyMultiplayer.Static`
gone, `MyMultiplayer.ReplicationLayer` returns the offline `MyReplicationSingle`. Four replicables
refresh their place in the replication hierarchy with
`(MyMultiplayer.ReplicationLayer as MyReplicationLayer).RefreshReplicableHierarchy(this)`, and that
cast is null now:

- `MyCharacterReplicable.OnParentChanged`
- `MyTerminalReplicable.OnBlockCubeGridChanged`
- `MyInventoryReplicable.OnBlockCubeGridChanged`
- `MyInventoryBaseReplicable.OnBlockCubeGridChanged`

The unload closes every entity. A cockpit lets its pilot go when it closes, the character's parent
changes, and the first of these throws. `MySession.Unload` stops at `MySector`: the session
components after it never unload, the mods among them. That leaves:

- definitions half unloaded, so the next join fails in `MyBlockVariantGroup.ResolveBlocks` with
  `ERROR: Loading screen failed`;
- mod message handlers registered for good, since the game keeps them in static dictionaries and
  mods remove them in `UnloadData`. In the report, ToolCore's handler from the old world got a packet
  in the next session, failed, and its log writer was already closed, so the exception reached the
  game's packet processing and the client was disconnected (`Packet processing error,
  disconnecting`).

## The fix

A transpiler on the four methods replaces the `RefreshReplicableHierarchy` call with one that does
nothing when the layer is null. Without a multiplayer session there is no replication hierarchy to
refresh, and the unload runs to its end.

[mod-file-handles.md](mod-file-handles.md) no longer closes mod files after an unload that threw,
because the mods may still be live then.

## Testing

TODO
