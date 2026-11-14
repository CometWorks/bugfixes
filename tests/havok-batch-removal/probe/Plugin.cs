using System.IO;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Plugins;
using VRage.Utils;

namespace BatchProbe;

// Test only, for tests/havok-batch-removal/rig.py. A line in batch-probe.txt in the user data
// folder makes the probe hand Havok a remove batch the way the game does when it goes wrong:
//   twice    the body of a grid is batched for removal two times
//   removed  the body is removed from the world, then batched for removal
// The body is put back afterwards, and batch-probe-done.txt gets a line saying how it went.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (MySession.Static == null || ++frame % 30 != 0)
            return;

        var request = Path.Combine(MyFileSystem.UserDataPath, "batch-probe.txt");
        if (!File.Exists(request))
            return;

        var mode = File.ReadAllText(request).Trim();
        File.Delete(request);

        var grid = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .FirstOrDefault(g => g.Physics?.RigidBody is { InWorld: true });
        if (grid == null)
        {
            Done($"{mode}: no grid with a body in the world");
            return;
        }

        var world = grid.Physics.HavokWorld;
        var body = grid.Physics.RigidBody;
        MyLog.Default.WriteLineAndConsole(
            $"BatchProbe: {mode} on {grid.DisplayName} ({grid.EntityId})"
        );
        MyLog.Default.Flush();

        if (mode == "removed")
            world.RemoveRigidBody(body);
        world.RemoveRigidBodyBatch(body);
        if (mode == "twice")
            world.RemoveRigidBodyBatch(body);
        world.FinishBatch();

        var left = !body.InWorld;
        world.AddRigidBody(body);
        Done($"{mode}: survived, body left the world: {left}, back in the world: {body.InWorld}");
    }

    private static void Done(string line)
    {
        MyLog.Default.WriteLineAndConsole($"BatchProbe: {line}");
        File.AppendAllText(
            Path.Combine(MyFileSystem.UserDataPath, "batch-probe-done.txt"),
            line + "\n"
        );
    }
}
