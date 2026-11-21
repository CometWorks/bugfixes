using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.FileSystem;
using VRage.Network;
using VRage.Plugins;
using VRage.Utils;

namespace RefusedJoinProbe;

// Test only, for tests/replicable-hierarchy-after-disconnect/rig.py. A command in
// refused-join-probe.txt in the user data folder:
//   refuse  hands MyMultiplayerClient.OnUserJoined a UserNotConnected join result on the main
//           thread, as when the server (or a cluster Gateway) refuses the client mid-session
//   join    closes the message boxes and joins the Direct Transport server again
// Each step writes a line into refused-join-probe-done.txt.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (++frame % 30 != 0)
            return;
        var request = Path.Combine(MyFileSystem.UserDataPath, "refused-join-probe.txt");
        if (!File.Exists(request))
            return;
        var command = File.ReadAllText(request).Trim();
        File.Delete(request);
        try
        {
            if (command == "refuse")
                Refuse();
            else if (command == "join")
                Join();
            else
                Done($"unknown command {command}");
        }
        catch (Exception e)
        {
            Done($"error: {e}");
        }
    }

    private static void Refuse()
    {
        // MyMultiplayerClient is internal
        var client = MyMultiplayer.Static;
        var character = MySession.Static?.LocalCharacter;
        if (client?.GetType().Name != "MyMultiplayerClient")
        {
            Done($"not refused: the multiplayer session is {client?.GetType().Name}");
            return;
        }
        var onUserJoined = client
            .GetType()
            .GetMethod("OnUserJoined", BindingFlags.Instance | BindingFlags.NonPublic);
        Done(
            $"refusing: seated {character?.Parent is MyCockpit} in {character?.Parent?.GetType().Name}"
        );
        MySandboxGame.Static.Invoke(
            () =>
            {
                var message = new JoinResultMsg { JoinResult = JoinResult.UserNotConnected };
                onUserJoined!.Invoke(client, new object[] { message });
                Done($"refused, session {(MySession.Static == null ? "gone" : "still there")}");
            },
            "RefusedJoinProbe"
        );
    }

    private static void Join()
    {
        foreach (var box in MyScreenManager.Screens.OfType<MyGuiScreenMessageBox>().ToArray())
            box.CloseScreenNow();
        var directClient = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ClientPlugin.DirectClient", false))
            .FirstOrDefault(t => t != null);
        if (directClient == null)
        {
            Done("not joined: no Direct Transport client");
            return;
        }
        directClient.GetMethod("JoinServer")!.Invoke(null, null);
        Done("joining");
    }

    private static void Done(string line)
    {
        MyLog.Default.WriteLineAndConsole($"RefusedJoinProbe: {line}");
        File.AppendAllText(
            Path.Combine(MyFileSystem.UserDataPath, "refused-join-probe-done.txt"),
            line + "\n"
        );
    }
}
