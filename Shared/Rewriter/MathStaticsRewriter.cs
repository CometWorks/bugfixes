using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shared.Logging;
using VRage.Scripting;

namespace Shared.Rewriter;

// Client + Server
//
// Keeps programmable block scripts and mods from changing the static fields of the VRage.Math
// types for the whole game. See Docs/vragemath-statics.md.
//
// Fields like Vector3D.Zero, MatrixD.Identity or Quaternion.Identity are not readonly, and the
// script whitelist allows the whole VRageMath namespace without telling reads from writes. A
// script assigning one, or passing it by ref, changes it for every other script, every mod
// and the game itself, until the game exits.
//
// Pulsar and Magnetar pass every script and mod compilation to the plugin's Rewrite method
// before the game's own rewriters and the whitelist check run. For each such field the
// compilation may write, every use of that field in the compilation is redirected to a copy
// in a class added to the compilation, initialized from the real field. A write then changes
// only the script's own copy, and the script still sees its own writes everywhere. Fields the
// compilation only reads are left alone, and so is a compilation that writes none.
//
// A use may write when it is assigned, incremented or decremented, passed by ref or out, taken
// by ref, deconstructed into, or has a member of it written. Calling a method, property or
// indexer on a struct field passes the field by address, so that counts as a write unless the
// IL of that overload shows it never changes `this` (StructMutation). Each possible write is
// logged, up to MaxLoggedWrites per compilation.
public static class MathStaticsRewriter
{
    public const string HolderName = "__BugfixesVRageMathStatics";
    private const string MathAssemblyName = "VRage.Math";
    private const int MaxLoggedWrites = 20;

    // The friendly name of the script or mod being compiled, set by ScriptCompilerNamePatch
    [ThreadStatic]
    public static string CompilingName;

    private sealed class Use
    {
        public SyntaxTree Tree;
        public ExpressionSyntax Node;
        public IFieldSymbol Field;
        public bool MayWrite;
    }

    public static CSharpCompilation Rewrite(
        CSharpCompilation compilation,
        MyApiTarget target,
        IPluginLogger log
    )
    {
        var name = CompilingName;
        CompilingName = null;

        if (target != MyApiTarget.Ingame && target != MyApiTarget.Mod)
            return compilation;

        var uses = FindUses(compilation);
        var redirected = new HashSet<IFieldSymbol>(
            uses.Where(u => u.MayWrite).Select(u => u.Field)
        );
        if (redirected.Count == 0)
            return compilation;

        LogWrites(
            log,
            string.IsNullOrEmpty(name) ? NameFromAssembly(compilation.AssemblyName, target) : name,
            uses.Where(u => u.MayWrite).ToList()
        );

        foreach (var group in uses.Where(u => redirected.Contains(u.Field)).GroupBy(u => u.Tree))
        {
            var replacements = group.ToDictionary(
                u => (SyntaxNode)u.Node,
                u => HolderReference(u.Field)
            );
            var root = group
                .Key.GetRoot()
                .ReplaceNodes(
                    replacements.Keys,
                    (original, _) => replacements[original].WithTriviaFrom(original)
                );
            compilation = compilation.ReplaceSyntaxTree(
                group.Key,
                group.Key.WithRootAndOptions(root, group.Key.Options)
            );
        }

        var options = compilation.SyntaxTrees.First().Options;
        var holder = CSharpSyntaxTree.ParseText(
            HolderSource(redirected),
            (CSharpParseOptions)options,
            HolderName + ".cs",
            Encoding.UTF8
        );
        return compilation.AddSyntaxTrees(holder);
    }

