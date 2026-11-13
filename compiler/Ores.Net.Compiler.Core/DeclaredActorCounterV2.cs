using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Compiler-owned, closed v2 receive plan. Construction is restricted to the
/// lexical/type/ownership verifier below; this is not a general actor AST.
/// </summary>
public sealed class VerifiedExternalCounterV2
{
    internal VerifiedExternalCounterV2(string ns, string actor, string field,
        long initial, long stopValue)
    {
        Namespace = ns;
        ActorName = actor;
        StateField = field;
        InitialValue = initial;
        StopValue = stopValue;
    }

    public string Namespace { get; }
    public string ActorName { get; }
    public string StateField { get; }
    public long InitialValue { get; }
    public long StopValue { get; }
}

/// <summary>
/// A deliberately closed executable slice of the canonical declared-actor
/// source grammar. Each of five real .atto units is lexed independently.
/// The receiver is external/private; it is never an ordinary instance method.
/// The async main package unit is NOT compiled by this entry point.
/// </summary>
public static partial class OresParser
{
    private const string CounterPath = "src/counter/";

    public static VerifiedExternalCounterV2 InspectExternalCounterV2(
        IReadOnlyDictionary<string, string> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        string[] names = ["actor.atto", "contract.atto", "method.atto",
            "receive.atto", "state.atto"];
        if (units.Count != names.Length ||
            names.Any(n => !units.ContainsKey(CounterPath + n)))
            throw new FormatException("E_EXTERNAL_RECEIVE: exactly five declared-actor source units required");

        DeclaredReader Unit(string name)
        {
            var r = new DeclaredReader(units[CounterPath + name]);
            return r;
        }

        var actor = Unit("actor.atto");
        string ns = actor.Namespace();
        actor.E("declare"); actor.E("pub"); actor.E("actor");
        string actorName = actor.Id();
        actor.E("implements");
        string contractName = actor.Id();
        actor.E(";"); actor.Done();

        var contract = Unit("contract.atto");
        contract.Namespace(ns);
        contract.E("define"); contract.E("pub"); contract.E("contract");
        contract.E(contractName); contract.E("for"); contract.E("actor");
        contract.E("as"); contract.E("mtd");
        string methodName = contract.Id();
        contract.E("("); contract.E("int"); string contractArg = contract.Id();
        contract.E(")"); contract.E(":"); contract.E("int"); contract.E(";");
        contract.E("end"); contract.Done();

        var method = Unit("method.atto");
        method.Namespace(ns);
        method.E("pub"); method.E("mtd"); method.E("(");
        method.E(actorName); string methodReceiver = method.Id();
        method.E(")"); method.E(methodName);
        method.E("("); method.E("int"); string methodArg = method.Id();
        method.E(")"); method.E(":"); method.E("int");
        method.E("{"); method.E("return"); method.E(methodArg);
        method.E(";"); method.E("}"); method.Done();

        var state = Unit("state.atto");
        state.Namespace(ns);
        state.E("define"); state.E("state"); state.E("on");
        state.E("actor"); state.E(actorName); state.E("as"); state.E("{");
        state.E("let"); state.E("int"); string field = state.Id();
        state.E(":"); long initial = state.I64();
        state.E(";"); state.E("}"); state.Done();

        var receive = Unit("receive.atto");
        receive.Namespace(ns);
        receive.E("external"); receive.E("mtd"); receive.E("(");
        receive.E(actorName); string receiverName = receive.Id();
        receive.E(")"); receive.E("receive"); receive.E("(");
        receive.E("ActorMail"); receive.E("<"); receive.E("int"); receive.E(">");
        string mail = receive.Id();
        receive.E(")"); receive.E(":"); receive.E("void"); receive.E("{");

        receive.StateIntrinsic("write");
        string writer = receive.Loan();
        receive.E("{");
        receive.E(writer); receive.E("."); receive.E(field); receive.E("=");
        receive.E(writer); receive.E("."); receive.E(field); receive.E("+");
        receive.E(mail); receive.E("."); receive.E("value"); receive.E(";");
        receive.E("}"); receive.E(")"); receive.E(";");

        receive.StateIntrinsic("read");
        string reader = receive.Loan();
        receive.E("{");
        receive.E("self"); receive.E("."); receive.E("send"); receive.E("(");
        receive.E(reader); receive.E("."); receive.E(field); receive.E(")");
        receive.E(";"); receive.E("}"); receive.E(")"); receive.E(";");

        receive.E("if"); receive.E(mail); receive.E("."); receive.E("value");
        receive.E("==");
        long stopValue = receive.I64();
        receive.E(";"); receive.E("do");
        receive.E("self"); receive.E("."); receive.E("end");
        receive.E("("); receive.E(")"); receive.E(";"); receive.E("fi");
        receive.E("return"); receive.E(";"); receive.E("}"); receive.Done();

        if (new[] { "self", "receive", "handle_state", "send", "end" }.Contains(actorName) ||
            new[] { "self", "receive", "handle_state", "send", "end" }.Contains(field) ||
            methodName is "receive" or "handle_state" or "send" or "end" ||
            mail is "self" or "handle_state" or "receive" or "end" ||
            writer is "self" or "mail" or "handle_state" ||
            reader is "self" or "mail" or "handle_state" ||
            writer == mail || reader == mail ||
            methodReceiver is "self" or "handle_state" ||
            receiverName is "self" or "handle_state" ||
            receiverName == mail || methodReceiver == methodArg ||
            contractArg is "self" or "handle_state" ||
            methodArg is "self" or "handle_state")
            throw new FormatException("E_EXTERNAL_RECEIVE: lexical actor/self/loan authority shadowed");
        return new VerifiedExternalCounterV2(ns, actorName, field, initial, stopValue);
    }

