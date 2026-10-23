using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shared.Rewriter;
using VRage.Scripting;
using VRageMath;

namespace VRageMathStaticsTests;

// Runs MathStaticsRewriter on small scripts compiled against the game's VRage.Math.dll, checks
// which fields it redirects, then runs the rewritten script and checks that the real fields
// kept their values while the script saw its own writes.
internal static class Program
{
    private sealed class Case
    {
        public string Name;
        public string[] Sources;
        public string[] Redirected = [];
        public string Result;
        public MyApiTarget Target = MyApiTarget.Ingame;
        public LanguageVersion Language = LanguageVersion.CSharp6;
        public string LogContains;
        public bool NoName;
        public string AssemblyName;
    }

    private static readonly Case[] Cases =
    [
        new Case
        {
            Name = "reads only: left alone",
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        static double Use(Vector3D v) { return v.X; }
                        public static string Run()
                        {
                            var z = Vector3D.Zero;
                            var x = Vector3D.Up.Y + MatrixD.Identity.M11 + Use(Vector3D.Forward) + Quaternion.Identity.W;
                            var l = Vector3D.One.Length() + MatrixD.Identity.Translation.X + Vector3D.One.Dot(Vector3D.Up);
                            var s = Vector3D.One.GetDim(4) + " " + MatrixD.Identity.Equals(MatrixD.Zero) + " " + Quaternion.Identity.Forward + " " + Vector3D.Zero.GetHashCode();
                            return z + " " + x + " " + l + " " + s;
                        }
                    }
                    """,
            ],
            Result = "X:0 Y:0 Z:0 3 2.732050807568877 1 False {X:-0 Y:0 Z:-1} 0",
        },
        new Case
        {
            Name = "assignment",
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        public static string Run()
                        {
                            Vector3D.Zero = new Vector3D(1, 0, 0);
                            return Vector3D.Zero.ToString();
                        }
                    }
                    """,
            ],
            Redirected = ["Vector3D.Zero"],
            Result = "X:1 Y:0 Z:0",
        },
        new Case
        {
            Name = "member assignment, increment and compound assignment",
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        public static string Run()
                        {
                            Vector3D.Up.X = 42;
                            MatrixD.Identity.M11++;
                            Quaternion.Identity.W *= 3;
                            Vector3.Zero += Vector3.One;
                            return Vector3D.Up.X + " " + MatrixD.Identity.M11 + " " + Quaternion.Identity.W + " " + Vector3.Zero.X;
                        }
                    }
                    """,
            ],
            Redirected = ["Vector3D.Up", "MatrixD.Identity", "Quaternion.Identity", "Vector3.Zero"],
            Result = "42 2 3 1",
        },
        new Case
        {
            Name = "ref and out arguments",
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        static void Twist(ref MatrixD m) { m.M11 = 5; }
                        static void Fill(out Quaternion q) { q = new Quaternion(1, 2, 3, 4); }
                        public static string Run()
                        {
                            Twist(ref MatrixD.Identity);
                            Fill(out Quaternion.Identity);
                            Vector3D r;
                            Vector3D.Transform(ref Vector3D.Up, ref MatrixD.Identity, out r);
                            return MatrixD.Identity.M11 + " " + Quaternion.Identity + " " + r;
                        }
                    }
                    """,
            ],
            Redirected = ["MatrixD.Identity", "Quaternion.Identity", "Vector3D.Up"],
            Result = "5 {X:1 Y:2 Z:3 W:4} X:0 Y:1 Z:0",
        },
        new Case
        {
            Name = "changing struct method",
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        public static string Run()
                        {
                            Vector3D.One.Normalize();
                            Vector3D.Up.SetDim(0, 3);
                            return Vector3D.One.X.ToString("0.000") + " " + Vector3D.Up;
                        }
                    }
                    """,
            ],
            Redirected = ["Vector3D.One", "Vector3D.Up"],
            Result = "0.577 X:3 Y:1 Z:0",
        },
        new Case
        {
            Name = "property setter on a struct field",
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        public static string Run()
                        {
                            MatrixD.Identity.Translation = new Vector3D(7, 8, 9);
                            return MatrixD.Identity.Translation.ToString();
                        }
                    }
                    """,
            ],
            Redirected = ["MatrixD.Identity"],
            Result = "X:7 Y:8 Z:9",
        },
        new Case
        {
            Name = "using static, write in one method, read in another",
            Sources =
            [
                """
                    using static VRageMath.Vector3D;
                    public static class Probe
                    {
                        static void Spoil() { Zero = Up; }
                        public static string Run()
                        {
                            Spoil();
                            return VRageMath.Vector3D.Zero + " " + Zero + " " + nameof(VRageMath.Vector3D.Zero);
                        }
                    }
                    """,
            ],
            Redirected = ["Vector3D.Zero"],
            Result = "X:0 Y:1 Z:0 X:0 Y:1 Z:0 Zero",
        },
        new Case
        {
            Name = "reference type field set to null",
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        public static string Run()
                        {
                            var before = MyLineSegmentOverlapResult<int>.DistanceComparer != null;
                            MyLineSegmentOverlapResult<int>.DistanceComparer = null;
                            return before + " " + (MyLineSegmentOverlapResult<int>.DistanceComparer == null);
                        }
                    }
                    """,
            ],
            Redirected = ["MyLineSegmentOverlapResult<int>.DistanceComparer"],
            Result = "True True",
        },
        new Case
        {
            Name = "mod with two files: the write in one file redirects the read in the other",
            Target = MyApiTarget.Mod,
            Sources =
            [
                """
                    namespace TestMod
                    {
                        public static class Writer { public static void Spoil() { VRageMath.Vector3D.Forward.Z = 5; } }
                    }
                    """,
                """
                    public static class Probe
                    {
                        public static string Run()
                        {
                            TestMod.Writer.Spoil();
                            return VRageMath.Vector3D.Forward.ToString();
                        }
                    }
                    """,
            ],
            Redirected = ["Vector3D.Forward"],
            Result = "X:0 Y:0 Z:5",
        },
        new Case
        {
            Name = "C# 7.3: ref local, deconstruction, in argument",
            Language = LanguageVersion.CSharp7_3,
            Sources =
            [
                """
                    using VRageMath;
                    public static class Probe
                    {
                        static double Read(in Vector3D v) => v.Y;
                        public static string Run()
                        {
                            ref var left = ref Vector3D.Left;
                            left.X = 9;
                            (Vector3D.Right, Vector3D.Down) = (Vector3D.Zero, Vector3D.Zero);
                            return Vector3D.Left + " " + Vector3D.Right + " " + Vector3D.Down + " " + Read(in Vector3D.Up);
                        }
                    }
                    """,
            ],
            Redirected = ["Vector3D.Left", "Vector3D.Right", "Vector3D.Down"],
            Result = "X:9 Y:0 Z:0 X:0 Y:0 Z:0 X:0 Y:0 Z:0 1",
        },
        new Case
        {
            Name = "programmable block wrapper: log names the block and the script line",
            Sources =
            [
                "using VRageMath;\npublic static class Probe\n{\n#line 1 \"Program\"\npublic static string Run()\n{\n    Vector3D.Backward = Vector3D.Zero;\n    return Vector3D.Backward.ToString();\n}\n}\n",
            ],
            Redirected = ["Vector3D.Backward"],
            Result = "X:0 Y:0 Z:0",
            LogContains =
                "PB: Test (1) may change the shared VRageMath.Vector3D.Backward, it gets its own copy instead: Program line 3: Vector3D.Backward = Vector3D.Zero;",
        },
        new Case
        {
            Name = "programmable block compiled before the name patch: named after its entity id",
            NoName = true,
            AssemblyName = "111703061066241592-.dll",
            Sources =
            [
                "using VRageMath;\npublic static class Probe\n{\n#line 1 \"Program\"\npublic static string Run()\n{\n    Vector3D.Right.Y = 2;\n    return Vector3D.Right.ToString();\n}\n}\n",
            ],
            Redirected = ["Vector3D.Right"],
            Result = "X:1 Y:2 Z:0",
            LogContains =
                "PB 111703061066241592 may change the shared VRageMath.Vector3D.Right, it gets its own copy instead: Program line 3: Vector3D.Right.Y = 2;",
        },
    ];

    private static int Main(string[] args)
    {
        if (args.Contains("--mutating"))
        {
            // Which members of the common structs the IL scan treats as changing `this`
            foreach (
                var type in new[]
                {
                    typeof(Vector3D),
                    typeof(Vector3),
                    typeof(MatrixD),
                    typeof(Matrix),
                    typeof(QuaternionD),
                    typeof(Quaternion),
                    typeof(BoundingBoxD),
                }
            )
            {
                var methods = type.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                    )
                    .OrderBy(m => m.Name);
                var mutating = methods.Where(m =>
                    StructMutation.MayMutate(type, m.Name, StructMutation.Signature(m))
                );
                Console.WriteLine(
                    $"{type.Name}: {string.Join(", ", mutating.Select(StructMutation.Signature).Select(x => x.Replace("VRageMath.", "").Replace("System.", "")))}"
                );
            }
            return 0;
        }

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            .Split(Path.PathSeparator)
            .Append(typeof(Vector3D).Assembly.Location)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var snapshot = Snapshot();
        var failures = 0;
        foreach (var test in Cases)
        {
            string problem;
            try
            {
                problem = Run(test, references, snapshot);
            }
            catch (Exception e)
            {
                problem = e.ToString();
            }

            Restore(snapshot);
            Console.WriteLine(
                $"{(problem == null ? "PASS" : "FAIL")}  {test.Name}{(problem == null ? "" : "\n      " + problem)}"
            );
            if (problem != null)
                failures++;
        }

        // Control: without the rewriter the same write reaches the real field
        var control = CSharpCompilation.Create(
            "Control",
            [
                CSharpSyntaxTree.ParseText(
                    Cases[1].Sources[0],
                    new CSharpParseOptions(LanguageVersion.CSharp6)
                ),
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using (var stream = new MemoryStream())
        {
            control.Emit(stream);
            Assembly.Load(stream.ToArray()).GetType("Probe").GetMethod("Run").Invoke(null, null);
        }
        var leaked = Vector3D.Zero.X == 1;
        Restore(snapshot);
        Console.WriteLine(
            $"{(leaked ? "PASS" : "FAIL")}  control: without the rewriter the write reaches Vector3D.Zero"
        );
        if (!leaked)
            failures++;

        var claims = CrossCheck(out var broken, out var skipped);
        var timedOut =
            skipped.Count == 0
                ? ""
                : $" ({skipped.Count} timed out: {string.Join(", ", skipped.Distinct())})";
        Console.WriteLine(
            $"{(broken.Count == 0 ? "PASS" : "FAIL")}  IL scan cross-check: {claims} calls judged not to change their struct left it unchanged{timedOut}{string.Join("", broken.Select(b => "\n      changed: " + b))}"
        );
        if (broken.Count != 0)
            failures++;

        // The same check over the calls judged to change their struct shows it can see a change
        var changing = CrossCheck(out var changed, out _, judgedChanging: true);
        var sensitive = changed.Count > 0;
        Console.WriteLine(
            $"{(sensitive ? "PASS" : "FAIL")}  cross-check control: {changed.Count} of {changing} calls judged changing did change their struct"
        );
        if (!sensitive)
            failures++;

        Console.WriteLine(
            failures == 0
                ? $"all {Cases.Length + 3} passed"
                : $"{failures} of {Cases.Length + 3} failed"
        );
        // A timed-out call may still be running
        Environment.Exit(failures == 0 ? 0 : 1);
        return 1;
    }

    private static string Run(
        Case test,
        List<MetadataReference> references,
        Dictionary<FieldInfo, object> snapshot
    )
    {
        var options = new CSharpParseOptions(test.Language, DocumentationMode.None);
        var trees = test.Sources.Select(
            (s, i) => CSharpSyntaxTree.ParseText(s, options, $"File{i}.cs")
        );
        var compilation = CSharpCompilation.Create(
            test.AssemblyName ?? "Probe" + Guid.NewGuid().ToString("N"),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count != 0)
            return "test source does not compile: " + string.Join("; ", errors);

        var log = new ListLogger();
        MathStaticsRewriter.CompilingName = test.NoName ? null : "PB: Test (1)";
        var rewritten = MathStaticsRewriter.Rewrite(compilation, test.Target, log);

        var holder = rewritten.SyntaxTrees.FirstOrDefault(t =>
            t.FilePath == MathStaticsRewriter.HolderName + ".cs"
        );
        var redirected =
            holder == null
                ? new List<string>()
                : Regex
                    .Matches(holder.ToString(), @"= global::VRageMath\.(\S+);")
                    .Select(m => m.Groups[1].Value)
                    .ToList();

        if (!redirected.OrderBy(x => x).SequenceEqual(test.Redirected.OrderBy(x => x)))
            return $"redirected [{string.Join(", ", redirected)}], expected [{string.Join(", ", test.Redirected)}]";

        if (test.Redirected.Length == 0 && !ReferenceEquals(rewritten, compilation))
            return "a compilation without writes was changed";

        if (test.LogContains != null && !log.Lines.Any(l => l.Contains(test.LogContains)))
            return "log is missing the expected line, got:\n      "
                + string.Join("\n      ", log.Lines);

        using var stream = new MemoryStream();
        var emitted = rewritten.Emit(stream);
        if (!emitted.Success)
            return "rewritten compilation does not compile: "
                + string.Join(
                    "; ",
                    emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                )
                + "\n"
                + string.Join("\n", rewritten.SyntaxTrees.Select(t => t.ToString()));

        var assembly = Assembly.Load(stream.ToArray());
        var result = (string)assembly.GetType("Probe").GetMethod("Run").Invoke(null, null);
        if (result != test.Result)
            return $"script returned \"{result}\", expected \"{test.Result}\"";

        var changed = snapshot
            .Where(p => !Same(p.Key.GetValue(null), p.Value))
            .Select(p => p.Key.DeclaringType.Name + "." + p.Key.Name)
            .ToList();
        if (changed.Count != 0)
            return "the script changed the real fields: " + string.Join(", ", changed);

        return null;
    }

    // Calls every public method StructMutation judges unable to change its struct (or, for the
    // control, able to) on each static value of that struct, with simple arguments, and lists
    // the calls that changed the value. Not a proof, but it catches a scan that misses a kind
    // of store.
    private static int CrossCheck(
        out List<string> broken,
        out List<string> skipped,
        bool judgedChanging = false
    )
    {
        broken = new List<string>();
        skipped = new List<string>();
        var claims = 0;
        var structs = typeof(Vector3D)
            .Assembly.GetTypes()
            .Where(t =>
                t.IsValueType && !t.IsEnum && !t.IsPrimitive && !t.ContainsGenericParameters
            );
        foreach (var type in structs)
        {
            var receivers = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == type)
                .Select(f => f.GetValue(null))
                .ToList();
            if (receivers.Count == 0)
                receivers.Add(Activator.CreateInstance(type));

            var methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                )
                .Where(m => !m.ContainsGenericParameters);
            foreach (var method in methods)
            {
                if (
                    StructMutation.MayMutate(type, method.Name, StructMutation.Signature(method))
                    != judgedChanging
                )
                    continue;

                foreach (var receiver in receivers)
                {
                    var boxed = receiver;
                    var before = Describe(boxed);
                    var arguments = method
                        .GetParameters()
                        .Select(p => Argument(p.ParameterType))
                        .ToArray();
                    var call = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            method.Invoke(boxed, arguments);
                        }
                        catch (TargetInvocationException) { }
                        catch (ArgumentException) { }
                    });

                    // Plane.RandomPoint never returns for some planes
                    if (!call.Wait(1000))
                    {
                        skipped.Add($"{type.Name}.{method.Name}");
                        continue;
                    }

                    claims++;
                    if (Describe(boxed) != before)
                        broken.Add($"{type.Name}.{StructMutation.Signature(method)} on {before}");
                }
            }
        }
        return claims;
    }

    private static object Argument(Type type)
    {
        if (type.IsByRef)
            type = type.GetElementType();
        if (type == typeof(string))
            return "1";
        if (type == typeof(bool))
            return true;
        if (type.IsPrimitive)
            return Convert.ChangeType(1, type);
        if (type.IsValueType)
        {
            var sample = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(f => f.FieldType == type && f.Name is "One" or "Identity" or "Up");
            return sample?.GetValue(null) ?? Activator.CreateInstance(type);
        }
        return null;
    }

    // The exact bits of a struct's fields, so -0 and NaN compare as they are stored
    private static string Describe(object value)
    {
        if (value == null)
            return "null";
        if (value is double d)
            return BitConverter.DoubleToInt64Bits(d).ToString();
        if (value is float f)
            return BitConverter.SingleToInt32Bits(f).ToString();
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || !type.IsValueType)
            return value.ToString();
        return "{"
            + string.Join(
                ",",
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(x => Describe(x.GetValue(value)))
            )
            + "}";
    }

    // Every non-readonly static field of the non-generic VRage.Math types, and of the generic
    // one the tests use
    private static Dictionary<FieldInfo, object> Snapshot()
    {
        return typeof(Vector3D)
            .Assembly.GetTypes()
            .Where(t => !t.ContainsGenericParameters)
            .Append(typeof(MyLineSegmentOverlapResult<int>))
            .SelectMany(t =>
                t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            )
            .Where(f => !f.IsInitOnly && !f.IsLiteral)
            .ToDictionary(f => f, f => f.GetValue(null));
    }

    // Equals fails on NaN components (Vector3.Invalid) and on Vector4D, so compare the text of
    // values and the identity of objects
    private static bool Same(object a, object b)
    {
        return a?.GetType().IsValueType == true
            ? a.ToString() == b?.ToString()
            : ReferenceEquals(a, b);
    }

    private static void Restore(Dictionary<FieldInfo, object> snapshot)
    {
        foreach (var pair in snapshot)
            pair.Key.SetValue(null, pair.Value);
    }
}
