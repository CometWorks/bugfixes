using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Shared.Config;

namespace ClientPlugin;

[XmlRoot(nameof(PluginConfig))]
public sealed class ClientConfig : PluginConfig
{
    public const string FileName = "Bugfixes.cfg";
    private static readonly Lazy<ClientConfig> Startup = new(ReadAtStartup);

    private string[] disabledFixes = Array.Empty<string>();

    public ClientConfig() => WorldStorageInWorldFolder = true;

    public string[] DisabledFixes
    {
        get => disabledFixes;
        set => SetValue(ref disabledFixes, value ?? Array.Empty<string>());
    }

    internal static ClientConfig AtStartup => Startup.Value;

    internal bool IsFixEnabled(string id) =>
        id switch
        {
            nameof(Enabled) => Enabled,
            nameof(IsolateMathStatics) => IsolateMathStatics,
            nameof(WorldStorageInWorldFolder) => WorldStorageInWorldFolder,
            _ => Array.IndexOf(DisabledFixes, id) < 0,
        };

    internal void SetFixEnabled(string id, bool enabled)
    {
        switch (id)
        {
            case nameof(Enabled):
                Enabled = enabled;
                break;
            case nameof(IsolateMathStatics):
                IsolateMathStatics = enabled;
                break;
            case nameof(WorldStorageInWorldFolder):
                WorldStorageInWorldFolder = enabled;
                break;
            default:
                DisabledFixes = enabled
                    ? DisabledFixes.Where(fix => fix != id).ToArray()
                    : DisabledFixes.Append(id).ToArray();
                break;
        }
    }

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
