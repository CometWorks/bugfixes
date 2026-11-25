using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Input;
using VRage.Plugins;
using VRage.Utils;

namespace LostPatchProbe;

// Test plugin for SE-1587. LOST_PATCH_PROBE_DROP lists what to remove every Harmony patch from at
// Init, the way a lost MonoMod detour leaves them: "files" for MyFileSystem.FileExists, "input" for
// MyVRagePlatform.get_Input2. In the main menu it reports what those two return and runs one input update, as a
// focused game window does every frame. Then it loads LOST_PATCH_PROBE_WORLD offline, keeps it
// running for LOST_PATCH_PROBE_SECONDS and exits the game. Every step goes to LOST_PATCH_PROBE_LOG.
public class Plugin : IPlugin
{
    private static readonly string LogPath = Environment.GetEnvironmentVariable(
        "LOST_PATCH_PROBE_LOG"
    );
    private static readonly string World = Environment.GetEnvironmentVariable(
        "LOST_PATCH_PROBE_WORLD"
    );
    private static readonly string Drop =
        Environment.GetEnvironmentVariable("LOST_PATCH_PROBE_DROP") ?? "";
    private static readonly int WorldSeconds = int.TryParse(
        Environment.GetEnvironmentVariable("LOST_PATCH_PROBE_SECONDS"),
        out var s
    )
        ? s
        : 20;

    private enum Phase
    {
        Waiting,
        Loading,
        World,
        Done,
    }

    private Phase phase = Phase.Waiting;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double phaseStart;

    public void Init(object gameInstance)
    {
        Log($"loaded drop={Drop} world={(World == null ? "-" : Path.GetFileName(World))}");
        var targets = new List<MethodBase>();
        if (Drop.Contains("files"))
            targets.Add(AccessTools.Method(typeof(MyFileSystem), nameof(MyFileSystem.FileExists)));
        if (Drop.Contains("input"))
            targets.Add(
                AccessTools.PropertyGetter(
                    AccessTools.TypeByName("VRage.Platform.Windows.MyVRagePlatform"),
                    "Input2"
                )
            );
        var harmony = new Harmony("lost-patch-probe");
        foreach (var method in targets)
        {
            var info = Harmony.GetPatchInfo(method);
            var owners = info == null ? "none" : string.Join(",", info.Owners);
            harmony.Unpatch(method, HarmonyPatchType.All, "*");
            Log(
                $"dropped Harmony patches of {method.DeclaringType?.Name}.{method.Name}: owners={owners}"
            );
        }
    }

    public void Update()
    {
        var now = clock.Elapsed.TotalSeconds;
        switch (phase)
        {
            case Phase.Waiting:
                if (
                    !MySandboxGame.IsGameReady
                    || MySession.Static != null
                    || MyScreenManager.GetFirstScreenOfType<MyGuiScreenBase>() == null
                )
                    break;
                Report();
                if (World == null)
                {
                    Finish("no world");
                    break;
                }
                // The world has no binary save yet: load it from XML without asking
                AccessTools
                    .Property(typeof(MySandboxGame), nameof(MySandboxGame.SuppressLoadingDialogs))
                    .SetValue(MySandboxGame.Static, true);
                Enter(Phase.Loading);
                MySessionLoader.LoadSingleplayerSession(
                    World,
                    onlineMode: MyOnlineModeEnum.OFFLINE
                );
                break;
            case Phase.Loading:
                if (MySession.Static != null && MySession.Static.Ready)
                    Enter(Phase.World);
                else if (now - phaseStart > 300)
                    Finish("TIMEOUT loading the world");
                break;
            case Phase.World:
                if (now - phaseStart >= WorldSeconds)
                    Finish($"world ran {WorldSeconds} s");
                break;
        }
    }

    private void Report()
    {
        var model = Path.Combine(MyFileSystem.ContentPath, "Models\\Cubes\\Large\\PistonBase.mwm");
        Log($"FileExists({model})={MyFileSystem.FileExists(model)}");
        Log(
            $"Input2={(MyVRage.Platform.Input2 == null ? "null" : MyVRage.Platform.Input2.GetType().Name)}"
        );
        try
        {
            ((MyVRageInput)MyInput.Static).UpdateStates();
            Log("input update: ok");
        }
        catch (Exception e)
        {
            var frame = e.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            Log($"input update: {e.GetType().Name} {frame}");
        }
    }

    private void Enter(Phase next)
    {
        phase = next;
        phaseStart = clock.Elapsed.TotalSeconds;
        Log($"phase {next}");
    }

    private void Finish(string why)
    {
        Log($"DONE {why}");
        phase = Phase.Done;
        MySandboxGame.ExitThreadSafe();
    }

    public void Dispose() { }

    private void Log(string text)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff}Z t={clock.Elapsed.TotalSeconds:F1} {text}";
        MyLog.Default?.WriteLine("[LostPatchProbe] " + line);
        if (LogPath != null)
            File.AppendAllText(LogPath, line + Environment.NewLine);
    }
}
