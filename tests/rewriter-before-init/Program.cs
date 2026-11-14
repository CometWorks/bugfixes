using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

// Calls the server plugin's Rewrite the way Magnetar does for a compilation that comes before
// MyInitializer.InvokeBeforeRun, while MyFileSystem is not initialized yet (bugfixes#41).
//
//   dotnet run -c Release --project tests/rewriter-before-init [-- <Bugfixes.dll> <DedicatedServer64>]
//
// Without a -path argument the rewriter must not throw and must not cache the defaults. The
// harness then runs itself again with -path pointing to a folder holding a Bugfixes.cfg that
// turns IsolateMathStatics off, and the rewriter must read that file.
internal static class Program
{
    private static int Main(string[] args)
    {
        string dll = Path.GetFullPath(
            args.Length > 0 && !args[0].StartsWith("-")
                ? args[0]
                : Path.Combine(
                    AppContext.BaseDirectory,
                    "../../../../../ServerPlugin/bin/Release/net10.0/Bugfixes.dll"
                )
        );
        string ds64 =
            args.Length > 1 && !args[1].StartsWith("-")
                ? args[1]
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".steam/steam/steamapps/common/SpaceEngineersDedicatedServer/DedicatedServer64"
                );

        int index = Array.IndexOf(args, "-path");
        if (index >= 0)
            return Child(dll, ds64, args[index + 1]);

        if (Child(dll, ds64, null) != 0)
            return 1;

        string folder = Directory.CreateTempSubdirectory("bugfixes-41-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "Bugfixes.cfg"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<PluginConfig><IsolateMathStatics>false</IsolateMathStatics></PluginConfig>\n"
            );
            var start = new ProcessStartInfo(Environment.ProcessPath);
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add(ds64);
            start.ArgumentList.Add("-path");
            start.ArgumentList.Add(folder);
            using var child = Process.Start(start);
            child.WaitForExit();
            if (child.ExitCode != 0)
                return 1;
        }
        finally
        {
            Directory.Delete(folder, true);
        }

        Console.WriteLine("PASS: the rewriter works before the game's file system is initialized");
        return 0;
    }

    private static int Child(string dll, string ds64, string path)
    {
        string pluginDir = Path.GetDirectoryName(dll);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var dir in new[] { pluginDir, ds64 })
            {
                string file = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(file))
                    return context.LoadFromAssemblyPath(file);
            }
            return null;
        };

        var fileSystem = Assembly
            .Load("VRage.Library")
            .GetType("VRage.FileSystem.MyFileSystem", true);
        if ((bool)fileSystem.GetProperty("IsInitialized").GetValue(null))
            return Fail("MyFileSystem is already initialized");

        var plugin = AssemblyLoadContext
            .Default.LoadFromAssemblyPath(dll)
            .GetType("ServerPlugin.Plugin", true);
        var target = Enum.Parse(
            Assembly.Load("VRage").GetType("VRage.Scripting.MyApiTarget", true),
            "Ingame"
        );
        var compilation = Assembly
            .Load("Microsoft.CodeAnalysis.CSharp")
            .GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation", true)
            .GetMethod("Create")
            .Invoke(null, ["probe", null, null, null]);

        string label = path == null ? "without -path" : "with -path";
        object result;
        try
        {
            result = plugin.GetMethod("Rewrite").Invoke(null, [compilation, target]);
        }
        catch (TargetInvocationException e)
        {
            return Fail($"{label}: Rewrite threw {e.InnerException}");
        }
        if (result == null)
            return Fail($"{label}: Rewrite returned null");

        var config = plugin
            .GetField("configBeforeInit", BindingFlags.NonPublic | BindingFlags.Static)
            .GetValue(null);
        if (path == null)
        {
            if (config != null)
                return Fail(
                    $"{label}: the defaults were cached, the config file would never be read"
                );
        }
        else
        {
            if (config == null)
                return Fail($"{label}: the config file was not read");
            if ((bool)config.GetType().GetProperty("IsolateMathStatics").GetValue(config))
                return Fail($"{label}: IsolateMathStatics=false in Bugfixes.cfg was not honored");
            if (!ReferenceEquals(result, compilation))
                return Fail(
                    $"{label}: the compilation was rewritten although the config turns it off"
                );
        }

        Console.WriteLine($"ok: {label}");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.WriteLine("FAIL: " + message);
        return 1;
    }
}