    private static List<Use> FindUses(CSharpCompilation compilation)
    {
        var uses = new List<Use>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel model = null;
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                // Cheap filter before binding: the field names are all capitalized words
                if (
                    name is not IdentifierNameSyntax
                    || name.Identifier.ValueText.Length == 0
                    || !char.IsUpper(name.Identifier.ValueText[0])
                )
                    continue;

                model ??= compilation.GetSemanticModel(tree);
                if (
                    model.GetSymbolInfo(name).Symbol is not IFieldSymbol field
                    || !IsSharedMathStatic(field)
                    || IsInNameOf(name)
                )
                    continue;

                ExpressionSyntax node =
                    name.Parent is MemberAccessExpressionSyntax access && access.Name == name
                        ? access
                        : name;
                uses.Add(
                    new Use
                    {
                        Tree = tree,
                        Node = node,
                        Field = field,
                        MayWrite = MayWrite(node, model),
                    }
                );
            }
        }
        return uses;
    }

    private static bool IsSharedMathStatic(IFieldSymbol field)
    {
        return field.IsStatic
            && !field.IsReadOnly
            && !field.IsConst
            && field.ContainingAssembly?.Name == MathAssemblyName
            && !HasTypeParameters(field.ContainingType);
    }

    // A field of an open generic type, like MyLineSegmentOverlapResult<T>.DistanceComparer
    // inside a generic method, can't get a copy in a non-generic holder
    private static bool HasTypeParameters(INamedTypeSymbol type)
    {
        for (; type != null; type = type.ContainingType)
        {
            if (type.TypeArguments.Any(a => a.TypeKind == TypeKind.TypeParameter))
                return true;
        }
        return false;
    }

    private static bool IsInNameOf(SyntaxNode node)
    {
        foreach (var invocation in node.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
                return true;
        }
        return false;
    }

    // May this use of the field change it? Undecidable cases answer yes.
    private static bool MayWrite(ExpressionSyntax node, SemanticModel model)
    {
        var expression = node;
        while (true)
        {
            var parent = expression.Parent;
            if (parent is ParenthesizedExpressionSyntax parenthesized)
            {
                expression = parenthesized;
                continue;
            }

            if (parent is MemberAccessExpressionSyntax access && access.Expression == expression)
            {
                var receiver = model.GetTypeInfo(expression).Type;
                if (receiver == null)
                    return true;

                // Members of an object can't replace the field holding it
                if (!receiver.IsValueType)
                    return false;

                var member = model.GetSymbolInfo(access).Symbol;
                if (member is IFieldSymbol { IsStatic: false })
                {
                    expression = access;
                    continue;
                }

                return MemberMayWrite(member, access, receiver);
            }

            if (parent is ElementAccessExpressionSyntax element && element.Expression == expression)
            {
                var receiver = model.GetTypeInfo(expression).Type;
                if (receiver == null)
                    return true;
                if (!receiver.IsValueType)
                    return false;

                return MemberMayWrite(model.GetSymbolInfo(element).Symbol, element, receiver);
            }

            if (
                parent is ConditionalAccessExpressionSyntax conditional
                && conditional.Expression == expression
            )
                return model.GetTypeInfo(expression).Type?.IsValueType != false;

            return IsWritten(expression);
        }
    }

    private static bool MemberMayWrite(
        ISymbol member,
        ExpressionSyntax access,
        ITypeSymbol receiver
    )
    {
        switch (member)
        {
            case IMethodSymbol { MethodKind: MethodKind.ReducedExtension } extension:
                var refKind = extension.ReducedFrom?.Parameters.FirstOrDefault()?.RefKind;
                return refKind != RefKind.None && refKind != RefKind.In;

            case IMethodSymbol { IsStatic: false } method:
                return AccessorMayWrite(receiver, method);

            case IPropertySymbol { IsStatic: false } property:
                // Assigning a property, compound assignments included, calls its setter
                if (IsWritten(access))
                    return true;
                return property.GetMethod == null || AccessorMayWrite(receiver, property.GetMethod);

            default:
                return true;
        }
    }

    // Members inherited from ValueType or object work on a boxed copy
    private static bool AccessorMayWrite(ITypeSymbol receiver, IMethodSymbol method)
    {
        if (!Equals(method.ContainingType?.OriginalDefinition, receiver.OriginalDefinition))
            return false;

        return StructMutation.MayMutate(
            RuntimeType(receiver),
            method.MetadataName,
            Signature(method)
        );
    }

    private static Type RuntimeType(ITypeSymbol type)
    {
        var name = MetadataFullName(type);
        if (name == null || type.ContainingAssembly == null)
            return null;

        var assemblyName = type.ContainingAssembly.Name;
        var assembly = AppDomain
            .CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == assemblyName);
        return assembly?.GetType(name, false);
    }

    // The same text StructMutation.Signature makes from the runtime method, or null when a
    // parameter type is one this doesn't spell the runtime's way (generics, pointers)
    private static string Signature(IMethodSymbol method)
    {
        var parameters = new List<string>();
        foreach (var parameter in method.Parameters)
        {
            var name = MetadataFullName(parameter.Type);
            if (name == null)
                return null;
            parameters.Add(parameter.RefKind == RefKind.None ? name : name + "&");
        }
        return method.MetadataName + "(" + string.Join(",", parameters) + ")";
    }

    // Namespace.Outer+Inner like Type.FullName, for non-generic types and their arrays
    private static string MetadataFullName(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            var element = MetadataFullName(array.ElementType);
            return element == null ? null : element + "[]";
        }

        if (
            type is not INamedTypeSymbol named
            || named.IsGenericType
            || named.ContainingType?.IsGenericType == true
        )
            return null;

        var name = named.MetadataName;
        for (var outer = named.ContainingType; outer != null; outer = outer.ContainingType)
            name = outer.MetadataName + "+" + name;

        return named.ContainingNamespace?.IsGlobalNamespace == false
            ? named.ContainingNamespace.ToDisplayString() + "." + name
            : name;
    }

    private static bool IsWritten(ExpressionSyntax expression)
    {
        switch (expression.Parent)
        {
            case AssignmentExpressionSyntax assignment:
                return assignment.Left == expression;

            case PrefixUnaryExpressionSyntax prefix:
                return prefix.IsKind(SyntaxKind.PreIncrementExpression)
                    || prefix.IsKind(SyntaxKind.PreDecrementExpression)
                    || prefix.IsKind(SyntaxKind.AddressOfExpression);

            case PostfixUnaryExpressionSyntax postfix:
                return postfix.IsKind(SyntaxKind.PostIncrementExpression)
                    || postfix.IsKind(SyntaxKind.PostDecrementExpression);

            case RefExpressionSyntax:
                return true;

            case ArgumentSyntax argument:
                if (
                    argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword)
                    || argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)
                )
                    return true;
                return argument.Parent is TupleExpressionSyntax tuple && IsDeconstructedInto(tuple);

            default:
                return false;
        }
    }

    private static bool IsDeconstructedInto(TupleExpressionSyntax tuple)
    {
        while (tuple.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax outer })
            tuple = outer;

        return tuple.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == tuple,
            ForEachVariableStatementSyntax forEach => forEach.Variable == tuple,
            _ => false,
        };
    }

    private static ExpressionSyntax HolderReference(IFieldSymbol field)
    {
        return SyntaxFactory.ParseExpression(
            $"global::{HolderName}.{HolderClassName(field.ContainingType)}.{field.Name}"
        );
    }

    private static string HolderClassName(INamedTypeSymbol type)
    {
        return Regex.Replace(type.ToDisplayString(), "[^A-Za-z0-9_]", "_");
    }

    private static string HolderSource(IEnumerable<IFieldSymbol> fields)
    {
        var source = new StringBuilder();
        source.AppendLine($"internal static class {HolderName}");
        source.AppendLine("{");
        foreach (
            var type in fields
                .GroupBy(f => f.ContainingType)
                .OrderBy(g => HolderClassName(g.Key), StringComparer.Ordinal)
        )
        {
            var typeName = type.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            source.AppendLine($"    internal static class {HolderClassName(type.Key)}");
            source.AppendLine("    {");
            foreach (var field in type.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var fieldType = field.Type.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                );
                source.AppendLine(
                    $"        internal static {fieldType} {field.Name} = {typeName}.{field.Name};"
                );
            }
            source.AppendLine("    }");
        }
        source.AppendLine("}");
        return source.ToString();
    }

    // Scripts compiled during world load, before the plugin's Init applied ScriptCompilerNamePatch,
    // get no friendly name. A programmable block's assembly is named after its entity id.
    private static string NameFromAssembly(string assemblyName, MyApiTarget target)
    {
        var entityId = Regex.Match(assemblyName ?? "", @"^(\d+)-");
        return target == MyApiTarget.Ingame && entityId.Success
            ? "PB " + entityId.Groups[1].Value
            : assemblyName;
    }

    private static void LogWrites(IPluginLogger log, string name, List<Use> writes)
    {
        foreach (var use in writes.Take(MaxLoggedWrites))
        {
            var span = use.Tree.GetMappedLineSpan(use.Node.Span);
            var line = use
                .Tree.GetText()
                .Lines[use.Tree.GetLineSpan(use.Node.Span).StartLinePosition.Line]
                .ToString()
                .Trim();
            var where = string.IsNullOrEmpty(span.Path) ? "" : span.Path + " ";
            log.Info(
                $"{name} may change the shared {use.Field.ContainingType.ToDisplayString()}.{use.Field.Name}, it gets its own copy instead: {where}line {span.StartLinePosition.Line + 1}: {line}"
            );
        }

        if (writes.Count > MaxLoggedWrites)
            log.Info(
                $"{name}: {writes.Count - MaxLoggedWrites} more uses that may change shared VRage.Math fields"
            );
    }
}
