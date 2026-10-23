using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Shared.Rewriter;
using VRage.Scripting;

namespace Shared.Patches;

// Client + Server
//
// Remembers which programmable block or mod is being compiled, so the VRage.Math statics
// rewriter can name it in its log lines. See Docs/vragemath-statics.md.
//
// The loader calls the plugin's Rewrite method from MyScriptCompiler.CreateCompilation, which
// gets no name. Compile runs synchronously up to that call on the same thread, so a thread
// static set here is still current there.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyScriptCompiler), nameof(MyScriptCompiler.Compile))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ScriptCompilerNamePatch
{
    private static void Prefix(string friendlyName)
    {
        MathStaticsRewriter.CompilingName = friendlyName;
    }
}
