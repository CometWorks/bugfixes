# Files a mod left open staying locked after the world unloads

**Category:** Client + Server
**Patch:** `Shared/Patches/ModFileHandlesPatch.cs`

## The bug

Mods can't use `System.IO.File` or `FileStream`; they open files through
`IMyUtilities`: `ReadFileIn*`, `WriteFileIn*`, `ReadBinaryFileIn*` and
`WriteBinaryFileIn*`, for the global, local and world storage, the mod's own
folder and the game content. Each of them hands back a `StreamReader`,
`StreamWriter`, `BinaryReader` or `BinaryWriter` on an open file, and the mod
has to dispose it.

When a mod doesn't, nothing in the game closes the file. Leaving the world
doesn't help: mod assemblies stay loaded until the game exits, so a reader or
writer kept in a static field is never collected. The engine opens both
readers and writers with `FileShare.Read`. On Windows that refuses every other
writer and every delete while the file is open, and an open writer refuses
readers too, so the file stays locked until the game exits:

- the mod fails to read or rewrite its own file in the next world, typically
  failing the world load with `IOException: The process cannot access the
  file ... because it is being used by another process`,
- the player can't delete, replace or edit the file,
- text the mod wrote but didn't flush never reaches the disk.

This happens on every way out of a world: exit to menu, disconnect from a
server, a failed world load and loading another save. A dedicated server that
unloads and loads worlds in one process has the same problem. A server restart
that starts a new process does not, the operating system closes the files.

## The fix

A postfix on the 16 `IMyUtilities` methods that return a reader or writer
remembers each one a mod opened, with weak references, so a mod that drops it
still lets it be collected. Calls with no mod on the stack, from plugins or the
game, are not tracked, since a plugin may keep a file open across worlds on
purpose. The mod is found from the first mod script assembly on the call
stack.

A finalizer on `MySession.Unload` disposes every tracked reader and writer whose
file is still open. It runs after the mods' `UnloadData`, so a mod that closes
its files there is left alone, and it also runs when the unload throws.
Disposing a writer flushes it first, so what the mod wrote is saved. Each file
closed this way gets an info line in the game log:

```
Bugfixes: Closed leak.txt which mod LeakTest opened with WriteFileInLocalStorage and left open
```

A mod that keeps using a reader or writer after the world unloaded now gets an
`ObjectDisposedException` instead of writing into a stale file.

On Linux, LinuxCompat emulates the Windows file locks for the storage API. Its
lock follows the file from LinuxCompat 1.0.32 on, so it lifts when Bugfixes
closes the file. With an older LinuxCompat the files are closed, but LinuxCompat
still refuses to reopen them until the game exits.

## Testing

On Linux (2026-10-13, game 1.210.014), with a test mod that opens a text
writer in the local storage, a binary writer in the world storage and a
reader in the global storage in `LoadData`, writes to both writers and closes
none of them. The open files were read from `/proc/<pid>/fd`.

Headless Pulsar 2.4.3 client (Interim), offline world, with a LinuxCompat
build that has the lock change above:

- With Bugfixes disabled, all three files stayed open after exit to menu and
  both written files were still empty. Loading the world again failed with the
  sharing violation from LinuxCompat when the mod read its file back.
- With the fix, exit to menu, loading the world again, a reload and a world
  load the mod failed on purpose each closed all three files and logged the
  three lines above. The written text and the 4 bytes of the binary file were
  on disk, and the next load read them back. While a writer was open, reading
  its file was still refused.
- With the released LinuxCompat 1.0.31, the files were closed, but the next
  load still failed with the sharing violation, as described above.

Isolated Magnetar 2.4.3 server with DirectTransport, same mod:

- A client that joined, then lost the server, closed its three files on the
  disconnect.
- A server world load that the mod failed closed the server's three files in
  `MySession.Unload`. Stopping and restarting Magnetar ends the process, which
  closes the files anyway.

Not tested on Windows, where the files are actually locked.
