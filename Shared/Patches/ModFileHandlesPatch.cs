using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Plugin;
using VRage.Game.ModAPI;

namespace Shared.Patches;

// Client + Server
//
// Closes the files a mod opened through the mod API and left open when the world unloads.
// See Docs/mod-file-handles.md.
//
// Mods reach files only through IMyUtilities: ReadFileIn*, WriteFileIn*, ReadBinaryFileIn* and
// WriteBinaryFileIn* in the global, local and world storage, the mod's own folder and the game
// content. Each returns a reader or writer on an open file. A mod that never disposes it keeps
// the file open after the world unloads, and nothing in the game closes it: mod assemblies stay
// loaded, so a writer held in a static field is never collected. On Windows the file stays
// locked until the game exits. The mod can't reopen its own file in the next world, and the
// player can't delete or replace it. A writer's last buffered text never reaches the disk.
//
// The postfix on those methods remembers each reader and writer a mod opened, with weak
// references, so a mod that drops one still lets it be collected. The finalizer on
// MySession.Unload disposes the ones whose file is still open. That runs after the mods'
// UnloadData, so mods that close their files there are left alone. Every way out of a world
// goes through MySession.Unload: exit to menu, disconnect, a failed load, loading another save
// and a dedicated server's world reload.
//
// Calls without a mod on the stack, from plugins or the game itself, are not tracked: a plugin
// may keep a file open across worlds on purpose.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ModFileHandlesPatch
{
    private static readonly Type[] HandleTypes =
    [
        typeof(TextReader),
        typeof(TextWriter),
        typeof(BinaryReader),
        typeof(BinaryWriter),
    ];

    private sealed class OpenFile
    {
        public WeakReference Handle;
        public WeakReference Stream;
        public string Method;
        public string File;
        public string Mod;

        public bool IsOpen =>
            Stream.Target is Stream { CanRead: true } or Stream { CanWrite: true };
    }

    private static readonly List<OpenFile> OpenFiles = new();

    private static IEnumerable<MethodBase> TargetMethods() =>
        typeof(MyAPIUtilities)
            .GetInterfaceMap(typeof(IMyUtilities))
            .TargetMethods.Where(m => HandleTypes.Contains(m.ReturnType));

    private static void Postfix(object __result, MethodBase __originalMethod, string file)
    {
        if (Common.Config?.Enabled != true)
            return;

        var stream = __result switch
        {
            StreamReader reader => reader.BaseStream,
            StreamWriter writer => writer.BaseStream,
            BinaryReader reader => reader.BaseStream,
            BinaryWriter writer => writer.BaseStream,
            _ => null,
        };
        if (stream == null)
            return;

        var mod = FindCallingMod();
        if (mod == null)
            return;

        var openFile = new OpenFile
        {
            Handle = new WeakReference(__result),
            Stream = new WeakReference(stream),
            Method = __originalMethod.Name.Split('.').Last(),
            File = file,
            Mod = mod,
        };

        // Mods open files from worker threads too
        lock (OpenFiles)
        {
            OpenFiles.RemoveAll(f => !f.IsOpen);
            OpenFiles.Add(openFile);
        }
    }

    // The mod of the first mod script assembly on the call stack, as "name (id)", or just the
    // name of a local mod.
    // Null when no mod is calling, or when no mod scripts are loaded.
    private static string FindCallingMod()
    {
        var scriptManager = MyScriptManager.Static;
        if (scriptManager == null)
            return null;

        foreach (var frame in new StackTrace().GetFrames() ?? [])
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly == null)
                continue;

            foreach (var mod in scriptManager.ScriptsPerMod)
            {
                foreach (var scriptId in mod.Value)
                {
                    if (
                        scriptManager.Scripts.TryGetValue(scriptId, out var script)
                        && script == assembly
                    )
                        return mod.Key.ModId == mod.Key.ModName
                            ? mod.Key.ModName
                            : $"{mod.Key.ModName} ({mod.Key.ModId})";
                }
            }
        }

        return null;
    }

    [HarmonyPatch(typeof(MySession), nameof(MySession.Unload))]
    private static class SessionUnloadPatch
    {
        private static void Finalizer()
        {
            List<OpenFile> openFiles;
            lock (OpenFiles)
            {
                openFiles = OpenFiles.Where(f => f.IsOpen).ToList();
                OpenFiles.Clear();
            }

            foreach (var openFile in openFiles)
            {
                // Disposing the reader or writer flushes what the mod wrote, then closes the
                // file. The mod may hold only the stream, if it took BaseStream.
                var handle =
                    (IDisposable)openFile.Handle.Target ?? (IDisposable)openFile.Stream.Target;
                if (handle == null)
                    continue;

                try
                {
                    handle.Dispose();
                    Common.Logger.Info(
                        "Closed {0} which mod {1} opened with {2} and left open",
                        openFile.File,
                        openFile.Mod,
                        openFile.Method
                    );
                }
                catch (Exception ex)
                {
                    Common.Logger.Warning(
                        ex,
                        "Failed to close {0} which mod {1} opened with {2} and left open",
                        openFile.File,
                        openFile.Mod,
                        openFile.Method
                    );
                }
            }
        }
    }
}
