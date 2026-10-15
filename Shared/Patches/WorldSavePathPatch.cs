using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.World;
using Shared.Plugin;
using VRage.FileSystem;
using VRage.Game;

namespace Shared.Patches;

// Client + Server
//
// Makes the mod API's world storage use the folder the world was loaded from.
// See Docs/world-save-path.md.
//
// The world storage helpers of IMyUtilities (ReadFileInWorldStorage, WriteFileInWorldStorage,
// FileExistsInWorldStorage, DeleteFileInWorldStorage and the binary variants) put their files
// under MySession.WorldSavePath. MySession.Load sets that to SavesPath plus the session name,
// not to the folder it loads the world from. When the folder's name differs from the session
// name, mods read and write a folder that doesn't belong to the world: a renamed world folder,
// a world downloaded from the Workshop, a dedicated server's LoadWorld outside SavesPath. Only
// "Save as" sets it again, so it stays wrong for the whole session (SE1-0207).
//
// The game sets WorldSavePath this way so that a world started from a template points at the
// folder it will be saved to after loading. The transpiler keeps that for worlds loaded from
// the game's Content folder, the templates, and uses the loaded folder for everything else.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(
    typeof(MySession),
    nameof(MySession.Load),
    typeof(string),
    typeof(MyObjectBuilder_Checkpoint),
    typeof(ulong),
    typeof(bool),
    typeof(bool)
)]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class WorldSavePathPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var setter = AccessTools.PropertySetter(typeof(MySession), nameof(MySession.WorldSavePath));
        var il = instructions.ToList();
        var index = il.FindIndex(i => i.Calls(setter));
        if (index < 0)
        {
            Common.Logger.Warning(
                "WorldSavePathPatch: MySession.Load doesn't set WorldSavePath, not patched"
            );
            return il;
        }

        // Stack before the setter: session, the game's path. Turn the path into ours.
        il.InsertRange(
            index,
            [
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(typeof(WorldSavePathPatch), nameof(WorldSavePath))
                ),
            ]
        );
        return il;
    }

    private static string WorldSavePath(string savePath, string sessionPath)
    {
        if (
            Common.Config?.Enabled != true
            || string.IsNullOrEmpty(sessionPath)
            || IsUnder(sessionPath, MyFileSystem.ContentPath)
            || Normalize(savePath) == Normalize(sessionPath)
        )
            return savePath;

        Common.Logger.Info(
            "World storage of mods: using the world's folder {0} instead of {1}",
            sessionPath,
            savePath
        );
        return sessionPath;
    }

    // Textual only: the game's path is built from the session name, which may hold characters
    // Path.GetFullPath rejects on .NET Framework.
    private static string Normalize(string path) =>
        path
            ?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private static bool IsUnder(string path, string folder) =>
        !string.IsNullOrEmpty(folder)
        && Normalize(Path.GetFullPath(path))
            .StartsWith(
                Normalize(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
}
