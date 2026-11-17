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

    // Gives each programmable block script and mod its own copy of the VRage.Math static fields
    // it may write, like Vector3D.Zero, instead of letting it change them for the whole game.
    // On by default. See Docs/vragemath-statics.md.
    bool IsolateMathStatics { get; set; }

    // Keeps client prediction of the player's character on while it touches only grids at rest,
    // instead of the vanilla 10 s without prediction after any contact. On by default.
    // See Docs/character-contact-prediction.md.
    bool CharacterContactPrediction { get; set; }

    // Fastest speed in m/s, at the character's position, at which a touched grid still counts
    // as at rest for CharacterContactPrediction. 0.5 by default.
    float CharacterContactRestSpeed { get; set; }

    // TODO: Add config properties here, then extend the implementing classes accordingly, also add UI to ConfigView.xaml
}
