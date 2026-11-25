using System;

namespace Shared.Config;

public enum PluginTarget
{
    Shared,
    Client,
    Server,
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigTargetAttribute(PluginTarget target) : Attribute
{
    public PluginTarget Target { get; } = target;
}
