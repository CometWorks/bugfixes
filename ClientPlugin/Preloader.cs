using System;
using System.Collections.Generic;
using ClientPlugin;
using ClientPlugin.Patches;
using Mono.Cecil;

// DO NOT USE A NAMESPACE HERE!
// CRITICAL: Pulsar locates this class via assembly.GetType("Preloader"), which only succeeds
// for a top-level type with no namespace.

// ReSharper disable once UnusedType.Global
public static class Preloader
{
    // ReSharper disable once UnusedMember.Global
    public static IEnumerable<string> TargetDLLs =>
        StartupConfig.AtStartup.Enabled && StartupConfig.AtStartup.ActorWorldMatrixIndexPrepatch
            ? new[] { "VRage.Render.dll" }
            : Array.Empty<string>();

    // ReSharper disable once UnusedMember.Global
    public static void Patch(AssemblyDefinition asmDef)
    {
        ActorWorldMatrixIndexPrepatch.Prepatch(asmDef);
    }
}
