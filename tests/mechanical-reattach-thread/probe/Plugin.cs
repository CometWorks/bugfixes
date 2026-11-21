using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace ReattachProbe;

// Test only, for tests/mechanical-reattach-thread/rig.py. From the start of the game the probe
// counts the calls that reach MyCubeGrid.MechanicalConnectionBlockAttachUpdateStatusChanged off
// the update thread, which is where a grid's BlocksWaitingOnAttachUpdate set gets changed. A line
// in reattach-probe.txt in the user data folder makes it act on the rotors in the world:
//   report          write the counts so far
//   stream SECONDS  hand copies of the rotor heads' grids to the entity creation thread, the way a
//                   client initializes the grids a server streams to it. Each head's Init marks
//                   its rotor for reattach from that thread.
//   hammer SECONDS  call MarkForReattach on the rotors from a worker thread in a tight loop.
// The main thread meanwhile reattaches the rotors every frame. The probe writes a report line to
// reattach-probe-done.txt and keeps counting.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private static int offThreadCalls;
    private static readonly ConcurrentDictionary<string, int> OffThreadNames = new();

    private readonly List<MyMotorStator> stators = new();
    private string mode;
    private int seconds;
    private int frame;
    private int phaseFrame;
    private int phase;
    private DateTime endTime;
    private Thread worker;
    private volatile bool stop;
    private long workerCalls;
    private string workerError;
    private int streamed;

    public void Init(object gameInstance)
    {
        var harmony = new Harmony("reattach-probe");
        harmony.Patch(
            AccessTools.Method(
                typeof(MyCubeGrid),
                "MechanicalConnectionBlockAttachUpdateStatusChanged"
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(StatusChangedPrefix))
        );
    }

    public void Dispose() { }

    private static void StatusChangedPrefix()
    {
        if (Thread.CurrentThread == MySandboxGame.Static?.UpdateThread)
            return;
        Interlocked.Increment(ref offThreadCalls);
        OffThreadNames.AddOrUpdate(Thread.CurrentThread.Name ?? "(unnamed)", 1, (_, n) => n + 1);
    }

    public void Update()
    {
        if (MySession.Static == null || !MySession.Static.Ready)
            return;
        frame++;

        switch (phase)
        {
            case 0:
                if (frame % 30 == 0)
                    ReadRequest();
                break;
            case 3:
                Race();
                break;
            case 4:
                if (++phaseFrame == 180)
                    Report();
                break;
        }
    }

    private void ReadRequest()
    {
        var request = Path.Combine(MyFileSystem.UserDataPath, "reattach-probe.txt");
        if (!File.Exists(request))
            return;
        var words = File.ReadAllText(request)
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        File.Delete(request);
        mode = words[0];
        seconds = words.Length > 1 ? int.Parse(words[1]) : 0;
        Log($"request {mode} {seconds}");
        stators.Clear();
        stators.AddRange(
            MyEntities
                .GetEntities()
                .OfType<MyCubeGrid>()
                .SelectMany(g => g.GetFatBlocks<MyMotorStator>())
        );
        if (mode == "report")
        {
            phase = 4;
            phaseFrame = 179;
            return;
        }
        StartRace();
    }

    private int Attached() =>
        stators.Count(s => s.TopBlock != null && s.TopGrid != null && !s.TopGrid.MarkedForClose);

    private void StartRace()
    {
        Log($"heads attached before the race: {Attached()}/{stators.Count}");
        endTime = DateTime.UtcNow.AddSeconds(seconds);
        if (mode == "hammer")
        {
            worker = new Thread(Hammer) { Name = "reattach-probe worker", IsBackground = true };
            worker.Start();
        }
        phase = 3;
    }

    private void Hammer()
    {
        try
        {
            while (!stop)
            {
                foreach (var stator in stators)
                {
                    stator.MarkForReattach();
                    workerCalls++;
                }
            }
        }
        catch (Exception e)
        {
            workerError = e.GetType().Name + ": " + e.Message;
        }
    }

    private void Race()
    {
        if (mode == "stream" && frame % 5 == 0)
        {
            foreach (var stator in stators)
            {
                var top = stator.TopGrid;
                if (top == null)
                    continue;
                var builder = (MyObjectBuilder_CubeGrid)top.GetObjectBuilder(true);
                MyEntities.InitAsync(null, builder, false);
                streamed++;
            }
        }

        if (DateTime.UtcNow < endTime && workerError == null)
            return;

        stop = true;
        worker?.Join(5000);
        worker = null;
        stop = false;
        phase = 4;
        phaseFrame = 0;
    }

    private void Report()
    {
        var waiting = 0;
        var consistent = true;
        var corrupt = "none";
        foreach (var grid in stators.Select(s => s.CubeGrid).Distinct())
        {
            // A HashSet changed by two threads at once can have a cycle in a bucket chain, so the
            // walk stops after Count + 1 items, and its internals can throw.
            var set = grid.BlocksWaitingOnAttachUpdate;
            try
            {
                var items = set.Take(set.Count + 1).ToList();
                waiting += items.Count;
                if (items.Count != set.Count || items.Any(b => !set.Contains(b)))
                {
                    consistent = false;
                    corrupt = $"{grid.DisplayName}:count={set.Count},walked={items.Count}";
                }
            }
            catch (Exception e)
            {
                consistent = false;
                corrupt = $"{grid.DisplayName}:{e.GetType().Name}";
            }
        }

        var names = string.Join(",", OffThreadNames.Select(kv => $"{kv.Key}={kv.Value}"));
        Done(
            $"mode={mode} seconds={seconds} streamed={streamed} worker_calls={workerCalls} "
                + $"worker_error={workerError ?? "none"} off_thread_calls={offThreadCalls} off_threads=[{names}] "
                + $"attached={Attached()}/{stators.Count} waiting={waiting} set_consistent={consistent} corrupt={corrupt}"
        );
        phase = 0;
    }

    private static void Log(string line)
    {
        MyLog.Default.WriteLineAndConsole($"ReattachProbe: {line}");
        MyLog.Default.Flush();
    }

    private static void Done(string line)
    {
        Log(line);
        File.AppendAllText(
            Path.Combine(MyFileSystem.UserDataPath, "reattach-probe-done.txt"),
            line + "\n"
        );
    }
}
