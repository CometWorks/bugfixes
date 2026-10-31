using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shared.Config;

public class PluginConfig : IPluginConfig
{
    public event PropertyChangedEventHandler PropertyChanged;

    private void SetValue<T>(ref T field, T value, [CallerMemberName] string propName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;

        OnPropertyChanged(propName);
    }

    private void OnPropertyChanged([CallerMemberName] string propName = "")
    {
        PropertyChangedEventHandler propertyChanged = PropertyChanged;
        if (propertyChanged == null)
            return;

        propertyChanged(this, new PropertyChangedEventArgs(propName));
    }

    private bool enabled = true;
    private bool worldStorageInWorldFolder;
    private bool isolateMathStatics = true;
    private bool calmConnectedShips = true;

    // TODO: Implement your config fields here
    // The default values here will apply to Client and Dedicated.
    // The default values for Torch are defined in TorchPlugin.

    public bool Enabled
    {
        get => enabled;
        set => SetValue(ref enabled, value);
    }

    public bool WorldStorageInWorldFolder
    {
        get => worldStorageInWorldFolder;
        set => SetValue(ref worldStorageInWorldFolder, value);
    }

    public bool IsolateMathStatics
    {
        get => isolateMathStatics;
        set => SetValue(ref isolateMathStatics, value);
    }

    public bool CalmConnectedShips
    {
        get => calmConnectedShips;
        set => SetValue(ref calmConnectedShips, value);
    }

    // TODO: Encapsulate your config fields as properties here
}
