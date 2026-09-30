using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using HarmonyLib;
using Sandbox.Game.World;
using Shared.Config;
using Shared.Plugin;
using VRage.FileSystem;

namespace ClientPlugin.Patches;

// Fixes the vicinity asset preload for modded servers.
//
// When a client joins, the server answers its vicinity-cache request with the model
// paths taken from the server's own MyModel.AssetName values. For vanilla blocks those
// are Content-relative and resolve on any client. For mod blocks they are the server's
// absolute on-disk paths, which name nothing on the machine that receives them, so
// every one of them fails as a missing mesh asset and the prewarm the vicinity cache
// exists for never happens for modded blocks near the spawn point.
//
// Both sides hold the same published workshop items, so the server's layout above the
// mod folder is the only part that differs. Replacing it with this client's own folder
// for that mod makes the preload work; what cannot be matched that way is dropped, so
// the preload becomes a no-op instead of a failed load.
//
// Verified on Windows with both the vanilla game and Pulsar: identical failures, so
// this is a game bug rather than a loader or platform one.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MySession))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class MySessionVicinityModelPathPatch
{
    private static IPluginConfig Config => Common.Config;

    [HarmonyPrefix]
    [HarmonyPatch("PreloadVicinityCache")]
    public static void PreloadVicinityCachePrefix(List<string> models, List<string> armorModels)
    {
        if (!Config.Enabled)
            return;

        Remap(models);
        Remap(armorModels);
    }

    // Rewrites the received list in place, dropping what this client cannot load.
    private static void Remap(List<string> models)
    {
        if (models == null || models.Count == 0)
            return;

        var modFolders = ModFoldersByPublishedFileId();
        var total = models.Count;
        var remapped = 0;
        var kept = 0;

        for (var i = 0; i < total; i++)
        {
            var model = models[i];
            if (string.IsNullOrEmpty(model))
                continue;

            if (TryRemapToLocalMod(model, modFolders, out var local))
            {
                models[kept++] = local;
                remapped++;
                continue;
            }

            // A rooted path names a location on the sender's own disk. It is usable
            // only while the sender is this machine, which is the case when hosting.
            if (IsRooted(model) && !Exists(model))
                continue;

            models[kept++] = model;
        }

        var dropped = total - kept;
        models.RemoveRange(kept, dropped);

        if (remapped > 0 || dropped > 0)
            Common.Logger.Info(
                $"Vicinity preload: of {total} model paths sent by the server, remapped {remapped} onto this client's mods and dropped {dropped} as unreachable"
            );
    }

    // Replaces everything up to and including the path segment naming a loaded mod with
    // this client's own folder for that mod.
    private static bool TryRemapToLocalMod(
        string path,
        Dictionary<string, string> modFolders,
        out string remapped
    )
    {
        remapped = null;
        if (modFolders.Count == 0)
            return false;

        var normalized = path.Replace('\\', '/');
        var start = 0;
        while (true)
        {
            var end = normalized.IndexOf('/', start);

            // The trailing segment is the file name, never a mod folder.
            if (end < 0)
                return false;

            if (
                IsPublishedFileId(normalized, start, end)
                && modFolders.TryGetValue(normalized.Substring(start, end - start), out var folder)
            )
            {
                // The render model factory matches the preloaded path against the one in
                // this client's own block definition, so the result must be spelled the
                // same way. On Windows that is Path.Combine(mod folder, .sbc text), and
                // the server sends the .sbc text verbatim below the mod folder. On Linux,
                // linux-compat normalizes definition paths to '/'.
                var rest = Path.DirectorySeparatorChar == '/' ? normalized : path;
                remapped = folder + rest.Substring(end);
                return true;
            }

            start = end + 1;
        }
    }

    private static Dictionary<string, string> ModFoldersByPublishedFileId()
    {
        var map = new Dictionary<string, string>();

        var mods = MySession.Static?.Mods;
        if (mods == null)
            return map;

        foreach (var mod in mods)
        {
            // A mod the server deployed outside the workshop cannot be matched by id,
            // and this client has no copy of it to preload anyway.
            if (mod.PublishedFileId == 0)
                continue;

            string folder;
            try
            {
                folder = mod.GetPath();
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrEmpty(folder))
                map[mod.PublishedFileId.ToString()] = folder.TrimEnd('\\', '/');
        }

        return map;
    }

    // Every key is a decimal published file id, so vanilla paths never allocate here.
    private static bool IsPublishedFileId(string path, int start, int end)
    {
        if (end <= start)
            return false;

        for (var i = start; i < end; i++)
        {
            if (path[i] < '0' || path[i] > '9')
                return false;
        }

        return true;
    }

    // Rooted as the sender meant it: an absolute path, or a Windows drive or UNC path.
    private static bool IsRooted(string path)
    {
        return path[0] == '/' || path[0] == '\\' || (path.Length >= 2 && path[1] == ':');
    }

    private static bool Exists(string model)
    {
        try
        {
            if (!model.EndsWith(".mwm", StringComparison.OrdinalIgnoreCase))
                model += ".mwm";

            return MyFileSystem.FileExists(model);
        }
        catch
        {
            return false;
        }
    }
}
