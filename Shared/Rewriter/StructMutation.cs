using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Shared.Rewriter;

// Tells from the IL of the game's struct methods whether a call may change the struct it is
// called on.
//
// A call on a static struct field passes the field's address as `this`, so a method that
// stores through `this` changes the field. The scan follows that address, and the addresses
// of its fields, through the evaluation stack of the method:
// - Reading through them (ldfld, ldobj, ldind) is fine.
// - Passing them to another method, as its `this` or as a ref parameter, is fine if that
//   method doesn't change that argument either, which is scanned the same way. Methods of
//   primitives never change their `this`.
// - Anything else counts as a change: storing through them, keeping them in a local,
//   returning them, carrying them across a branch, recursion other than a method calling
//   itself on the same argument, a method without IL, or an instruction the scan doesn't
//   model. A wrong answer can only be "may change", never the other way round.
public static class StructMutation
{
    private const BindingFlags InstanceMethods =
        BindingFlags.Instance
        | BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;

    private static readonly object Lock = new();
    private static readonly Dictionary<(MethodBase, int), bool> Results = new();
    private static readonly HashSet<(MethodBase, int)> InProgress = new();
    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

    // True if calling this method, property accessor or indexer accessor on a struct may change
    // it. The signature, as Signature() writes it, picks one overload; without it, or if it
    // matches none, all overloads of the name are judged together. Unknown methods count as
    // changing.
    public static bool MayMutate(Type structType, string methodName, string signature = null)
    {
        if (structType == null || !structType.IsValueType)
            return true;

        if (structType.IsPrimitive)
            return false;

        var overloads = structType
            .GetMethods(InstanceMethods)
            .Where(m => m.Name == methodName)
            .ToList();
        if (overloads.Count == 0)
            return true;

        var exact =
            signature == null ? null : overloads.FirstOrDefault(m => Signature(m) == signature);
        if (exact != null)
            return MayChangeArgument(exact, 0);

        return overloads.Any(m => MayChangeArgument(m, 0));
    }

