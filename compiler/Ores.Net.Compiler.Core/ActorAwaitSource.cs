using System.Globalization;

namespace Ores.Net.Compiler;

/// <summary>Closed one-await actor-method IR; not a general actor AST.</summary>
public sealed class VerifiedActorAwait
{
    internal VerifiedActorAwait(string actor, string field, long initial, string method,
        long? producerLeft = null, long? producerRight = null)
    {
        ActorName = actor; FieldName = field; InitialValue = initial; MethodName = method;
        ProducerLeft = producerLeft; ProducerRight = producerRight;
    }
    public long? ProducerLeft { get; }
    public long? ProducerRight { get; }
    public string ActorName { get; }
    public string FieldName { get; }
    public long InitialValue { get; }
    public string MethodName { get; }
}

public static partial class OresParser
{
    // Explicit experimental emitter. Use the existing bounded declared-actor
    // lexer; no regex rewriting, implicit effects or borrow-across-await.
    public static VerifiedActorAwait InspectActorAwait(string source)
    {
        var r = new DeclaredReader(source);
        _ = r.Namespace();
        r.E("declare"); r.E("pub"); r.E("actor");
        string actor = r.Id(); r.E(";");
        r.E("define"); r.E("state"); r.E("on"); r.E("actor"); r.E(actor);
        r.E("as"); r.E("{"); r.E("let"); r.E("int");
        string field = r.Id(); r.E(":"); long initial = r.I64(); r.E(";"); r.E("}");
        r.E("pub"); r.E("suspends"); r.E("mtd"); r.E("("); r.E(actor);
        string receiver = r.Id(); r.E(")");
        string method = r.Id(); r.E("("); r.E("int"); string input = r.Id();
        bool hostFuture = r.Take(",");
        string future;
        if (hostFuture) {
            r.E("Future"); r.E("<"); r.E("int"); r.E(">");
            future = r.Id();
        } else future = "";
        r.E(")"); r.E(":"); r.E("int"); r.E("{");
        r.E("val"); r.E("int"); string saved = r.Id(); r.E("="); r.E(input); r.E(";");
        long? producerLeft = null, producerRight = null;
        if (!hostFuture) {
            // Capture-free, finite checked arithmetic only; no ambient actor
            // state, task nesting, suspension or runtime-generated code.
            r.E("val"); r.E("Future"); r.E("<"); r.E("int"); r.E(">");
            future = r.Id(); r.E("="); r.E("task"); r.E("<"); r.E("int"); r.E(">");
            r.E("{"); r.E("return"); producerLeft = r.I64(); r.E("+");
            producerRight = r.I64(); r.E(";"); r.E("}"); r.E(";");
        }
        r.E("val"); r.E("int"); string result = r.Id(); r.E("="); r.E("await"); r.E(future); r.E(";");
        r.E("return"); r.StateIntrinsic("write"); string loan = r.Loan();
        r.E("{"); r.E(loan); r.E("."); r.E(field); r.E("=");
        r.E(loan); r.E("."); r.E(field); r.E("+"); r.E("(");
        r.E(saved); r.E("+"); r.E(result); r.E(")"); r.E(";");
        r.E("return"); r.E(loan); r.E("."); r.E(field); r.E(";");
        r.E("}"); r.E(")"); r.E(";"); r.E("}"); r.Done();
        // No source identifiers are emitted as unescaped C# locals. Reject
        // shadowing and lifecycle hooks at the source boundary.
        string[] names = [actor, field, receiver, method, input, future, saved, result, loan];
        string[] reserved = ["self", "handle_state", "receive", "init", "send", "end",
            "tear_down", "on_start", "on_stop", "on_restart", "on_panic", "on_throw",
            "on_raise", "on_recover", "await", "suspends", "critical", "int", "Future",
            "val", "let", "return", "pub", "mtd", "actor", "state", "define", "declare", "task"];
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(n => reserved.Contains(n, StringComparer.Ordinal)))
            throw new FormatException("E_ACTOR_AWAIT: shadowed or reserved source identifier");
        return new VerifiedActorAwait(actor, field, initial, method, producerLeft, producerRight);
    }
}

