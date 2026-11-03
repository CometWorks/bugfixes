using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Definitions;
using Shared.Plugin;
using VRage.Game;

namespace Shared.Patches;

// Client + Server
//
// Keeps a mod without a Data folder from being reported as failed to load. See
// Docs/mod-without-data-folder.md.
//
// MyDefinitionManager.LoadDefinitions adds null to its list of definition files for a mod with no
// Data folder, then loops over that list in each of the 6 load phases. Each phase throws a
// NullReferenceException, which FailModLoading turns into a critical definition error ("MOD
// PARTIALLY SKIPPED, LOADED ONLY n/6 PHASES"). The client then shows "Some mods were not loaded
// because of errors" for a mod that has only scripts, models or textures, like the Impostors packs.
//
// The transpiler adds an empty list instead of the null, so the phases have nothing to load.
//
// On Linux, LinuxCompat replaces the whole method with a prefix that skips the null, so the
// original code patched here doesn't run there.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class ModWithoutDataFolderPatch
{
    // LoadDefinitions(List<MyModContext>, List<DefinitionSet>), DefinitionSet is private
    private static MethodBase TargetMethod() =>
        AccessTools
            .GetDeclaredMethods(typeof(MyDefinitionManager))
            .Single(m =>
                m.Name == "LoadDefinitions"
                && m.GetParameters().FirstOrDefault()?.ParameterType == typeof(List<MyModContext>)
            );

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var add = AccessTools.Method(
            typeof(List<List<Tuple<MyObjectBuilder_Definitions, string>>>),
            "Add"
        );
        var il = instructions.ToList();
        var replaced = 0;
        for (var i = 0; i < il.Count - 1; i++)
        {
            if (il[i].opcode != OpCodes.Ldnull || !il[i + 1].Calls(add))
                continue;

            il[i].opcode = OpCodes.Call;
            il[i].operand = AccessTools.Method(
                typeof(ModWithoutDataFolderPatch),
                nameof(NoDefinitions)
            );
            replaced++;
        }

        if (replaced != 1)
            Common.Logger.Warning(
                "ModWithoutDataFolderPatch: expected one null added in MyDefinitionManager.LoadDefinitions, replaced {0}",
                replaced
            );

        return il;
    }

    private static List<Tuple<MyObjectBuilder_Definitions, string>> NoDefinitions() =>
        Common.Config?.Enabled == true ? new() : null; // Disabled: fails like the game does
}
