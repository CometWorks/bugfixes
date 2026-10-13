using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Engine.Networking;
using Shared.Config;
using Shared.Plugin;
using VRage.Game;
using VRage.GameServices;

namespace ClientPlugin.Patches;

// Makes the vicinity model cache of a saved world portable between machines.
//
// MySession.GatherVicinityInformation stores the MyModel.AssetName of every model near
// the player in the checkpoint's VicinityModelsCache and VicinityArmorModelsCache, and
// the next load preloads them. Vanilla models are Content-relative there, but mod models
// are this machine's absolute paths, like
// /home/<user>/.local/share/Steam/steamapps/workshop/content/244850/<id>/Models/....
// On another machine or OS those name nothing, and a Linux path loaded on Windows or
// Proton crashes the load: the preload resolves the model's texture folder with
// new Uri(path) in MyResourceUtils.NormalizePath, which throws UriFormatException there.
//
// The prefix rewrites each mod model path to "<publishedFileId>\<path in the mod>" just
// before the checkpoint is written, and MySessionVicinityModelPathPatch maps that onto
// the loading machine's own folder of the mod. A game without this plugin sees a missing
// Content-relative file, which the preload logs and skips, as it does with the server
// paths of a multiplayer join. Rooted paths outside the world's workshop mods are
// dropped. Content-relative vanilla paths are untouched, so the save format is unchanged.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyLocalCache))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class MyLocalCacheVicinityModelPathPatch
{
    private static IPluginConfig Config => Common.Config;

    [HarmonyPrefix]
    [HarmonyPatch(
        nameof(MyLocalCache.SaveCheckpoint),
        new[]
        {
            typeof(MyObjectBuilder_Checkpoint),
            typeof(string),
            typeof(ulong),
            typeof(List<MyCloudFile>),
        },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal }
    )]
    public static void SaveCheckpointPrefix(MyObjectBuilder_Checkpoint checkpoint)
    {
        if (!Config.Enabled || checkpoint == null)
            return;

        var modIds = new HashSet<string>();
        if (checkpoint.Mods != null)
        {
            foreach (var mod in checkpoint.Mods)
            {
                if (mod.PublishedFileId != 0)
                    modIds.Add(mod.PublishedFileId.ToString());
            }
        }

        var changed = MakePortable(checkpoint.VicinityModelsCache, modIds, out var dropped);
        changed += MakePortable(checkpoint.VicinityArmorModelsCache, modIds, out var droppedArmor);
        dropped += droppedArmor;

        if (changed > 0 || dropped > 0)
            Common.Logger.Info(
                $"Vicinity cache: saved {changed} mod model paths relative to their mod and dropped {dropped} other absolute paths"
            );
    }

    // Rewrites the list in place, returns the number of paths made mod-relative.
    private static int MakePortable(List<string> models, HashSet<string> modIds, out int dropped)
    {
        dropped = 0;
        if (models == null || models.Count == 0)
            return 0;

        var total = models.Count;
        var kept = 0;
        var changed = 0;
        for (var i = 0; i < total; i++)
        {
            var model = models[i];
            if (string.IsNullOrEmpty(model) || !MySessionVicinityModelPathPatch.IsRooted(model))
            {
                models[kept++] = model;
                continue;
            }

            if (TryMakeModRelative(model, modIds, out var portable))
            {
                models[kept++] = portable;
                changed++;
            }
        }

        dropped = total - kept;
        models.RemoveRange(kept, dropped);
        return changed;
    }

    // Cuts everything above the path segment naming one of the world's mods. Backslashes,
    // so the rest reads like the model path in the mod's .sbc, which is the spelling the
    // remap on a Windows client has to reproduce.
    private static bool TryMakeModRelative(string path, HashSet<string> modIds, out string portable)
    {
        portable = null;
        if (modIds.Count == 0)
            return false;

        var normalized = path.Replace('/', '\\');
        var start = 0;
        while (true)
        {
            var end = normalized.IndexOf('\\', start);

            // The trailing segment is the file name, never a mod folder.
            if (end < 0)
                return false;

            if (
                MySessionVicinityModelPathPatch.IsPublishedFileId(normalized, start, end)
                && modIds.Contains(normalized.Substring(start, end - start))
            )
            {
                portable = normalized.Substring(start);
                return true;
            }

            start = end + 1;
        }
    }
}
