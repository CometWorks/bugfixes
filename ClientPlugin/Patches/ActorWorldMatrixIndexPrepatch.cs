using System;
using System.Linq;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ClientPlugin.Patches;

// Cecil prepatch, invoked from Preloader.Patch. Not a Harmony patch: both
// MyActor.UpdateWorldMatrix and its caller MyInstance.UpdateWorldMatrix are
// AggressiveInlining, so a Harmony patch on the actor would be bypassed by
// already inlined copies. Rewriting the IL before the assembly loads has no
// such gap. See Docs/actor-world-matrix-index.md.
public static class ActorWorldMatrixIndexPrepatch
{
    private const string ActorType = "VRage.Render.Scene.MyActor";
    private const string IndexField = "m_worldMatrixIndex";

    private static readonly string[] Methods =
    {
        "SetWorldMatrix",
        "SetWorldMatrixDirty",
        "UpdateWorldMatrix",
    };

    // Returns the number of increments replaced, 0 if the game code changed.
    public static int Prepatch(AssemblyDefinition asmDef)
    {
        if (asmDef.Name.Name != "VRage.Render")
            return 0;

        var type = asmDef.MainModule.GetType(ActorType);
        var field = type?.Fields.FirstOrDefault(f => f.Name == IndexField);
        if (field == null)
            return Skip("MyActor.m_worldMatrixIndex not found");

        // Find every increment first, so a partial match changes nothing.
        var sites = Methods
            .Select(name => type.Methods.FirstOrDefault(m => m.Name == name && m.HasBody))
            .Select(method => (method, index: method == null ? -1 : FindIncrement(method, field)))
            .ToList();
        if (sites.Any(s => s.index < 0))
            return Skip("an increment of m_worldMatrixIndex was not found");

        var increment = asmDef.MainModule.ImportReference(
            typeof(Interlocked).GetMethod(
                nameof(Interlocked.Increment),
                new[] { typeof(int).MakeByRefType() }
            )
        );

        foreach (var (method, index) in sites)
        {
            // ldarg.0; ldarg.0; ldfld; ldc.i4.1; add; stfld
            // becomes
            // ldarg.0; ldflda; call Interlocked.Increment; pop; nop; nop
            // The first instruction stays, so branches into the sequence still land on it.
            var il = method.Body.Instructions;
            il[index + 1].OpCode = OpCodes.Ldflda;
            il[index + 1].Operand = field;
            il[index + 2].OpCode = OpCodes.Call;
            il[index + 2].Operand = increment;
            il[index + 3].OpCode = OpCodes.Pop;
            il[index + 3].Operand = null;
            il[index + 4].OpCode = OpCodes.Nop;
            il[index + 5].OpCode = OpCodes.Nop;
            il[index + 5].Operand = null;
        }

        return sites.Count;
    }

    private static int FindIncrement(MethodDefinition method, FieldDefinition field)
    {
        var il = method.Body.Instructions;
        var found = -1;
        for (var i = 0; i + 5 < il.Count; i++)
        {
            if (
                il[i].OpCode == OpCodes.Ldarg_0
                && il[i + 1].OpCode == OpCodes.Ldarg_0
                && il[i + 2].OpCode == OpCodes.Ldfld
                && il[i + 2].Operand == field
                && il[i + 3].OpCode == OpCodes.Ldc_I4_1
                && il[i + 4].OpCode == OpCodes.Add
                && il[i + 5].OpCode == OpCodes.Stfld
                && il[i + 5].Operand == field
            )
            {
                // Expect exactly one increment per method.
                if (found >= 0)
                    return -1;
                found = i;
            }
        }

        return found;
    }

    private static int Skip(string reason)
    {
        Console.WriteLine($"[Bugfixes] Actor world matrix index fix not applied, {reason}");
        return 0;
    }
}
