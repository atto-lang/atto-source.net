using Mono.Cecil;
using Mono.Cecil.Cil;
using Ores.Net.Compiler;

/// <summary>
/// Verify the *generated CIL artifact* in addition to source admission.
/// Hosted execution in NativeAOT is a separate evidence gate.
/// </summary>
internal static class ActorNominalCilSelfTest
{
    public static void Run()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory,
            "actor-cil-v1", "counter.atto");
        string source = File.ReadAllText(fixture);
        var admitted = ActorScalarSourceCompiler.Parse(source);
        Require(admitted.ActorName == "Counter" &&
            admitted.FieldName == "value" &&
            admitted.MethodName == "increment" &&
            admitted.InitialValue == 0,
            "canonical actor declaration has typed state and method identity");

        string root = Path.Combine(Path.GetTempPath(),
            "atto-actor-m0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "a", "OresGenerated.dll");
            string second = Path.Combine(root, "b", "OresGenerated.dll");
            var compiled = ActorScalarSourceCompiler.EmitLibrary(source, first);
            _ = ActorScalarSourceCompiler.EmitLibrary(source, second);
            Require(compiled == admitted &&
                File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second)),
                "identical actor source emits byte-identical deterministic CIL");

            // A state-initializer source mutation must change generated
            // bytes and the actual emitted public initializer MethodDef.
            string alternateSource = source.Replace(
                "int value: 0", "int value: 11", StringComparison.Ordinal);
            Require(alternateSource != source,
                "fixture must have a replaceable state initializer");
            string alternateOutput = Path.Combine(root, "alternate", "OresGenerated.dll");
            _ = ActorScalarSourceCompiler.EmitLibrary(alternateSource, alternateOutput);
            Require(!File.ReadAllBytes(first).AsSpan().SequenceEqual(
                    File.ReadAllBytes(alternateOutput)),
                "changing actor initial state changes generated assembly bytes");
            using (var alternateAssembly = AssemblyDefinition.ReadAssembly(alternateOutput))
            {
                var alternateProgram = alternateAssembly.MainModule.Types.Single(t =>
                    t.Namespace == "Oreslang.Generated" && t.Name == "OresProgram");
                var alternateInitial = alternateProgram.Methods.Single(m =>
                    m.Name == "ActorInitial");
                Require(alternateInitial.Body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Ldc_I8 &&
                    i.Operand is long constant && constant == 11),
                    "actor state initializer is truly lowered as CIL literal 11");
            }

            using var assembly = AssemblyDefinition.ReadAssembly(first);
            var program = assembly.MainModule.Types.SingleOrDefault(t =>
                t.Namespace == "Oreslang.Generated" && t.Name == "OresProgram");
            Require(program is not null, "actor CIL has statically linkable OresProgram");
            var methods = program!.Methods.ToDictionary(m => m.Name, StringComparer.Ordinal);
            Require(methods.Count == 3 && methods.ContainsKey("Run") &&
                methods.ContainsKey("ActorInitial") &&
                methods.ContainsKey("ActorStep"),
                "actor CIL exports precisely entry, initializer and method");
            var initial = methods["ActorInitial"];
            var step = methods["ActorStep"];
            Require(initial.IsPublic && initial.IsStatic &&
                initial.Parameters.Count == 0 &&
                initial.ReturnType.FullName == "System.Int64",
                "actor initializer signature is precisely ():int");
            Require(step.IsPublic && step.IsStatic &&
                step.Parameters.Count == 2 &&
                step.Parameters.All(p => p.ParameterType.FullName == "System.Int64") &&
                step.ReturnType.FullName == "System.Int64",
                "generated method is precisely (int,int):int");
            Require(step.HasBody && step.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Add_Ovf),
                "actor method emits checked CIL rather than unchecked addition");
            Require(!step.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj),
                "actor M0 has no dynamic method dispatch or object creation");

            int caseNo = 0;
            foreach (string rejected in new[]
            {
                "define actor Counter as int value:0 int extra:0 fnc increment(int amount):int {self.value=self.value+amount;return self.value;} end",
                "define actor Counter as int value:0 fnc other(int amount):int {self.value=self.value+amount;return self.value;} end",
                "define actor Counter as int value:0 fnc increment(int amount):int {self.value=self.value-amount;return self.value;} end",
                "define actor Counter as int value:0 fnc increment(int amount):int {self.value=self.value+amount;return self.value;} fnc other():int {return 0;} end",
                "define actor Counter as int value:0 suspends fnc increment(int amount):int {self.value=self.value+amount;return self.value;} end",
                "define actor Counter as int value:0 fnc increment(int amount):int {self.value=self.value+amount;return amount;} end",
                "define actor Counter as int value:0 fnc increment(int amount):int {self.value=self.value+amount;return self.value;} end define actor Second as end",
                "define actor Counter as int value:9223372036854775808 fnc increment(int amount):int {self.value=self.value+amount;return self.value;} end",
                "define actor Counter as int value:0 fnc increment(int amount):int {self.value=self.value+amount;return self.value;} end @unknown",
                "define actor Counter as int value:0 fnc increment(int value):int {self.value=self.value+value;return self.value;} end",
                "define actor Counter as int value:0 fnc increment(int amount):int {self.value=self.value+amount;return self.value;} end /* unclosed",
                "/* " + ((char)0) + " */ " + source,
                "/* " + ((char)0xD800) + " */ " + source,
                "define actor Counter as int value:0 fnc increment(int amount):int {self.value=self.value+amount;return self.value;} end @unsafe"
            })
            {
                string rejectedOutput = Path.Combine(root,
                    "rejected" + caseNo++, "OresGenerated.dll");
                ExpectRejected(() =>
                    ActorScalarSourceCompiler.EmitLibrary(rejected, rejectedOutput),
                    "unsupported/forged actor M0 source");
                Require(!File.Exists(rejectedOutput),
                    "rejected source must not produce a CIL artifact");
            }

            ExpectRejected(() => ActorScalarSourceCompiler.EmitLibrary(
                source, Path.Combine(root, "not-generated.dll")),
                "noncanonical actor DLL filename must be rejected");
            Console.WriteLine("PASS generated nominal actor M0 CIL signatures, checked-add, deterministic bytes and 15 rejection cases");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectRejected(Action operation, string explanation)
    {
        try { operation(); }
        catch (FormatException) { return; }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(
            "Actor M0 unsupported source unexpectedly accepted: " + explanation);
    }

    private static void Require(bool valid, string explanation)
    {
        if (!valid)
            throw new InvalidOperationException("Actor M0 CIL regression: " + explanation);
    }
}
