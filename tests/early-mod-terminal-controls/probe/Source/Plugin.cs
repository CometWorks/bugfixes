using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.ModAPI;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;
using VRage.Utils;

namespace EarlyControlsProbe;

// Test plugin for tests/early-mod-terminal-controls/rig.py (SE-1934). Right after
// MySession.LoadDataComponents, where mods run LoadData and before any block entity loads, it
// reads the IMyTerminalBlock controls and the IMyTimerBlock actions and adds a ProbeButton control
// to IMyTimerBlock, as many mods do. Once a world is ready and a timer block is in it, it reports
// the timer's controls and actions. Every step goes to EARLY_CONTROLS_PROBE_LOG, by default
// early-controls-probe.log in the game's user data folder.
//
// Offline (EARLY_CONTROLS_PROBE_WORLD set): loads that world, reports, exits to the main menu,
// loads it a second time, reports again and exits the game.
// Multiplayer client (no world set): DirectTransport's --connect joins the server; the probe
// reports, exits to the main menu, rejoins through DirectTransport, reports again and exits.
// Dedicated server: reports once and keeps running.
public class Plugin : IPlugin
{
    private static string LogPath = Environment.GetEnvironmentVariable("EARLY_CONTROLS_PROBE_LOG");
    private static readonly string World = Environment.GetEnvironmentVariable(
        "EARLY_CONTROLS_PROBE_WORLD"
    );
    private const int Loads = 2;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private enum Phase
    {
        Menu,
        Loading,
        World,
        Unloading,
        Done,
    }

    private Phase phase = Phase.Menu;
    private double phaseStart;
    private int load;
    private bool loadRequested;

    public void Init(object gameInstance)
    {
        LogPath ??= Path.Combine(MyFileSystem.UserDataPath, "early-controls-probe.log");
        Log(
            $"mode={(Sandbox.Engine.Platform.Game.IsDedicated ? "server" : World == null ? "client" : "offline")}"
        );
        Log($"loaded game={MyFinalBuildConstants.APP_VERSION_STRING}");
        new Harmony("early-controls-probe").Patch(
            AccessTools.Method(typeof(MySession), nameof(MySession.LoadDataComponents)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(EarlyAccess))
        );
    }

    private static void EarlyAccess()
    {
        var controls = MyAPIGateway.TerminalControls;
        controls.GetControls<IMyTerminalBlock>(out var baseControls);
        controls.GetActions<IMyTimerBlock>(out var timerActions);
        var button = controls.CreateControl<IMyTerminalControlButton, IMyTimerBlock>("ProbeButton");
        button.Title = MyStringId.GetOrCompute("Probe");
        controls.AddControl<IMyTimerBlock>(button);
        var timers = TimerCount();
        Log(
            $"early access in LoadData: base_controls={baseControls.Count} timer_actions={timerActions.Count} timers_in_world={timers}"
        );
    }

    public void Update()
    {
        var now = Clock.Elapsed.TotalSeconds;
        switch (phase)
        {
            case Phase.Menu:
                if (MySession.Static != null)
                {
                    load++;
                    loadRequested = false;
                    Enter(Phase.Loading, now);
                    break;
                }
                if (
                    Sandbox.Engine.Platform.Game.IsDedicated
                    || !MySandboxGame.IsGameReady
                    || MyScreenManager.GetFirstScreenOfType<MyGuiScreenBase>() == null
                )
                    break;
                if (load == Loads)
                {
                    Finish("both loads reported");
                    break;
                }
                if (loadRequested)
                {
                    if (now - phaseStart > 300)
                        Finish("TIMEOUT waiting for the load to start");
                    break;
                }
                if (World != null)
                {
                    // The world has no binary save yet: load it from XML without asking
                    AccessTools
                        .Property(
                            typeof(MySandboxGame),
                            nameof(MySandboxGame.SuppressLoadingDialogs)
                        )
                        .SetValue(MySandboxGame.Static, true);
                    MySessionLoader.LoadSingleplayerSession(
                        World,
                        onlineMode: MyOnlineModeEnum.OFFLINE
                    );
                }
                else if (load > 0)
                {
                    // The first join comes from DirectTransport's --connect
                    Log("rejoining through DirectTransport");
                    RejoinThroughDirectTransport();
                }
                else
                    break;
                loadRequested = true;
                phaseStart = now;
                break;
            case Phase.Loading:
                if (MySession.Static != null && MySession.Static.Ready)
                    Enter(Phase.World, now);
                else if (now - phaseStart > 600)
                    Finish("TIMEOUT loading the world");
                break;
            case Phase.World:
                // On a client the grids stream in after the session is ready
                if (now - phaseStart < 5 || (TimerCount() == 0 && now - phaseStart < 120))
                    break;
                Report();
                if (Sandbox.Engine.Platform.Game.IsDedicated)
                {
                    phase = Phase.Done;
                    break;
                }
                Enter(Phase.Unloading, now);
                MySessionLoader.UnloadAndExitToMenu();
                break;
            case Phase.Unloading:
                if (MySession.Static == null)
                    Enter(Phase.Menu, now);
                else if (now - phaseStart > 300)
                    Finish("TIMEOUT unloading the world");
                break;
        }
    }

    private static void RejoinThroughDirectTransport()
    {
        var join = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ClientPlugin.DirectClient", false))
            .FirstOrDefault(t => t?.GetMethod("JoinServer") != null)
            ?.GetMethod("JoinServer");
        if (join == null)
            Log("DirectTransport's DirectClient.JoinServer not found");
        else
            join.Invoke(null, null);
    }

    private static int TimerCount() =>
        MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Sum(g => g.GetFatBlocks<MyTimerBlock>().Count());

    private void Report()
    {
        var controls = MyAPIGateway.TerminalControls;
        controls.GetControls<IMyTimerBlock>(out var timerControls);
        controls.GetActions<IMyTimerBlock>(out var timerActions);
        controls.GetControls<IMyTerminalBlock>(out var baseControls);
        controls.GetActions<IMyTerminalBlock>(out var baseActions);
        var ids = timerControls.Select(c => c.Id).ToList();
        var wanted = new[]
        {
            "OnOff",
            "Name",
            "CustomData",
            "OpenToolbar",
            "TriggerNow",
            "ProbeButton",
        };
        var present = string.Join(",", wanted.Select(w => $"{w}:{ids.Count(i => i == w)}"));
        var timer = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .SelectMany(g => g.GetFatBlocks<MyTimerBlock>())
            .FirstOrDefault();
        var delay =
            timer == null
                ? "no-timer"
                : (((IMyTerminalBlock)timer).GetProperty("TriggerDelay") != null).ToString();
        Log(
            $"REPORT load={load} timer_controls={timerControls.Count} timer_actions={timerActions.Count} "
                + $"base_controls={baseControls.Count} base_actions={baseActions.Count} "
                + $"ids={present} timer_TriggerDelay_property={delay}"
        );
    }

    private void Enter(Phase next, double now)
    {
        phase = next;
        phaseStart = now;
        Log($"phase {next} load={load}");
    }

    private void Finish(string why)
    {
        Log($"DONE {why}");
        phase = Phase.Done;
        MySandboxGame.ExitThreadSafe();
    }

    public void Dispose() { }

    private static void Log(string text)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff}Z t={Clock.Elapsed.TotalSeconds:F1} {text}";
        MyLog.Default?.WriteLine("[EarlyControlsProbe] " + line);
        if (LogPath != null)
            File.AppendAllText(LogPath, line + Environment.NewLine);
    }
}
