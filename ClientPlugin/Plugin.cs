using System;
using System.IO;
using System.Threading;
using ClientPlugin.Settings;
using HarmonyLib;
using Microsoft.CodeAnalysis.CSharp;
using Sandbox.Graphics.GUI;
using Shared.Config;
using Shared.Logging;
using Shared.Patches;
using Shared.Plugin;
using Shared.Rewriter;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;
using VRage.Scripting;
// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
using System.Reflection;

[assembly: AssemblyVersion("1.22.0.0")]
[assembly: AssemblyFileVersion("1.22.0.0")]

#endif

namespace ClientPlugin;

// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin, ICommonPlugin
{
    public const string Name = "Bugfixes";
    public static Plugin Instance { get; private set; }
    public long Tick { get; private set; }
    private static bool failed;

    public IPluginLogger Log => Logger;
    private static readonly IPluginLogger Logger = new PluginLogger(Name);

    public IPluginConfig Config { get; private set; }
    private PersistentConfig<ClientConfig> config;

    // Called by Pulsar for every script and mod compilation, before the game's own rewriters
    // and the whitelist check
    // ReSharper disable once UnusedMember.Global
    public static CSharpCompilation Rewrite(CSharpCompilation compilation, MyApiTarget target)
    {
        var current = Common.Config ?? ClientConfig.AtStartup;
        if (!current.Enabled || !current.IsolateMathStatics)
            return compilation;

        return MathStaticsRewriter.Rewrite(compilation, target, Logger);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    public void Init(object gameInstance)
    {
#if DEBUG
        // Allow the debugger some time to connect once the plugin assembly is loaded
        Thread.Sleep(100);
#endif

        Instance = this;

        Log.Info("Loading");

        var configPath = Path.Combine(MyFileSystem.UserDataPath, PluginConfig.FileName);
        config = PersistentConfig<ClientConfig>.Load(Log, configPath);

        // Some fixes keep state across frames. Keep their settings fixed until restart.
        var startupConfig = ClientConfig.AtStartup;
        Config = startupConfig;

        var gameVersion = MyFinalBuildConstants.APP_VERSION_STRING.ToString();
        Common.SetPlugin(this, gameVersion, MyFileSystem.UserDataPath);

        if (
            !PatchHelpers.HarmonyPatchAll(
                Log,
                new Harmony(Name),
                startupConfig,
                ClientFixes.MapClientPatch
            )
        )
        {
            failed = true;
            return;
        }

        Log.Debug("Successfully loaded");
    }

    // ReSharper disable once UnusedMember.Global
    public void OpenConfigDialog()
    {
        if (config != null)
            MyGuiSandbox.AddScreen(new SettingsScreen(config.Data));
    }

    public void Dispose()
    {
        try
        {
            // TODO: Save state and close resources here, called when the game exists (not guaranteed!)
            // IMPORTANT: Do NOT call harmony.UnpatchAll() here! It may break other plugins.
        }
        catch (Exception ex)
        {
            Log.Critical(ex, "Dispose failed");
        }

        Instance = null;
    }

    public void Update()
    {
        if (failed)
            return;

#if DEBUG
        CustomUpdate();
        Tick++;
#else
        try
        {
            CustomUpdate();
            Tick++;
        }
        catch (Exception e)
        {
            Log.Critical(e, "Update failed");
            failed = true;
        }
#endif
    }

    private void CustomUpdate()
    {
        // TODO: Put your update code here. It is called on every simulation frame!
        PatchHelpers.PatchUpdates();
    }

    //TODO: Uncomment and use this method to load asset files
    /*public void LoadAssets(string folder)
    {

    }*/
}
