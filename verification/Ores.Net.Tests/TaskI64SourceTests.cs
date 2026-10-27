using Mono.Cecil;
using Ores.Net.Compiler;

internal static class TaskI64SourceTests
{
    internal const string Source = "pub fnc main():int {const Future<int> f = Future.from_task(task {return 6 * 7;}); return await f;}";

    internal static void Run()
    {
        var program = TaskI64Source.Check(Source);
        var dir = Path.Combine(Path.GetTempPath(), "ores-task-ir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "OresGenerated.dll");
            CilEmitter.EmitTaskI64Library(program, path);
            byte[] first = File.ReadAllBytes(path);
            string scalarPath = Path.Combine(dir, "scalar.dll");
            CilEmitter.EmitLibrary(program.Producer, scalarPath);
            using (var assembly = AssemblyDefinition.ReadAssembly(path))
            {
                using var scalar = AssemblyDefinition.ReadAssembly(scalarPath);
                if (assembly.MainModule.Mvid == scalar.MainModule.Mvid)
                    throw new Exception("Task producer ABI reused scalar module identity");
                var method = assembly.MainModule.Types.Single(t => t.Name == "OresProgram")
                    .Methods.Single(m => m.Name == "RunTaskI64Producer");
                if (CilReferenceEvaluator.Evaluate(method) != 42 ||
                    !method.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Mul_Ovf))
                    throw new Exception("Task-i64 source did not lower to executable checked producer CIL");
            }
            CilEmitter.EmitTaskI64Library(program, path);
            if (!File.ReadAllBytes(path).SequenceEqual(first))
                throw new Exception("Task-i64 CIL is nondeterministic");
        }
        finally { Directory.Delete(dir, recursive: true); }

        foreach (var trivia in new[] { "/* a /* b */ c */", "// gap\r", "// gap\u2028" })
            _ = TaskI64Source.Check(Source.Replace("task {", "task " + trivia + " {", StringComparison.Ordinal));
        _ = TaskI64Source.Check(Source.Replace("const Future", "let Future", StringComparison.Ordinal));
        foreach (var invalid in new[] {
            Source.Replace("Future<int>", "Future<bool>", StringComparison.Ordinal),
            Source.Replace("6 * 7", "captured", StringComparison.Ordinal),
            Source.Replace("6 * 7", "await other", StringComparison.Ordinal),
            Source.Replace("6 * 7", "task {return 42;}", StringComparison.Ordinal),
            Source.Replace("from_task", "new", StringComparison.Ordinal),
            Source.Replace("return await f", "return await other", StringComparison.Ordinal),
            Source.Replace("fnc main", "fnc worker", StringComparison.Ordinal),
            Source.Replace("6 * 7", "self.value", StringComparison.Ordinal),
            Source.Replace("6 * 7", "rt borrow value", StringComparison.Ordinal),
            Source + " // \ud800", Source + " /* \0 */", Source + " fnc other():int{return 1;}"
        })
        {
            try { _ = TaskI64Source.Check(invalid); }
            catch (FormatException) { continue; }
            throw new Exception("Unsupported Task-i64 source was admitted");
        }
        // Shipping parser must remain closed even for the opt-in positive.
        try { _ = OresParser.Parse(Source); }
        catch (FormatException) { return; }
        throw new Exception("Task-i64 experimental profile leaked into shipping scalar parser");
    }
}
