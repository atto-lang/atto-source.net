using System.Text.Json;
using Ores.Net.Compiler;
using Ores.Net.Runtime;
using Oreslang.Generated;

internal static class CounterMainV2Tests
{
    private static void Require(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException("compiled Counter main: " + why);
    }

    private static void Reject(IReadOnlyDictionary<string, string> units,
        string find, string replacement, string label)
    {
        const string main = "src/counter/main.atto";
        var modified = new Dictionary<string, string>(units, StringComparer.Ordinal);
        if (!modified[main].Contains(find, StringComparison.Ordinal))
            throw new Exception("Invalid negative fixture: " + label);
        modified[main] = modified[main].Replace(find, replacement, StringComparison.Ordinal);
        try { _ = OresParser.InspectCounterMainV2(modified); }
        catch (FormatException) { return; }
        throw new Exception("Counter main source accepted unsupported variant: " + label);
    }

    public static void Run()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "actor-counter-corpus.json")));
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        var source = manifest.RootElement.GetProperty("sourceUnits");
        foreach (string filename in new[] { "actor.atto", "contract.atto",
                     "method.atto", "receive.atto", "state.atto", "main.atto" })
        {
            string name = "src/counter/" + filename;
            units.Add(name, source.GetProperty(name).GetString() ??
                throw new InvalidOperationException("Missing source unit " + name));
        }
        VerifiedCounterMainV2 verified = OresParser.InspectCounterMainV2(units);
        Require(verified.Messages.SequenceEqual(new long[] { 1, 2, 3, 4 }),
            "canonical source sends not preserved as typed operands");

        string compiled = CounterMainV2Emitter.EmitCSharp(verified);
        string golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "GeneratedCounterMainV2.g.cs"));
        static string Lf(string x) => x.Replace("\r\n", "\n", StringComparison.Ordinal);
        Require(Lf(compiled) == Lf(golden), "static generated async main differs from compiler");
        Require(compiled.Contains("outputs.Reader.ReadAsync(timeout.Token)", StringComparison.Ordinal) &&
                compiled.Contains("Task done = runtime.ObserveTermination(worker);", StringComparison.Ordinal) &&
                compiled.Contains("runtime.Send(worker, mail);", StringComparison.Ordinal) &&
                !compiled.Contains("Thread.Sleep(", StringComparison.Ordinal) &&
                !compiled.Contains("SpinWait.", StringComparison.Ordinal),
            "guest source was replaced by host polling or precomputed output");

        var changed = new Dictionary<string, string>(units, StringComparer.Ordinal);
        changed["src/counter/main.atto"] = changed["src/counter/main.atto"]
            .Replace("worker.send(2);", "worker.send(5);", StringComparison.Ordinal);
        var modified = OresParser.InspectCounterMainV2(changed);
        Require(modified.Messages.SequenceEqual(new long[] { 1, 5, 3, 4 }) &&
                CounterMainV2Emitter.EmitCSharp(modified).Contains(
                    "BinaryPrimitives.WriteInt64LittleEndian(mail, 5L);", StringComparison.Ordinal),
            "native main messages were host hardcoded, not compiled from source");

        var commented = new Dictionary<string, string>(units, StringComparer.Ordinal);
        commented["src/counter/main.atto"] = "/* nested /* comment */ closed */\n" +
            commented["src/counter/main.atto"];
        Require(OresParser.InspectCounterMainV2(commented).Messages.SequenceEqual(
                new long[] { 1, 2, 3, 4 }), "non-executable comment affected parser");

        Reject(units, "worker.send(2)", "worker.send(worker)", "nonliteral mailbox data");
        Reject(units, "worker.send(2)", "worker.send(4)", "early STOP before remaining sends");
        Reject(units, "worker.send(4)", "worker.send(9)", "no terminal STOP");
        Reject(units, "worker.send(2)", "worker.send(9223372036854775808)", "i64 overflow");
        Reject(units, "worker.send(2)", "worker.send(9223372036854775807)", "checked state overflow");
        Reject(units, "await worker.get_ready();", "worker.get_ready();", "discarded readiness await");
        Reject(units, "await worker.get_done();", "worker.get_done();", "discarded completion await");
        Reject(units, "await worker.get_done();", "await worker.get_ready();", "wrong completion primitive");
        Reject(units, "val a = (await worker.outputs.next()).value.unwrap();",
            "val a = worker.outputs.next();", "non-awaiting output read");
        Reject(units, "val b = (await worker.outputs.next()).value.unwrap();",
            "val a = (await worker.outputs.next()).value.unwrap();", "duplicate output binding");
        Reject(units, "stdio.stdout.write(b.value);",
            "stdio.stdout.write(a.value);", "stdout source order changed");
        Reject(units, "stdio.stdout.write(\"|\");",
            "stdio.stdout.write(\",\");", "wrong stdout separator");
        Reject(units, "spawn Counter()", "spawn OtherActor()", "wrong nominal actor");
        Reject(units, "await worker.outputs.next()", "await worker.other.next()", "wrong output capability");
        Reject(units, "return;", "return; worker.send(9);", "unverified trailing statements");
        Reject(units, "worker.send(2);", "worker.send(2); await worker.get_done();",
            "premature termination await");
        Reject(units, "namespace counter;", "namespace other;", "foreign namespace");
        Reject(units, "worker.send(2);", "worker.send(2); /* unclosed", "invalid comment");
        Reject(units, "worker.send(2);", "worker.send(2); \u0000", "NUL");

        var missing = new Dictionary<string, string>(units, StringComparer.Ordinal);
        missing.Remove("src/counter/main.atto");
        bool denied = false;
        try { _ = OresParser.InspectCounterMainV2(missing); }
        catch (FormatException) { denied = true; }
        Require(denied, "missing main source was accepted");

        // Read-only actor completion authority must require the exact nonce,
        // and a captured termination task survives STOP without polling.
        using (var own = new ActorRuntime())
        using (var other = new ActorRuntime())
        {
            ActorRef actor = own.Spawn(ActorKind.Private, (_, _) => { });
            ActorRef foreign = other.Spawn(ActorKind.Private, (_, _) => { });
            bool foreignDenied = false;
            try { _ = own.ObserveTermination(foreign); }
            catch (InvalidOperationException) { foreignDenied = true; }
            Require(foreignDenied, "foreign runtime forged completion observation");
            bool defaultDenied = false;
            try { _ = own.ObserveTermination(default); }
            catch (InvalidOperationException) { defaultDenied = true; }
            Require(defaultDenied, "default actor ref forged completion observation");
            Task done = own.ObserveTermination(actor);
            own.Stop(actor);
            Require(done.Wait(TimeSpan.FromSeconds(5)) && done.IsCompletedSuccessfully,
                "captured termination task failed to complete on STOP");
            bool staleDenied = false;
            try { _ = own.ObserveTermination(actor); }
            catch (InvalidOperationException) { staleDenied = true; }
            Require(staleDenied, "retired actor minted a new termination handle");
        }

        string output = CounterMain_V2.RunAsync().GetAwaiter().GetResult();
        Require(output == manifest.RootElement.GetProperty("expected")
                .GetProperty("stdout").GetString(),
            "actual compiled main did not return source computed 1|3|6|10");
        Console.WriteLine("DECLARED_COUNTER_MAIN_V2_MANAGED_PASS async_root=true source_send=true owned_mailbox=true terminal_future=true");
    }
}
