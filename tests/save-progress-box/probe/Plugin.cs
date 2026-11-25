using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Analytics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.FileSystem;
using VRage.Plugins;
using VRage.Utils;

namespace SaveBoxProbe;

// Test only, for tests/save-progress-box/rig.py. A file savebox-probe.txt in the user data folder
// makes the probe exit the joined world to the main menu. Right after MySession.UnloadMultiplayer,
// while MySession.Static is still set, the probe raises the window close request the way the
// window's X does in that moment, which queues the "Do you want to save your progress?" box. Once
// the session is gone, the probe writes what it saw to savebox-probe-done.txt. The rig then clicks
// the box through the Remote plugin, which runs MySandboxGame.OnSaveWorldMessageBoxCallback with
// no session (SE-1877). An exception out of that callback goes to the game log with its stack.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private static bool armed;
    private static string unloadState = "not reached";
    private static bool forcedAnalyticsSession;

    private static readonly AccessTools.FieldRef<MySpaceAnalytics, bool> SessionStarted =
        AccessTools.FieldRefAccess<MySpaceAnalytics, bool>("m_isSessionStarted");

    private bool exiting;

    public void Init(object gameInstance)
    {
        new Harmony("savebox-probe").Patch(
            AccessTools.Method(typeof(MySession), "UnloadMultiplayer"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterUnloadMultiplayer))
        );
        // The Remote plugin catches an exception out of a click it runs, so the game does not
        // crash as a player's does. Log the exception with its stack, then let it go on.
        new Harmony("savebox-probe").Patch(
            AccessTools.Method(typeof(MySandboxGame), "OnSaveWorldMessageBoxCallback"),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(LogCallbackException))
        );
    }

    private static Exception LogCallbackException(Exception __exception)
    {
        if (__exception != null)
            MyLog.Default.WriteLine($"SaveBoxProbe: save box callback threw {__exception}");
        return __exception;
    }

    public void Dispose() { }

    private static void AfterUnloadMultiplayer()
    {
        if (!armed)
            return;
        armed = false;

        // A game started with an analytics session keeps it through exit to menu. The test client
        // runs without Steam and may not have started one, so set the flag as the player had it.
        var analytics = MySpaceAnalytics.Instance;
        if (!SessionStarted(analytics))
        {
            SessionStarted(analytics) = true;
            forcedAnalyticsSession = true;
        }

        unloadState =
            $"session_set={MySession.Static != null} multiplayer={MyMultiplayer.Static != null} "
            + $"is_server={Sync.IsServer}";
        MyLog.Default.WriteLine($"SaveBoxProbe: window close request during unload, {unloadState}");
        AccessTools
            .Method(typeof(MySandboxGame), "Window_OnManualWindowCloseRequest")
            .Invoke(MySandboxGame.Static, null);
    }

    public void Update()
    {
        if (exiting)
        {
            if (MySession.Static != null)
                return;
            exiting = false;
            var box = MyScreenManager
                .Screens.OfType<MyGuiScreenMessageBox>()
                .Any(b => b.ResultCallback?.Method.Name == "OnSaveWorldMessageBoxCallback");
            Report(box);
            return;
        }

        var request = Path.Combine(MyFileSystem.UserDataPath, "savebox-probe.txt");
        if (!File.Exists(request))
            return;
        File.Delete(request);
        if (MySession.Static == null)
            return;
        armed = true;
        exiting = true;
        MyLog.Default.WriteLine("SaveBoxProbe: exit to menu");
        MySessionLoader.UnloadAndExitToMenu();
    }

    private static void Report(bool boxOnMenu)
    {
        File.AppendAllText(
            Path.Combine(MyFileSystem.UserDataPath, "savebox-probe-done.txt"),
            $"{unloadState} forced_analytics_session={forcedAnalyticsSession} "
                + $"box_on_menu={boxOnMenu} session_now={MySession.Static != null}\n"
        );
    }
}
