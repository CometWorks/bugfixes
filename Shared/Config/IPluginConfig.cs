using System.ComponentModel;

namespace Shared.Config;

public interface IPluginConfig : INotifyPropertyChanged
{
    // Enables the plugin
    bool Enabled { get; set; }

    // Mod world storage in the folder the world was loaded from, instead of the vanilla
    // SavesPath/<session name>. Off by default: turning it on moves the storage of worlds whose
    // folder name differs from the session name, so mods won't see the data they wrote before.
    // See Docs/world-save-path.md.
    bool WorldStorageInWorldFolder { get; set; }

    // TODO: Add config properties here, then extend the implementing classes accordingly, also add UI to ConfigView.xaml
}
