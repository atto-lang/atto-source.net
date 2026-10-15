using System.Text.Json;
using Ores.Net.Compiler;
using Ores.Net.Runtime;

internal static class MandatoryAsyncIoTests
{
    public static void Run()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "mandatory-async-io.json"));
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant() != "e3b5caa3267683e7a1ec83f04d671333455746ceb87188dba6ec7ec1e75ec042")
            throw new Exception("mandatory I/O fixture drift");
        var corpus = JsonDocument.Parse(raw);
        foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var source = item.GetProperty("source").GetString()!;
            var accept = item.GetProperty("expect").GetString() == "accept";
            foreach (var parser in new Action<string>[] {
                s => { OresIrLowering.Lower(OresParser.Parse(s)); },
                s => { OresStructuredParser.Parse(s); }
            })
            {
                bool rejected = false;
                try { parser(source); }
                catch (FormatException) { rejected = true; }
                if (rejected == accept) throw new Exception("I/O admission mismatch: " + item.GetProperty("id"));
            }
        }
        var stdout = Console.Out;
        var stderr = Console.Error;
        try
        {
            Console.SetOut(new NoIoWriter());
            Console.SetError(new NoIoWriter());
            int calls = 0;
            var value = RuntimeHost.Evaluate(() => { calls++; return 42; });
            if (calls != 1 || value != new ExecutionResult(0, 42, ExecutionError.None)) throw new Exception("result ABI");
            if (RuntimeHost.Evaluate(() => throw new OverflowException()) != new ExecutionResult(70, null, ExecutionError.ArithmeticOverflow)) throw new Exception("overflow ABI");
            if (RuntimeHost.Evaluate(() => throw new DivideByZeroException()) != new ExecutionResult(70, null, ExecutionError.DivisionByZero)) throw new Exception("division ABI");
            if (RuntimeHost.Execute(() => 9) != 0 || RuntimeHost.Execute(() => throw new OverflowException()) != 70) throw new Exception("status ABI");
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }
    private sealed class NoIoWriter : System.IO.TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => throw new Exception("guest runtime attempted synchronous console I/O");
        public override void Write(string? value) => throw new Exception("guest runtime attempted synchronous console I/O");
        public override void WriteLine(string? value) => throw new Exception("guest runtime attempted synchronous console I/O");
    }
}