    // Name(Namespace.Type,Namespace.Type&), with ref, in and out parameters marked by &
    public static string Signature(MethodBase method) =>
        method.Name
        + "("
        + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName))
        + ")";

    // May the method change what its IL argument `index` points to? Argument 0 of an instance
    // method is `this`.
    private static bool MayChangeArgument(MethodBase method, int index)
    {
        if (method.DeclaringType is { IsPrimitive: true } && !method.IsStatic && index == 0)
            return false;

        var key = (method, index);
        lock (Lock)
        {
            if (Results.TryGetValue(key, out var known))
                return known;

            // Recursion: assume the worst rather than proving it terminates
            if (!InProgress.Add(key))
                return true;

            bool changes;
            try
            {
                changes = new Scan(method, index).ChangesArgument();
            }
            catch (Exception)
            {
                changes = true;
            }
            finally
            {
                InProgress.Remove(key);
            }

            Results[key] = changes;
            return changes;
        }
    }

    // What the scan knows about a value on the evaluation stack
    private enum Value
    {
        Other,
        Tracked,
    }

    private sealed class Scan
    {
        private readonly MethodBase method;
        private readonly int tracked;
        private readonly byte[] il;
        private readonly Dictionary<int, List<Value>> stackAt = new();

        private List<Value> stack = new();
        private bool reachable = true;
        private Type constrained;

        public Scan(MethodBase method, int tracked)
        {
            this.method = method;
            this.tracked = tracked;
            var body = method.GetMethodBody();
            il = body?.GetILAsByteArray();

            foreach (var clause in body?.ExceptionHandlingClauses ?? [])
            {
                var exception =
                    clause.Flags
                    is ExceptionHandlingClauseOptions.Clause
                        or ExceptionHandlingClauseOptions.Filter;
                stackAt[clause.HandlerOffset] = exception ? [Value.Other] : [];
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                    stackAt[clause.FilterOffset] = [Value.Other];
            }
        }

        public bool ChangesArgument()
        {
            if (il == null)
                return true;

            var position = 0;
            while (position < il.Length)
            {
                if (stackAt.TryGetValue(position, out var recorded))
                {
                    if (reachable && recorded.Count != stack.Count)
                        return true;
                    stack = new List<Value>(recorded);
                    reachable = true;
                }
                else if (!reachable)
                {
                    // Only reached by a backward branch or not at all; nothing tracked crosses
                    // a branch, so the scan just needs a stack to go on with
                    stack = new List<Value>();
                    reachable = true;
                }

                if (
                    !TryRead(il, ref position, out var opCode, out var operand)
                    || Step(opCode, operand, position)
                )
                    return true;
            }

            return false;
        }

        // Returns true if the instruction may change the tracked argument
        private bool Step(OpCode op, int operand, int next)
        {
            if (op == OpCodes.Constrained)
            {
                constrained = method.Module.ResolveType(
                    operand,
                    TypeArguments(),
                    MethodArguments()
                );
                return false;
            }

            var constrainedType = constrained;
            constrained = null;

            if (ArgumentIndex(op, operand) == tracked)
            {
                if (
                    op == OpCodes.Ldarg
                    || op == OpCodes.Ldarg_S
                    || op == OpCodes.Ldarg_0
                    || op == OpCodes.Ldarg_1
                    || op == OpCodes.Ldarg_2
                    || op == OpCodes.Ldarg_3
                )
                {
                    stack.Add(Value.Tracked);
                    return false;
                }

                // ldarga, starg
                return true;
            }

            if (op == OpCodes.Dup)
            {
                if (stack.Count == 0)
                    return true;
                stack.Add(stack[stack.Count - 1]);
                return false;
            }

            if (op == OpCodes.Ldflda)
            {
                if (!Pop(out var target))
                    return true;
                stack.Add(target);
                return false;
            }

            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
                return Call(op, operand, constrainedType);

            if (op == OpCodes.Calli || op == OpCodes.Jmp)
                return true;

            if (op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)
                return Branch(op, operand, next);

            if (op == OpCodes.Ret)
            {
                var returns = method is MethodInfo info && info.ReturnType != typeof(void);
                if (returns && (!Pop(out var returned) || returned != Value.Other))
                    return true;
                reachable = false;
                return false;
            }

            var reads =
                op == OpCodes.Ldfld
                || op == OpCodes.Ldobj
                || op.Name.StartsWith("ldind", StringComparison.Ordinal);
            var pops = PopCount(op);
            if (pops < 0 || stack.Count < pops)
                return true;

            for (var i = 0; i < pops; i++)
            {
                Pop(out var popped);
                if (popped != Value.Other && !reads && op != OpCodes.Pop)
                    return true;
            }

            for (var i = PushCount(op); i > 0; i--)
                stack.Add(Value.Other);

            // throw, rethrow, endfinally and endfilter
            if (op.FlowControl is FlowControl.Throw or FlowControl.Return)
                reachable = false;

            return false;
        }

        private bool Call(OpCode op, int operand, Type constrainedType)
        {
            var callee = method.Module.ResolveMethod(operand, TypeArguments(), MethodArguments());
            var hasThis = !callee.IsStatic && op != OpCodes.Newobj;
            var count = callee.GetParameters().Length + (hasThis ? 1 : 0);
            if (stack.Count < count)
                return true;

            var arguments = stack.GetRange(stack.Count - count, count);
            stack.RemoveRange(stack.Count - count, count);

            for (var i = 0; i < count; i++)
            {
                if (arguments[i] == Value.Other)
                    continue;

                // A constructor's IL arguments start with the new object
                var ilIndex = op == OpCodes.Newobj ? i + 1 : i;

                // Calling itself on the same argument changes nothing the rest of this method
                // doesn't, like GetDim(i) calling GetDim(i % 3)
                if (callee == method && ilIndex == tracked)
                    continue;

                if (hasThis && i == 0)
                {
                    if (CallChangesThis(callee, constrainedType))
                        return true;
                    continue;
                }

                if (MayChangeArgument(callee, ilIndex))
                    return true;
            }

            if (
                op == OpCodes.Newobj
                || (callee is MethodInfo info && info.ReturnType != typeof(void))
            )
            {
                // A ref return may point into what was passed in
                var refReturn =
                    callee is MethodInfo { ReturnType.IsByRef: true }
                    && arguments.Contains(Value.Tracked);
                stack.Add(refReturn ? Value.Tracked : Value.Other);
            }

            return false;
        }

        private static bool CallChangesThis(MethodBase callee, Type constrainedType)
        {
            // constrained. callvirt Object::ToString and the like: the struct's own override, or
            // a boxed copy when it has none
            if (constrainedType != null && callee.DeclaringType is { IsValueType: false })
            {
                if (!constrainedType.IsValueType)
                    return true;

                var own = constrainedType
                    .GetMethods(InstanceMethods)
                    .FirstOrDefault(m =>
                        m.Name == callee.Name && Signature(m) == Signature(callee)
                    );
                return own != null && MayChangeArgument(own, 0);
            }

            return callee.DeclaringType is not { IsValueType: true }
                || MayChangeArgument(callee, 0);
        }

        private bool Branch(OpCode op, int operand, int next)
        {
            var pops = PopCount(op);
            if (pops < 0 || stack.Count < pops)
                return true;
            stack.RemoveRange(stack.Count - pops, pops);

            if (stack.Contains(Value.Tracked))
                return true;

            if (op == OpCodes.Switch)
            {
                // The targets are the last operand * 4 bytes before the next instruction
                for (var i = 0; i < operand; i++)
                    stackAt[next + BitConverter.ToInt32(il, next - 4 * (operand - i))] =
                        new List<Value>(stack);
                return false;
            }

            if (op == OpCodes.Leave || op == OpCodes.Leave_S)
                stack.Clear();
            stackAt[next + operand] = new List<Value>(stack);

            if (op.FlowControl == FlowControl.Branch)
                reachable = false;

            return false;
        }

        private bool Pop(out Value value)
        {
            value = Value.Other;
            if (stack.Count == 0)
                return false;
            value = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return true;
        }

        private Type[] TypeArguments() =>
            method.DeclaringType is { IsGenericType: true } type
                ? type.GetGenericArguments()
                : null;

        private Type[] MethodArguments() =>
            method.IsGenericMethod ? method.GetGenericArguments() : null;
    }

    // The argument index an ldarg, ldarga or starg instruction refers to, -1 for others
    private static int ArgumentIndex(OpCode op, int operand)
    {
        if (op == OpCodes.Ldarg_0)
            return 0;
        if (op == OpCodes.Ldarg_1)
            return 1;
        if (op == OpCodes.Ldarg_2)
            return 2;
        if (op == OpCodes.Ldarg_3)
            return 3;
        if (
            op == OpCodes.Ldarg
            || op == OpCodes.Ldarg_S
            || op == OpCodes.Ldarga
            || op == OpCodes.Ldarga_S
            || op == OpCodes.Starg
            || op == OpCodes.Starg_S
        )
            return operand;
        return -1;
    }

    // Number of values the instruction pops, -1 if it depends on more than the opcode
    private static int PopCount(OpCode op)
    {
        switch (op.StackBehaviourPop)
        {
            case StackBehaviour.Pop0:
                return 0;
            case StackBehaviour.Pop1:
            case StackBehaviour.Popi:
            case StackBehaviour.Popref:
                return 1;
            case StackBehaviour.Pop1_pop1:
            case StackBehaviour.Popi_pop1:
            case StackBehaviour.Popi_popi:
            case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4:
            case StackBehaviour.Popi_popr8:
            case StackBehaviour.Popref_pop1:
            case StackBehaviour.Popref_popi:
                return 2;
            case StackBehaviour.Popi_popi_popi:
            case StackBehaviour.Popref_popi_popi:
            case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8:
            case StackBehaviour.Popref_popi_popref:
            case StackBehaviour.Popref_popi_pop1:
                return 3;
            default:
                return -1;
        }
    }

    private static int PushCount(OpCode op)
    {
        switch (op.StackBehaviourPush)
        {
            case StackBehaviour.Push0:
                return 0;
            case StackBehaviour.Push1_push1:
                return 2;
            default:
                return 1;
        }
    }

    private static bool TryRead(byte[] il, ref int position, out OpCode opCode, out int operand)
    {
        operand = 0;
        opCode = default;

        short value = il[position++];
        if (value == 0xFE)
        {
            if (position >= il.Length)
                return false;
            value = unchecked((short)(0xFE00 | il[position++]));
        }

        if (!OpCodesByValue.TryGetValue(value, out opCode))
            return false;

        int size;
        switch (opCode.OperandType)
        {
            case OperandType.InlineNone:
                size = 0;
                break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                size = 1;
                break;
            case OperandType.InlineVar:
                size = 2;
                break;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                size = 8;
                break;
            case OperandType.InlineSwitch:
                if (position + 4 > il.Length)
                    return false;
                size = 4 + 4 * BitConverter.ToInt32(il, position);
                break;
            default:
                size = 4;
                break;
        }

        if (size < 0 || position + size > il.Length)
            return false;

        if (
            opCode.OperandType == OperandType.ShortInlineBrTarget
            || opCode.OperandType == OperandType.ShortInlineI
        )
            operand = (sbyte)il[position];
        else if (size == 1)
            operand = il[position];
        else if (size == 2)
            operand = BitConverter.ToUInt16(il, position);
        else if (opCode.OperandType == OperandType.InlineSwitch)
            operand = BitConverter.ToInt32(il, position);
        else if (size == 4)
            operand = BitConverter.ToInt32(il, position);

        position += size;
        return true;
    }

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
                table[opCode.Value] = opCode;
        }
        return table;
    }
}
