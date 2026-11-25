using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Havok;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Shared.Plugin;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace Shared.Patches;

// Client + Server
//
// Keeps the block placement test from leaking Havok shapes. See Docs/placement-test-shape-leak.md.
//
// MyCubeGrid.TestBlockPlacementArea (the overload with 16 parameters) builds a shape of the block
// and releases it at the end. For a block whose model has no collision shapes and whose cube
// topology is not a StandaloneBox, like armor blocks, it builds fresh convex shapes with
// MyCubeBlockCollector.AddConvexShape and wraps them into an HkListShape with
// HkReferencePolicy.None. The list then holds a reference of its own on each child, and
// releasing the list drops only that one: every call leaks one or two convex shapes. The hand
// welder and the ship welders run it on projected blocks, and so does Multigrid Projector.
// A client next to a repair projection of a damaged ship ran out of Havok memory (SE-1381).
//
// Also, when the block intersects a wheel, the method returns false before it releases the
// shape at all.
//
// The transpiler makes the list take over the children (TakeOwnership), as the game does
// elsewhere for freshly made child shapes, and releases the shape on the wheel return.
//
// ReSharper disable once UnusedType.Global
[HarmonyPatch]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class PlacementTestShapeLeakPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools
            .GetDeclaredMethods(typeof(MyCubeGrid))
            .Where(m => m.Name == nameof(MyCubeGrid.TestBlockPlacementArea))
            .OrderByDescending(m => m.GetParameters().Length)
            .First();

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var il = instructions.ToList();

        var addConvexShape = AccessTools.Method(
            "Sandbox.Game.Entities.Cube.MyCubeBlockCollector:AddConvexShape",
            new[]
            {
                typeof(Vector3I),
                typeof(Vector3I),
                typeof(MyCubeBlockDefinition),
                typeof(MyBlockOrientation),
                typeof(float),
                typeof(List<HkShape>),
            }
        );
        var listShape = AccessTools.Constructor(
            typeof(HkListShape),
            new[] { typeof(HkShape[]), typeof(int), typeof(HkReferencePolicy) }
        );
        var start = il.FindIndex(i => i.Calls(addConvexShape));
        var ctor =
            start < 0
                ? -1
                : il.FindIndex(
                    start,
                    i => i.opcode == OpCodes.Newobj && Equals(i.operand, listShape)
                );
        var policy = ctor < 1 ? null : il[ctor - 1];
        if (policy?.opcode == OpCodes.Ldc_I4_1)
        {
            policy.opcode = OpCodes.Call;
            policy.operand = AccessTools.Method(
                typeof(PlacementTestShapeLeakPatch),
                nameof(ConvexChildrenPolicy)
            );
        }
        else
            Common.Logger.Warning(
                "PlacementTestShapeLeakPatch: no list shape with HkReferencePolicy.None after AddConvexShape in MyCubeGrid.TestBlockPlacementArea"
            );

        List<CodeInstruction> wheelCalls =
            IntersectsWithWheelMethod == null
                ? []
                : il.Where(i => i.Calls(IntersectsWithWheelMethod)).ToList();
        foreach (var call in wheelCalls)
            call.operand = AccessTools.Method(
                typeof(PlacementTestShapeLeakPatch),
                nameof(IntersectsWithWheel)
            );
        if (wheelCalls.Count != 1)
            Common.Logger.Warning(
                "PlacementTestShapeLeakPatch: expected one IntersectsWithWheel call in MyCubeGrid.TestBlockPlacementArea, replaced {0}",
                wheelCalls.Count
            );

        return il;
    }

    private delegate bool IntersectsWithWheelDelegate(
        ref MatrixD worldMatrix,
        ref Quaternion rotation,
        ref BoundingBoxD localAabb,
        ref HkShape shape,
        ref Quaternion shapeRotation,
        MyEntity ignoredEntity
    );

    private static readonly MethodInfo IntersectsWithWheelMethod = AccessTools.Method(
        typeof(MyCubeGrid),
        "IntersectsWithWheel"
    );

    // Null when the game has no such method anymore; then nothing calls the replacement either
    private static readonly IntersectsWithWheelDelegate OriginalIntersectsWithWheel =
        IntersectsWithWheelMethod == null
            ? null
            : AccessTools.MethodDelegate<IntersectsWithWheelDelegate>(IntersectsWithWheelMethod);

    private static HkReferencePolicy ConvexChildrenPolicy() =>
        Common.Config?.Enabled == true ? HkReferencePolicy.TakeOwnership : HkReferencePolicy.None;

    // The game returns false right after a true result, without releasing the shape
    private static bool IntersectsWithWheel(
        ref MatrixD worldMatrix,
        ref Quaternion rotation,
        ref BoundingBoxD localAabb,
        ref HkShape shape,
        ref Quaternion shapeRotation,
        MyEntity ignoredEntity
    )
    {
        var intersects = OriginalIntersectsWithWheel(
            ref worldMatrix,
            ref rotation,
            ref localAabb,
            ref shape,
            ref shapeRotation,
            ignoredEntity
        );
        if (intersects && Common.Config?.Enabled == true)
            shape.RemoveReference();
        return intersects;
    }
}
