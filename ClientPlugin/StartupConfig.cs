using System;
using System.IO;
using Shared.Config;

namespace ClientPlugin;

internal static class StartupConfig
{
    private static readonly Lazy<PluginConfig> Startup = new(ReadAtStartup);

    internal static PluginConfig AtStartup => Startup.Value;

    // Pulsar queries preloaders before VRage.FileSystem is initialized.
    private static PluginConfig ReadAtStartup()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "-appdata");
        if (index < 0)
            index = Array.IndexOf(args, "--appdata");

        var folder =
            index >= 0
            && index + 1 < args.Length
            && !args[index + 1].StartsWith("-", StringComparison.Ordinal)
                ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[index + 1]))
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SpaceEngineers"
                );
        return PersistentConfig<PluginConfig>.Peek(Path.Combine(folder, PluginConfig.FileName))
            ?? new PluginConfig();
    }
}