    private readonly record struct DeclaredToken(string Text, int Offset);

    private sealed class DeclaredReader
    {
        private readonly List<DeclaredToken> _tokens;
        private int _index;

        internal DeclaredReader(string source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Length > 65_536 || Encoding.UTF8.GetByteCount(source) > 65_536 ||
                source.IndexOf('\0') >= 0)
                throw new FormatException("E_EXTERNAL_RECEIVE: source unit exceeds 64 KiB or contains NUL");
            for (int i = 0; i < source.Length; i++)
            {
                if (char.IsHighSurrogate(source[i]))
                {
                    if (++i >= source.Length || !char.IsLowSurrogate(source[i]))
                        throw new FormatException("E_EXTERNAL_RECEIVE: invalid UTF-16");
                }
                else if (char.IsLowSurrogate(source[i]))
                    throw new FormatException("E_EXTERNAL_RECEIVE: invalid UTF-16");
            }

            _tokens = new List<DeclaredToken>();
            int pos = 0, commentDepth = 0;
            while (pos < source.Length)
            {
                char ch = source[pos];
                if (char.IsWhiteSpace(ch)) { pos++; continue; }
                if (ch == '/' && pos + 1 < source.Length && source[pos + 1] == '/')
                {
                    pos += 2;
                    while (pos < source.Length && source[pos] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) pos++;
                    continue;
                }
                if (ch == '/' && pos + 1 < source.Length && source[pos + 1] == '*')
                {
                    pos += 2; commentDepth = 1;
                    while (commentDepth > 0)
                    {
                        if (pos + 1 >= source.Length)
                            throw new FormatException("E_EXTERNAL_RECEIVE: unterminated block comment");
                        if (source[pos] == '/' && source[pos + 1] == '*')
                        {
                            if (++commentDepth > 64)
                                throw new FormatException("E_EXTERNAL_RECEIVE: comment nesting limit");
                            pos += 2;
                        }
                        else if (source[pos] == '*' && source[pos + 1] == '/')
                        {
                            commentDepth--; pos += 2;
                        }
                        else pos++;
                    }
                    continue;
                }

                int start = pos;
                if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
                {
                    pos++;
                    while (pos < source.Length && source[pos] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') pos++;
                }
                else if (ch is >= '0' and <= '9')
                {
                    pos++;
                    while (pos < source.Length && source[pos] is >= '0' and <= '9' or '_') pos++;
                }
                else if (pos + 1 < source.Length && (source.AsSpan(pos, 2).SequenceEqual("->") ||
                         source.AsSpan(pos, 2).SequenceEqual("==")))
                    pos += 2;
                else if (";:.,=+{}()|<>-".IndexOf(ch) >= 0)
                    pos++;
                else throw new FormatException($"E_EXTERNAL_RECEIVE: unsupported token at offset {pos}");
                _tokens.Add(new DeclaredToken(source[start..pos], start));
                if (_tokens.Count > 4_096)
                    throw new FormatException("E_EXTERNAL_RECEIVE: token budget exceeded");
            }
            _tokens.Add(new DeclaredToken("", source.Length));
        }

        private DeclaredToken At => _tokens[_index];

        internal bool Take(string token)
        {
            if (At.Text != token) return false;
            _index++;
            return true;
        }

        internal void E(string token)
        {
            if (!Take(token))
                throw new FormatException($"E_EXTERNAL_RECEIVE: expected '{token}' at offset {At.Offset}");
        }

