using System.Globalization;

namespace Ores.Net.Compiler;

/// <summary>Closed one-await actor-method IR; not a general actor AST.</summary>
public sealed class VerifiedActorAwait
{
    internal VerifiedActorAwait(string actor, string field, long initial, string method,
        long? producerLeft = null, long? producerRight = null,
        long? secondLeft = null, long? secondRight = null, bool trapResult = false)
    {
        ActorName = actor; FieldName = field; InitialValue = initial; MethodName = method;
        ProducerLeft = producerLeft; ProducerRight = producerRight;
        SecondLeft = secondLeft; SecondRight = secondRight; TrapResult = trapResult;
    }
    public long? ProducerLeft { get; }
    public long? ProducerRight { get; }
    public long? SecondLeft { get; }
    public long? SecondRight { get; }
    public bool TrapResult { get; }
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
        string method = r.Id(); r.E("(");
        if (!r.Take("int"))
            return InspectTrappedActorAwaitTail(r, actor, field, initial, receiver, method);
        string input = r.Id();
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
        long? secondLeft = null, secondRight = null;
        string? secondFuture = null, secondResult = null;
        if (!hostFuture && r.Take("val")) {
            r.E("Future"); r.E("<"); r.E("int"); r.E(">");
            secondFuture = r.Id(); r.E("="); r.E("task"); r.E("<"); r.E("int"); r.E(">");
            r.E("{"); r.E("return"); secondLeft = r.I64(); r.E("+");
            secondRight = r.I64(); r.E(";"); r.E("}"); r.E(";");
            r.E("val"); r.E("int"); secondResult = r.Id(); r.E("=");
            r.E("await"); r.E(secondFuture); r.E(";");
        }
        r.E("return"); r.StateIntrinsic("write"); string loan = r.Loan();
        r.E("{"); r.E(loan); r.E("."); r.E(field); r.E("=");
        r.E(loan); r.E("."); r.E(field); r.E("+"); r.E("(");
        r.E(saved); r.E("+"); r.E(result);
        if (secondResult is not null) { r.E("+"); r.E(secondResult); }
        r.E(")"); r.E(";");
        r.E("return"); r.E(loan); r.E("."); r.E(field); r.E(";");
        r.E("}"); r.E(")"); r.E(";"); r.E("}"); r.Done();
        // No source identifiers are emitted as unescaped C# locals. Reject
        // shadowing and lifecycle hooks at the source boundary.
        string[] names = [actor, field, receiver, method, input, future, saved, result, loan];
        if (secondFuture is not null && secondResult is not null)
            names = [.. names, secondFuture, secondResult];
        string[] reserved = ["self", "handle_state", "receive", "init", "send", "end",
            "tear_down", "on_start", "on_stop", "on_restart", "on_panic", "on_throw",
            "on_raise", "on_recover", "await", "suspends", "critical", "int", "Future",
            "val", "let", "return", "pub", "mtd", "actor", "state", "define", "declare", "task"];
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(n => reserved.Contains(n, StringComparer.Ordinal)))
            throw new FormatException("E_ACTOR_AWAIT: shadowed or reserved source identifier");
        return new VerifiedActorAwait(actor, field, initial, method, producerLeft, producerRight, secondLeft, secondRight);
    }

    // Separate closed expression-trap profile: a single Future<int> input or
    // source-owned checked scalar producer. No implicit Result flattening,
    // arbitrary error union or state mutation under the trap.
    private static VerifiedActorAwait InspectTrappedActorAwaitTail(DeclaredReader r,
        string actor, string field, long initial, string receiver, string method)
    {
        bool external = r.Take("Future");
        string future = "";
        if (external) {
            r.E("<"); r.E("int"); r.E(">"); future = r.Id();
        }
        r.E(")"); r.E(":"); r.E("Result"); r.E("<"); r.E("int");
        r.E(","); r.E("Error"); r.E(">"); r.E("{");
        long? left = null, right = null;
        if (!external) {
            r.E("val"); r.E("Future"); r.E("<"); r.E("int"); r.E(">");
            future = r.Id(); r.E("="); r.E("task"); r.E("<"); r.E("int"); r.E(">");
            r.E("{"); r.E("return"); left = r.I64(); r.E("+"); right = r.I64();
            r.E(";"); r.E("}"); r.E(";");
        }
        r.E("return"); r.TrapKeyword(); r.E("("); r.E("await"); r.E(future);
        r.E(")"); r.E(";"); r.E("}"); r.Done();
        string[] names = [actor, field, receiver, method, future];
        string[] reserved = ["self", "handle_state", "receive", "init", "send", "end",
            "tear_down", "on_start", "on_stop", "on_restart", "on_panic", "on_throw",
            "on_raise", "on_recover", "await", "suspends", "critical", "int", "Future",
            "val", "let", "return", "pub", "mtd", "actor", "state", "define", "declare",
            "task", "trap", "Result", "Error"];
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(n => reserved.Contains(n, StringComparer.Ordinal)))
            throw new FormatException("E_ACTOR_AWAIT: shadowed or reserved trap binding");
        return new VerifiedActorAwait(actor, field, initial, method, left, right, trapResult: true);
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
        if (plan.TrapResult) return EmitTrapped(plan);
        string initial = plan.InitialValue == long.MinValue ? "long.MinValue" :
            plan.InitialValue.ToString(CultureInfo.InvariantCulture) + "L";
        string emitted = $$"""
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
        string owned = emitted
            .Replace("private readonly Task<long> _pending;",
                "private Task<long>? _pending;", StringComparison.Ordinal)
            .Replace("(long incoming, Task<long> pending)", "(long incoming)", StringComparison.Ordinal)
            .Replace("        _pending = pending ?? throw new System.ArgumentNullException(nameof(pending));\n",
                "", StringComparison.Ordinal)
            .Replace("        _pc = 1;",
                "        var producer = new OresVmHost.ActorScalarTask(static () => Produce(" +
                    Literal(left) + ", " + Literal(right) + "));\n" +
                "        _pending = producer.Future;\n        _pc = 1;", StringComparison.Ordinal)
            .Replace("new OresVmHost.ActorPumpStep(_pending, Resume);",
                "new OresVmHost.ActorPumpStep(producer.Future, Resume, producer);", StringComparison.Ordinal)
            .Replace("if (_pc != 1 || !_pending.IsCompletedSuccessfully)",
                "if (_pc != 1 || _pending is null || !_pending.IsCompletedSuccessfully)", StringComparison.Ordinal)
            .Replace("    private OresVmHost.ActorPumpStep? Resume(",
                "    private static long Produce(long left, long right) => checked(left + right);\n" +
                "    private OresVmHost.ActorPumpStep? Resume(", StringComparison.Ordinal);
        if (plan.SecondLeft is not long secondLeft || plan.SecondRight is not long secondRight)
            return owned;

        // The first awaited value is an owned scalar. No scope or loan may
        // cross the second suspension. Evaluate arithmetic only after both
        // awaits, preserving source evaluation order and checked overflow.
        return owned
            .Replace("    private int _pc;", "    private long _first;\n    private int _pc;", StringComparison.Ordinal)
            .Replace("new OresVmHost.ActorPumpStep(producer.Future, Resume, producer);",
                "new OresVmHost.ActorPumpStep(producer.Future, Resume, producer, ObserveFaultInSegment: true);",
                StringComparison.Ordinal)
            .Replace("if (_pc != 1 || _pending is null || !_pending.IsCompletedSuccessfully)",
                "if (_pc != 1 || _pending is null || !_pending.IsCompleted)", StringComparison.Ordinal)
            .Replace("        _pc = 2;\n        long delta = checked(_saved + _pending.GetAwaiter().GetResult());",
                "        _first = _pending.GetAwaiter().GetResult();\n" +
                "        var producer = new OresVmHost.ActorScalarTask(static () => Produce(" +
                    Literal(secondLeft) + ", " + Literal(secondRight) + "));\n" +
                "        _pending = producer.Future;\n        _pc = 2;\n" +
                "        return new OresVmHost.ActorPumpStep(producer.Future, ResumeSecond, producer, ObserveFaultInSegment: true);\n" +
                "    }\n" +
                "    private OresVmHost.ActorPumpStep? ResumeSecond(OresVmHost.ActorDispatchScope scope)\n" +
                "    {\n" +
                "        if (_pc != 2 || _pending is null || !_pending.IsCompleted)\n" +
                "            throw new System.InvalidOperationException(\"stale or non-ready second resume\");\n" +
                "        long second = _pending.GetAwaiter().GetResult();\n" +
                "        _pc = 3;\n" +
                "        long delta = checked(checked(_saved + _first) + second);", StringComparison.Ordinal);
    }

    private static string EmitTrapped(VerifiedActorAwait plan)
    {
        static string Literal(long value) => value == long.MinValue ? "long.MinValue" :
            value.ToString(CultureInfo.InvariantCulture) + "L";
        bool owned = plan.ProducerLeft.HasValue && plan.ProducerRight.HasValue;
        string constructor = owned
            ? "internal AwaitActor_" + plan.ActorName + "() { }"
            : "internal AwaitActor_" + plan.ActorName +
                "(Task<long> pending) { _pending = pending ?? throw new System.ArgumentNullException(nameof(pending)); }";
        string start = owned
            ? "var producer = new OresVmHost.ActorScalarTask(static () => Produce(" +
                Literal(plan.ProducerLeft!.Value) + ", " + Literal(plan.ProducerRight!.Value) + "));\n" +
                "        _pending = producer.Future;\n        _pc = 1;\n" +
                "        return new OresVmHost.ActorPumpStep(producer.Future, Resume, producer, ObserveFaultInSegment: true);"
            : "_pc = 1;\n        return new OresVmHost.ActorPumpStep(_pending!, Resume, ObserveFaultInSegment: true);";
        return $$"""
            // Verified trapped actor await; exact shared GuestResults ABI.
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
                            new[] { new OresVmHost.ActorStateField(OresVmHost.ActorStateScalarKind.Int64, {{Literal(plan.InitialValue)}}) });
                        return actor;
                    } catch { host.RetireActor(isolate, actor); throw; }
                }
                private Task<long>? _pending;
                private int _pc;
                internal GuestResultI64? Result { get; private set; }
                {{constructor}}
                internal OresVmHost.ActorPumpStep Start_{{plan.MethodName}}(OresVmHost.ActorDispatchScope scope)
                {
                    if (_pc != 0) throw new System.InvalidOperationException("stale start");
                    {{start}}
                }
                private static long Produce(long left, long right) => checked(left + right);
                private OresVmHost.ActorPumpStep? Resume(OresVmHost.ActorDispatchScope scope)
                {
                    if (_pc != 1 || _pending is null || !_pending.IsCompleted)
                        throw new System.InvalidOperationException("stale or non-ready resume");
                    _pc = 2;
                    Result = GuestAwaitResults.TrapCompletedI64(_pending);
                    return null;
                }
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
