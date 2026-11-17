using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Shared.Config;

public class PluginConfig : IPluginConfig
{
    public const string FileName = "Bugfixes.cfg";

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
    private string[] disabledFixes = Array.Empty<string>();

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

    public string[] DisabledFixes
    {
        get => disabledFixes;
        set => SetValue(ref disabledFixes, value ?? Array.Empty<string>());
    }

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
        if (IsFixEnabled(id) == enabled)
            return;

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
}
