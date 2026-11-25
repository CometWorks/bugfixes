using System.Globalization;
using System.IO;
using Sandbox.Game.Entities;
using VRage.FileSystem;
using VRage.Plugins;

namespace PushProbe;

public class Probe : IPlugin
{
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (
            ++frame % 6 != 0
            || MyEntities.GetEntityById(144000000000148001L)
                is not MyCubeGrid { Physics: not null } grid
        )
            return;

        var path = Path.Combine(MyFileSystem.UserDataPath, "push-state");
        if (File.Exists(path + ".stop"))
        {
            grid.Physics.ClearSpeed();
            File.Delete(path + ".stop");
        }
        var p = grid.PositionComp.GetPosition();
        File.WriteAllText(
            path + ".tmp",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{grid.Physics.IsActive} {p.X:R} {p.Y:R} {p.Z:R}"
            )
        );
        File.Move(path + ".tmp", path, true);
    }
}