        internal string Id()
        {
            string x = At.Text;
            if (x.Length is < 1 or > 128 ||
                x[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
                x.Skip(1).Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
                throw new FormatException($"E_EXTERNAL_RECEIVE: identifier expected at {At.Offset}");
            _index++;
            return x;
        }

        internal string Namespace()
        {
            E("namespace"); string ns = Id(); E(";");
            return ns;
        }
        internal void Namespace(string expected)
        {
            if (Namespace() != expected)
                throw new FormatException("E_EXTERNAL_RECEIVE: source namespace mismatch");
        }

        internal void Done()
        {
            if (At.Text.Length != 0)
                throw new FormatException($"E_EXTERNAL_RECEIVE: unexpected executable source at {At.Offset}");
        }

        internal long I64()
        {
            bool negative = Take("-");
            string raw = At.Text;
            if (raw.Length == 0 || raw[0] is not (>= '0' and <= '9') ||
                raw[^1] is not (>= '0' and <= '9') ||
                raw.Contains("__", StringComparison.Ordinal) ||
                raw.Any(c => c != '_' && (c < '0' || c > '9')))
                throw new FormatException($"E_EXTERNAL_RECEIVE: expected signed i64 literal at {At.Offset}");
            _index++;
            string text = (negative ? "-" : "") + raw.Replace("_", "", StringComparison.Ordinal);
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out long n))
                throw new FormatException("E_EXTERNAL_RECEIVE: i64 literal out of range");
            return n;
        }

        internal string Loan()
        {
            E("|"); string name = Id(); E("|"); E("->");
            if (name is "self" or "mail" or "handle_state")
                throw new FormatException("E_EXTERNAL_RECEIVE: reserved state loan name");
            return name;
        }

        internal void StateIntrinsic(string mode)
        {
            bool qualified = Take("self");
            if (qualified) E(".");
            E("handle_state"); E("(");
            int offset = At.Offset;
            E("critical");
            if (At.Offset != offset + 8) throw new FormatException("E_EXTERNAL_RECEIVE: critical mode must be adjacent");
            E("-");
            if (At.Offset != offset + 9) throw new FormatException("E_EXTERNAL_RECEIVE: critical mode must be adjacent");
            E(mode);
        }
    }
}

/// <summary>
/// Static NativeAOT source for the verified RECEIVE subset. The read result
/// is scalarized inside the same non-suspending turn. No CLR reflection/JIT,
/// guest main lowering, generic external method dispatch or actor CIL claim.
/// </summary>
public static class DeclaredActorCounterV2Emitter
{
    public static string EmitCSharp(VerifiedExternalCounterV2 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var b = new StringBuilder();
        b.AppendLine("// Verified external mtd receive: bounded source subset, no dynamic emit.");
        b.AppendLine("using Ores.Net.Runtime;");
        b.AppendLine("namespace Oreslang.Generated;");
        b.Append("public static class DeclaredActor_").Append(plan.ActorName).AppendLine();
        b.AppendLine("{");
        b.Append("    public const string ActorName = \"").Append(plan.ActorName).AppendLine("\";");
        b.AppendLine("    public static ActorRef SpawnRuntime(ActorRuntime runtime, ActorRef replyTo)");
        b.AppendLine("    {");
        b.Append("        var state = new ActorNativeScalarState(new long[] { ");
        b.Append(plan.InitialValue == long.MinValue ? "long.MinValue" :
            plan.InitialValue.ToString(CultureInfo.InvariantCulture) + "L");
        b.AppendLine(" }, new byte[] { 1 });");
        b.AppendLine("        return runtime.Spawn(ActorKind.Private, (context, message) =>");
        b.AppendLine("        {");
        b.AppendLine("            if (message.Length != 8) throw new System.InvalidOperationException(\"Expected 8-byte ActorMail<int> payload\");");
        b.AppendLine("            long incoming = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(message.Span);");
        b.AppendLine("            long result = state.AddInt64(context, 0, incoming);");
        b.AppendLine("            System.Span<byte> reply = stackalloc byte[16];");
        b.AppendLine("            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(reply, context.Self.Id);");
        b.AppendLine("            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(reply[8..], result);");
        b.AppendLine("            runtime.Send(replyTo, reply);");
        b.Append("            if (incoming == ").Append(plan.StopValue == long.MinValue ?
            "long.MinValue" : plan.StopValue.ToString(CultureInfo.InvariantCulture) + "L");
        b.AppendLine(") runtime.Stop(context.Self);");
        b.AppendLine("        });");
        b.AppendLine("    }");
        b.AppendLine("}");
        return b.ToString();
    }
}
