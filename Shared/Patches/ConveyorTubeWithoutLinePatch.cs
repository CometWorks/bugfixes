using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using Shared.Plugin;

namespace Shared.Patches;

// Client + Server
//
// Keeps a conveyor tube without a conveyor line from crashing the game on its first update. See
// Docs/conveyor-tube-without-line.md.
//
// A conveyor tube (MyConveyorConnector) joins a conveyor line in InitializeConveyorSegment, and only
// if its model has conveyor port dummies. A model that failed to load (Error.mwm) or a mod's tube
// model without them leaves the segment's ConveyorLine null. UpdateOnceBeforeFrame then calls
// ConveyorLine.UpdateIsWorking() without a check. A damaged tube schedules that update when its
// grid loads, so streaming such a grid to a joining client crashed it (SE-1587).
//
// The transpiler returns from UpdateOnceBeforeFrame right after the base call when the segment has
// no line. The tube then has nothing to show, as it has no conveyor line to work with anyway.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyConveyorConnector), nameof(MyConveyorConnector.UpdateOnceBeforeFrame))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ConveyorTubeWithoutLinePatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator
    )
    {
        var il = instructions.ToList();
        var baseCall = il.FindIndex(i =>
            (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt)
            && i.operand is System.Reflection.MethodInfo { Name: "UpdateOnceBeforeFrame" }
        );
        if (baseCall < 0)
        {
            Common.Logger.Warning(
                "ConveyorTubeWithoutLinePatch: no base UpdateOnceBeforeFrame call in MyConveyorConnector.UpdateOnceBeforeFrame, not patched"
            );
            return il;
        }

        var proceed = generator.DefineLabel();
        il[baseCall + 1].labels.Add(proceed);
        il.InsertRange(
            baseCall + 1,
            new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(typeof(ConveyorTubeWithoutLinePatch), nameof(HasLine))
                ),
                new CodeInstruction(OpCodes.Brtrue_S, proceed),
                new CodeInstruction(OpCodes.Ret),
            }
        );
        return il;
    }

    private static bool HasLine(MyConveyorConnector tube)
    {
        return tube.ConveyorSegment.ConveyorLine != null || Common.Config?.Enabled == false;
    }
}
