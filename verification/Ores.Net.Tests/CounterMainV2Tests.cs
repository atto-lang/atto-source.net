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
        Require(verified.ActorName == "Counter" &&
                verified.Messages.SequenceEqual(new long[] { 1, 2, 3, 4 }),
            "canonical actor identity/send operands not preserved as typed IR");

        var stateMir = ActorStateMirV3.Lower(verified);
        Require(stateMir.ActorName == verified.ActorName &&
                stateMir.StateField == "total" && stateMir.InitialValue == 0 &&
                stateMir.StopValue == 4 &&
                stateMir.Messages.SequenceEqual(verified.Messages),
            "typed main MIR does not retain linked actor state and source mail");
        Require(stateMir.ReadLoanReleasedBeforeMailboxSend() &&
                stateMir.Instructions.IndexOf(ActorStateMirV3.Op.ReleaseRead) <
                stateMir.Instructions.IndexOf(ActorStateMirV3.Op.MailboxSendCopyI64) &&
                stateMir.Instructions.IndexOf(ActorStateMirV3.Op.CheckedAddI64) <
                stateMir.Instructions.IndexOf(ActorStateMirV3.Op.ReleaseWrite) &&
                stateMir.Loans.Count == 4 &&
                stateMir.Loans[0].Mode == ActorStateMirV3.LoanMode.Write &&
                stateMir.Loans[1].Action == ActorStateMirV3.LoanAction.Release &&
                stateMir.Loans[2].Mode == ActorStateMirV3.LoanMode.Read &&
                stateMir.Loans[3].Action == ActorStateMirV3.LoanAction.Release &&
                stateMir.Loans.All(l => l.GenerationFenced && l.CurrentTurn),
            "typed main MIR omitted checked arithmetic or balanced state loans");
        bool immutableMir = false;
        try { ((IList<ActorStateMirV3.Op>)stateMir.Instructions)[0] =
            ActorStateMirV3.Op.MailboxSendCopyI64; }
        catch (NotSupportedException) { immutableMir = true; }
        Require(immutableMir, "state MIR instructions are mutable by caller");

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

        // Nominal actor identity must be a verified source operand, not a
        // fixed host class that silently ignores the linked actor declaration.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in units)
            renamed.Add(pair.Key, pair.Value.Replace("Counter", "Renamed", StringComparison.Ordinal));
        var renamedPlan = OresParser.InspectCounterMainV2(renamed);
        string renamedGenerated = CounterMainV2Emitter.EmitCSharp(renamedPlan);
        Require(renamedPlan.ActorName == "Renamed" &&
                renamedGenerated.Contains(
                    "worker = DeclaredActor_Renamed.SpawnRuntime(runtime, observer);",
                    StringComparison.Ordinal) &&
                !renamedGenerated.Contains("DeclaredActor_Counter.", StringComparison.Ordinal),
            "nominal actor identity silently fell back to hardcoded Counter");

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

        // The six-file root must not independently authorize an older
        // receiver that publishes while a critical-read loan is live.
        foreach (var mutation in new[] {
            ("self.send(snapshot)", "self.send(s.total)"),
            ("val int snapshot", "let int snapshot"),
            ("-> s.total", "-> s"),
            ("val int snapshot", "val object snapshot"),
            ("self.send(snapshot);", "await self.send(snapshot);")
        })
        {
            var badReceiver = new Dictionary<string, string>(units, StringComparer.Ordinal);
            const string receiveFile = "src/counter/receive.atto";
            Require(badReceiver[receiveFile].Contains(mutation.Item1, StringComparison.Ordinal),
                "malformed receiver negative source control");
            badReceiver[receiveFile] = badReceiver[receiveFile].Replace(
                mutation.Item1, mutation.Item2, StringComparison.Ordinal);
            bool rejected = false;
            try { _ = OresParser.InspectCounterMainV2(badReceiver); }
            catch (FormatException) { rejected = true; }
            Require(rejected, "six-file linker admitted borrowed state into mailbox boundary");
        }

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
