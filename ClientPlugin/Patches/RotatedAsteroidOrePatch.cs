using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRageMath;

namespace ClientPlugin.Patches;

[HarmonyPatch]
[HarmonyPatchCategory("Client")]
public static class RotatedAsteroidOrePatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method("Sandbox.Game.Entities.Cube.MyOreDepositGroup:UpdateDeposits");

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var corner = AccessTools.PropertyGetter(
            typeof(MyVoxelBase),
            nameof(MyVoxelBase.PositionLeftBottomCorner)
        );
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(corner))
            {
                yield return new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(instruction);
                yield return CodeInstruction.Call(
                    typeof(RotatedAsteroidOrePatch),
                    nameof(SearchOrigin)
                );
                replaced++;
            }
            else
                yield return instruction;
        }
        if (replaced != 2)
            throw new InvalidOperationException(
                $"Expected two ore search bounds, found {replaced}."
            );
    }

    private static Vector3D SearchOrigin(MyVoxelBase map, ref BoundingSphereD sphere)
    {
        var corner = map.PositionLeftBottomCorner;
        if (!(map is MyVoxelMap))
            return corner;

        // The game subtracts this origin from center ± radius to get the storage bounds.
        // Rotate the center offset into storage axes. A sphere has the same radius in either frame.
        var storageOffset = Vector3D.TransformNormal(
            sphere.Center - corner,
            map.PositionComp.WorldMatrixInvScaled
        );
        return sphere.Center - storageOffset;
    }
}
