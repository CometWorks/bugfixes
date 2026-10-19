using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Shared.Plugin;

namespace Shared.Patches;

// Client + Server
//
// Keeps a conveyor sorter whose terminal controls were never created from failing to load,
// and from throwing when a script or a player changes its settings. See
// Docs/conveyor-sorter-controls.md.
//
// MyConveyorSorter keeps its terminal controls in static fields, which its
// CreateTerminalControls fills when the first sorter is initialized, unless
// MyTerminalControlFactory already has an entry for the sorter type. A mod that asks for or
// adds sorter controls through MyAPIGateway.TerminalControls before that creates the entry:
// MyTerminalControlFactory.EnsureControlsAreCreated looks for a static CreateTerminalControls
// method, which the sorter doesn't have, so it does nothing. The static fields stay null for
// the rest of the process.
//
// The sync and event handlers of the sorter refresh those controls with UpdateVisual. Loading
// a sorter with Drain All on runs DoChangeDrainAll from MyConveyorSorter.Init, which throws a
// NullReferenceException, and the whole grid fails to load ("ERROR Entity init"). The filter
// and whitelist handlers throw the same way when a script or the server changes them
// (DIS-0033).
//
// The transpiler routes the UpdateVisual calls of those handlers through a method that skips
// a control that doesn't exist. There is nothing to refresh then: the terminal can't show a
// control that was never created.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ConveyorSorterControlsPatch
{
    private static bool logged;

    private static IEnumerable<MethodBase> TargetMethods() =>
        new[]
        {
            "DoChangeDrainAll",
            "DoChangeBlWl",
            "DoChangeListId",
            "DoChangeListType",
            "DoSetupFilter",
        }.Select(name => AccessTools.Method(typeof(MyConveyorSorter), name));

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod
    )
    {
        var il = instructions.ToList();
        var replaced = 0;
        foreach (var instruction in il)
        {
            if (
                instruction.operand
                    is not MethodInfo { Name: nameof(ITerminalControl.UpdateVisual) } method
                || method.GetParameters().Length != 0
                || !typeof(ITerminalControl).IsAssignableFrom(method.DeclaringType)
            )
                continue;

            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(
                typeof(ConveyorSorterControlsPatch),
                nameof(UpdateVisual)
            );
            replaced++;
        }

        if (replaced != 1)
            Common.Logger.Warning(
                "ConveyorSorterControlsPatch: expected one UpdateVisual call in MyConveyorSorter.{0}, replaced {1}",
                __originalMethod.Name,
                replaced
            );

        return il;
    }

    private static void UpdateVisual(ITerminalControl control)
    {
        if (control != null || Common.Config?.Enabled != true)
        {
            control.UpdateVisual(); // Disabled: throws like the game does
            return;
        }

        if (logged)
            return;

        logged = true;
        Common.Logger.Info(
            "The conveyor sorter's own terminal controls were never created: a mod or plugin added sorter controls before the first sorter was initialized. Skipping their refresh."
        );
    }
}