/// <summary>Build-time C# for one typed suspension edge; no dynamic emit.
/// The generated frame is internal to the trusted embedding assembly.
/// </summary>
public static class ActorAwaitSourceEmitter
{
    public static string EmitCSharp(VerifiedActorAwait plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string initial = plan.InitialValue == long.MinValue ? "long.MinValue" :
            plan.InitialValue.ToString(CultureInfo.InvariantCulture) + "L";
        string emitted = $"""
            // Verified one-await actor frame; static NativeAOT source.
            #nullable enable
            using Ores.Net.Runtime;
            using System.Threading.Tasks;
            namespace Oreslang.Generated;
            internal sealed class AwaitActor_{{plan.ActorName}}
            {
                internal const string Layout = "{{plan.ActorName}}|1:I";
                internal static OresVmHost.ActorTicket Create(OresVmHost host,
                    OresVmHost.IsolateHandle isolate, ulong id, bool serial)
                {
                    var actor = host.RegisterActor(isolate, id, serial);
                    try {
                        host.ConfigureNamedActorState(isolate, actor, "{{plan.ActorName}}", Layout,
                            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64, {{initial}}) });
                        return actor;
                    } catch { host.RetireActor(isolate, actor); throw; }
                }
                private readonly long _saved;
                private readonly Task<long> _pending;
                private int _pc;
                internal long? Result { get; private set; }
                internal AwaitActor_{{plan.ActorName}}(long incoming, Task<long> pending)
                {
                    _saved = incoming;
                    _pending = pending ?? throw new System.ArgumentNullException(nameof(pending));
                }
                internal OresVmHost.ActorPumpStep Start_{{plan.MethodName}}(OresVmHost.ActorDispatchScope scope)
                {
                    if (_pc != 0) throw new System.InvalidOperationException("stale start");
                    _pc = 1;
                    return new OresVmHost.ActorPumpStep(_pending, Resume);
                }
                private OresVmHost.ActorPumpStep? Resume(OresVmHost.ActorDispatchScope scope)
                {
                    if (_pc != 1 || !_pending.IsCompletedSuccessfully)
                        throw new System.InvalidOperationException("stale or non-ready resume");
                    _pc = 2;
                    long delta = checked(_saved + _pending.GetAwaiter().GetResult());
                    Result = scope.AddInt64(0, delta);
                    return null;
                }
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        if (plan.ProducerLeft is not long left || plan.ProducerRight is not long right)
            return emitted;
        static string Literal(long value) => value == long.MinValue ? "long.MinValue" :
            value.ToString(CultureInfo.InvariantCulture) + "L";
        // Keep the external-Future ABI unchanged. Source-owned producer mode
        // uses the same verified frame and a separate bounded carrier item.
        return emitted
            .Replace("private readonly Task<long> _pending;",
                "private OresVmHost.ActorScalarTask? _producer;\n    private Task<long>? _pending;", StringComparison.Ordinal)
            .Replace("(long incoming, Task<long> pending)", "(long incoming)", StringComparison.Ordinal)
            .Replace("        _pending = pending ?? throw new System.ArgumentNullException(nameof(pending));\n",
                "", StringComparison.Ordinal)
            .Replace("        _pc = 1;",
                "        _producer = new OresVmHost.ActorScalarTask(static () => Produce(" +
                    Literal(left) + ", " + Literal(right) + "));\n" +
                "        _pending = _producer.Future;\n        _pc = 1;", StringComparison.Ordinal)
            .Replace("new OresVmHost.ActorPumpStep(_pending, Resume);",
                "new OresVmHost.ActorPumpStep(_pending, Resume, _producer);", StringComparison.Ordinal)
            .Replace("if (_pc != 1 || !_pending.IsCompletedSuccessfully)",
                "if (_pc != 1 || _pending is null || !_pending.IsCompletedSuccessfully)", StringComparison.Ordinal)
            .Replace("    private OresVmHost.ActorPumpStep? Resume(",
                "    private static long Produce(long left, long right) => checked(left + right);\n" +
                "    private OresVmHost.ActorPumpStep? Resume(", StringComparison.Ordinal);
    }
}
