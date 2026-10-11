using System.Collections.Generic;
using ClientPlugin.Patches;
using Mono.Cecil;

// DO NOT USE A NAMESPACE HERE!
// CRITICAL: Pulsar locates this class via assembly.GetType("Preloader"), which only succeeds
// for a top-level type with no namespace.

// ReSharper disable once UnusedType.Global
public static class Preloader
{
    // ReSharper disable once UnusedMember.Global
    public static IEnumerable<string> TargetDLLs { get; } = new[] { "VRage.Render.dll" };

    // ReSharper disable once UnusedMember.Global
    public static void Patch(AssemblyDefinition asmDef)
    {
        ActorWorldMatrixIndexPrepatch.Prepatch(asmDef);
    }
}
