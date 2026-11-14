using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Shared.Plugin;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Components;

namespace Shared.Patches;

// Client + Server
//
// Keeps a merge block without a merge field from crashing the game when its grid is added to the
// scene. See Docs/merge-block-physics.md.
//
// MyShipMergeBlock.Init creates the block's phantom physics (the merge field) in LoadDummies, and
// only if the block's model has a dummy whose name contains "merge". When the dummies of the model
// can't be read (MyModel.LoadOnlyDummies logs "Importing asset failed" and leaves them empty) or a
// mod's merge block model has no such dummy, Physics stays null. OnAddedToScene then sets
// Physics.Enabled without a null check, so adding the grid throws a NullReferenceException. On a
// client joining a server that is an unhandled exception on the main thread (SE-1480). The game's
// own IsWorkingChanged handler of the block already checks Physics for null.
//
// The transpiler sets Physics.Enabled through a method that skips a null Physics. Such a block
// has no merge field, so it never merges, as if it were switched off.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch(typeof(MyShipMergeBlock), nameof(MyShipMergeBlock.OnAddedToScene))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class MergeBlockPhysicsPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var il = instructions.ToList();
        var replaced = 0;
        foreach (var instruction in il.Where(IsSetPhysicsEnabled))
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(
                typeof(MergeBlockPhysicsPatch),
                nameof(SetEnabled)
            );
            replaced++;
        }

        if (replaced != 1)
            Common.Logger.Warning(
                "MergeBlockPhysicsPatch: expected one Physics.Enabled assignment in MyShipMergeBlock.OnAddedToScene, replaced {0}",
                replaced
            );

        return il;
    }

    private static bool IsSetPhysicsEnabled(CodeInstruction instruction)
    {
        return instruction.operand is MethodInfo { Name: "set_Enabled" } method
            && typeof(MyPhysicsComponentBase).IsAssignableFrom(method.DeclaringType);
    }

    private static void SetEnabled(MyPhysicsComponentBase physics, bool enabled)
    {
        if (physics != null || Common.Config?.Enabled != true)
            physics.Enabled = enabled; // Disabled: throws like the game does
    }
}
