using System.Text.Json;
using Ores.Net.Compiler;

internal static class DeclaredActorCounterV2Tests
{
    private static void Require(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException("declared Counter v2: " + why);
    }

    private static void Reject(IReadOnlyDictionary<string, string> original,
        string path, string before, string after, string reason)
    {
        var changed = new Dictionary<string, string>(original, StringComparer.Ordinal);
        if (!changed[path].Contains(before, StringComparison.Ordinal))
            throw new Exception("invalid mutation control: " + reason);
        changed[path] = changed[path].Replace(before, after, StringComparison.Ordinal);
        try
        {
            _ = OresParser.InspectExternalCounterV2(changed);
        }
        catch (FormatException) { return; }
        throw new Exception("declared Counter v2 admitted: " + reason);
    }

    public static void Run()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "actor-counter-corpus.json")));
        var source = corpus.RootElement.GetProperty("sourceUnits");
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in new[] { "actor.atto", "contract.atto",
                     "method.atto", "receive.atto", "state.atto" })
        {
            string key = "src/counter/" + file;
            units.Add(key, source.GetProperty(key).GetString() ??
                throw new Exception("null canonical source unit " + key));
        }

        var plan = OresParser.InspectExternalCounterV2(units);
        Require(plan.Namespace == "counter" && plan.ActorName == "Counter" &&
                plan.StateField == "total" && plan.InitialValue == 0L &&
                plan.StopValue == 4L,
            "canonical five-file source was not bound to typed receive plan");
        string compiled = DeclaredActorCounterV2Emitter.EmitCSharp(plan);
        string golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "GeneratedExternalCounterActor.g.cs"));
        static string Lf(string x) => x.Replace("\r\n", "\n", StringComparison.Ordinal);
        Require(Lf(compiled) == Lf(golden),
            "source-verified external receive differs from static native golden");
        Require(compiled.Contains("state.AddInt64(context, 0, incoming)", StringComparison.Ordinal) &&
                compiled.Contains("runtime.Send(replyTo, reply)", StringComparison.Ordinal) &&
                compiled.Contains("if (incoming == 4L) runtime.Stop(context.Self)", StringComparison.Ordinal) &&
                !compiled.Contains("Call_receive(", StringComparison.Ordinal),
            "native source must use checked mailbox ingress, scalar loan and actor STOP");

        var omittedSelf = new Dictionary<string, string>(units, StringComparer.Ordinal);
        const string r = "src/counter/receive.atto";
        omittedSelf[r] = omittedSelf[r].Replace("self.handle_state(",
            "handle_state(", StringComparison.Ordinal);
        Require(DeclaredActorCounterV2Emitter.EmitCSharp(
                OresParser.InspectExternalCounterV2(omittedSelf)) == compiled,
            "omitted-self compiler intrinsic changed native receive authority");
        var differentStop = new Dictionary<string, string>(units, StringComparer.Ordinal);
        differentStop[r] = differentStop[r].Replace("if mail.value == 4",
            "if mail.value == 9", StringComparison.Ordinal);
        var checkedStop = OresParser.InspectExternalCounterV2(differentStop);
        Require(checkedStop.StopValue == 9 &&
                DeclaredActorCounterV2Emitter.EmitCSharp(checkedStop).Contains(
                    "if (incoming == 9L) runtime.Stop(context.Self)", StringComparison.Ordinal),
            "stop condition must come from source rather than a hardcoded 4");

        const string a = "src/counter/actor.atto";
        const string c = "src/counter/contract.atto";
        const string m = "src/counter/method.atto";
        const string s = "src/counter/state.atto";
        Reject(units, r, "external mtd", "pub mtd", "ordinary method cannot become receive ingress");
        Reject(units, r, "ActorMail<int>", "ActorMail<bool>", "wrong wire type");
        Reject(units, r, "ActorMail<int> mail", "ActorMail<int> self", "self input shadow");
        Reject(units, r, "critical-write", "critical-read", "read loan mutates state");
        Reject(units, r, "critical-write", "critical -write", "spaced critical mode");
        Reject(units, r, "critical-read", "critical -read", "spaced read loan");
        Reject(units, r, "critical-write", "critical--write", "double critical modifier");
        Reject(units, r, "s.total = s.total + mail.value",
            "s.total = s.total + 1", "host-forged delta instead of source mail");
        Reject(units, r, "self.send(s.total)", "self.send(mail.value)", "wrong read result");
        Reject(units, r, "self.end();", "self.end(1);", "forged lifecycle argument");
        Reject(units, r, "if mail.value == 4", "if mail.value = 4", "assignment instead of comparison");
        Reject(units, r, "if mail.value == 4", "if mail.value == 9223372036854775808", "stop i64 overflow");
        Reject(units, r, "return;", "return s;", "escaped borrowed state");
        Reject(units, r, "receive(ActorMail<int> mail): void",
            "receive(ActorMail<int> mail): int", "incorrect entrypoint return ABI");
        Reject(units, r, "namespace counter;", "namespace other;", "foreign receiver namespace");
        Reject(units, a, "implements CounterContract", "implements OtherContract", "nominal contract mismatch");
        Reject(units, c, "echo(int n): int", "echo(bool n): int", "contract method mismatch");
        Reject(units, m, "return n;", "return c;", "out-of-line method body not typechecked");
        Reject(units, s, "let int total : 0;", "let int total : true;", "wrong state type");
        Reject(units, s, "let int total : 0;", "let int total : 0; let int leak : 9;",
            "extra unverified actor state field");
        Reject(units, r, "s.total = s.total + mail.value",
            "s.total = s.total + mail.value; await something()", "suspension inside loan");
        Reject(units, r, "self.handle_state(critical-read", "other.handle_state(critical-read",
            "forged state intrinsic receiver");
        Reject(units, r, "self.handle_state(critical-read |s|",
            "self.handle_state(critical-read |self|", "state view authority shadow");
        Reject(units, r, "self.handle_state(critical-read |s|",
            "self.handle_state(critical-read |mail|", "message value authority shadow");
        Reject(units, r, "self.handle_state(critical-read |s|",
            "self.handle_state(critical-read |handle_state|", "intrinsic authority shadow");
        Reject(units, r, "return;", "return; print(42);", "unverified suffix code");
        Reject(units, r, "return;", "return; /* unterminated", "unterminated nested comment");

        var incomplete = new Dictionary<string, string>(units, StringComparer.Ordinal);
        incomplete.Remove(m);
        bool missingRejected = false;
        try { _ = OresParser.InspectExternalCounterV2(incomplete); }
        catch (FormatException) { missingRejected = true; }
        Require(missingRejected, "source-unit omission passed package binder");

        // These are authored compiler tests; they execute only when the .NET
        // verification binary actually runs. The suite never marks CI success.
        Console.WriteLine("DECLARED_EXTERNAL_COUNTER_BINDER_PASS typed_mail=true sealed_loans=true private_receive=true");
    }
}
