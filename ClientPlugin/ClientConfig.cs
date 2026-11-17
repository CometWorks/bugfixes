using System;
using System.IO;
using System.Xml.Serialization;
using Shared.Config;

namespace ClientPlugin;

[XmlRoot(nameof(PluginConfig))]
public sealed class ClientConfig : PluginConfig
{
    private static readonly Lazy<ClientConfig> Startup = new(ReadAtStartup);

    public ClientConfig() => WorldStorageInWorldFolder = true;

    internal static ClientConfig AtStartup => Startup.Value;

    // Pulsar queries preloaders before VRage.FileSystem is initialized.
    private static ClientConfig ReadAtStartup()
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
        return PersistentConfig<ClientConfig>.Peek(Path.Combine(folder, FileName))
            ?? new ClientConfig();
    }
}
