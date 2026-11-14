using System.Globalization;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>Closed six-file Counter main IR, not general async lowering.</summary>
public sealed class VerifiedCounterMainV2
{
    internal VerifiedCounterMainV2(VerifiedExternalCounterV2 actor, long[] messages)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Namespace = actor.Namespace;
        ActorName = actor.ActorName;
        StateField = actor.StateField;
        InitialValue = actor.InitialValue;
        StopValue = actor.StopValue;
        Messages = Array.AsReadOnly((long[])messages.Clone());
    }
    public string Namespace { get; }
    public string ActorName { get; }
    public string StateField { get; }
    public long InitialValue { get; }
    public long StopValue { get; }
    public IReadOnlyList<long> Messages { get; }
}

public static partial class OresParser
{
    public static VerifiedCounterMainV2 InspectCounterMainV2(
        IReadOnlyDictionary<string, string> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        string[] files = { "actor.atto", "contract.atto", "method.atto",
            "receive.atto", "state.atto", "main.atto" };
        if (units.Count != files.Length ||
            files.Any(f => !units.ContainsKey("src/counter/" + f)))
            throw new FormatException("E_COUNTER_MAIN: six linked source units required");
        var receiver = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in files)
            if (file != "main.atto")
                receiver.Add("src/counter/" + file, units["src/counter/" + file]);
        VerifiedExternalCounterV2 actor = InspectExternalCounterV2(receiver);

        var r = new CounterMainReader(units["src/counter/main.atto"]);
        r.Expect("namespace"); r.Expect(actor.Namespace); r.Expect(";");
        r.Expect("pub"); r.Expect("async"); r.Expect("routine");
        r.Expect("main"); r.Expect("("); r.Expect(")"); r.Expect(":");
        r.Expect("void"); r.Expect("{");
        r.Expect("val"); string worker = r.Identifier();
        if (worker is "self" or "stdio" or "await" or "handle_state")
            throw new FormatException("E_COUNTER_MAIN: reserved worker name");
        r.Expect("="); r.Expect("spawn"); r.Expect(actor.ActorName);
        r.Expect("("); r.Expect(")"); r.Expect(";");
        r.Expect("await"); r.Expect(worker); r.Expect(".");
        r.Expect("get_ready"); r.Expect("("); r.Expect(")"); r.Expect(";");

        var values = new long[4];
        for (int i = 0; i < values.Length; i++)
        {
            r.Expect(worker); r.Expect("."); r.Expect("send"); r.Expect("(");
            values[i] = r.Number(); r.Expect(")"); r.Expect(";");
            if (i != values.Length - 1 && values[i] == actor.StopValue)
                throw new FormatException("E_COUNTER_MAIN: early STOP leaves unsent messages");
        }
        if (values[^1] != actor.StopValue)
            throw new FormatException("E_COUNTER_MAIN: terminal send must stop receiver");
        long total = actor.InitialValue;
        foreach (long v in values)
        {
            try { total = checked(total + v); }
            catch (OverflowException ex)
            {
                throw new FormatException("E_COUNTER_MAIN: source arithmetic overflows", ex);
            }
        }

        var results = new string[4];
        var bound = new HashSet<string>(StringComparer.Ordinal) { worker };
        for (int i = 0; i < results.Length; i++)
        {
            r.Expect("val"); string name = r.Identifier();
            if (!bound.Add(name) || name is "self" or "stdio" or "await" or "handle_state")
                throw new FormatException("E_COUNTER_MAIN: duplicate/privileged result name");
            results[i] = name;
            r.Expect("="); r.Expect("("); r.Expect("await");
            r.Expect(worker); r.Expect("."); r.Expect("outputs"); r.Expect(".");
            r.Expect("next"); r.Expect("("); r.Expect(")"); r.Expect(")");
            r.Expect("."); r.Expect("value"); r.Expect("."); r.Expect("unwrap");
            r.Expect("("); r.Expect(")"); r.Expect(";");
        }
        for (int i = 0; i < results.Length; i++)
        {
            r.Expect("stdio"); r.Expect("."); r.Expect("stdout");
            r.Expect("."); r.Expect("write"); r.Expect("(");
            r.Expect(results[i]); r.Expect("."); r.Expect("value");
            r.Expect(")"); r.Expect(";");
            if (i != results.Length - 1)
            {
                r.Expect("stdio"); r.Expect("."); r.Expect("stdout");
                r.Expect("."); r.Expect("write"); r.Expect("(");
                r.Expect("\"|\""); r.Expect(")"); r.Expect(";");
            }
        }
        r.Expect("await"); r.Expect(worker); r.Expect(".");
        r.Expect("get_done"); r.Expect("("); r.Expect(")"); r.Expect(";");
        r.Expect("return"); r.Expect(";"); r.Expect("}");
        r.Done();
        return new VerifiedCounterMainV2(actor, values);
    }

    private readonly record struct MainToken(string Text, int Offset);
    private sealed class CounterMainReader
    {
        private readonly List<MainToken> _tokens = new();
        private int _index;
        internal CounterMainReader(string source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Length > 65_536 || Encoding.UTF8.GetByteCount(source) > 65_536 ||
                source.Contains('\0'))
                throw new FormatException("E_COUNTER_MAIN: NUL or oversized source");
            for (int n = 0; n < source.Length; n++)
            {
                if (char.IsHighSurrogate(source[n]))
                {
                    if (++n >= source.Length || !char.IsLowSurrogate(source[n]))
                        throw new FormatException("E_COUNTER_MAIN: malformed UTF-16");
                }
                else if (char.IsLowSurrogate(source[n]))
                    throw new FormatException("E_COUNTER_MAIN: malformed UTF-16");
            }
            int p = 0;
            while (p < source.Length)
            {
                char c = source[p];
                if (char.IsWhiteSpace(c)) { p++; continue; }
                if (c == '/' && p + 1 < source.Length && source[p + 1] == '/')
                {
                    p += 2;
                    while (p < source.Length && source[p] is not ('\r' or '\n' or '\u0085' or '\u2028' or '\u2029')) p++;
                    continue;
                }
                if (c == '/' && p + 1 < source.Length && source[p + 1] == '*')
                {
                    p += 2; int depth = 1;
                    while (depth != 0)
                    {
                        if (p + 1 >= source.Length)
                            throw new FormatException("E_COUNTER_MAIN: unclosed comment");
                        if (source[p] == '/' && source[p + 1] == '*')
                        {
                            if (++depth > 64) throw new FormatException("E_COUNTER_MAIN: comment depth");
                            p += 2;
                        }
                        else if (source[p] == '*' && source[p + 1] == '/')
                        {
                            depth--; p += 2;
                        }
                        else p++;
                    }
                    continue;
                }
                int start = p;
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
                {
                    p++;
                    while (p < source.Length &&
                        source[p] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') p++;
                }
                else if (c is >= '0' and <= '9')
                {
                    p++;
                    while (p < source.Length && source[p] is >= '0' and <= '9' or '_') p++;
                }
                else if (c == '"')
                {
                    if (p + 3 > source.Length || !source.AsSpan(p, 3).SequenceEqual("\"|\"".AsSpan()))
                        throw new FormatException("E_COUNTER_MAIN: only canonical pipe string supported");
                    p += 3;
                }
                else if (";:.,=(){}-".IndexOf(c) >= 0) p++;
                else throw new FormatException($"E_COUNTER_MAIN: unsupported character at {p}");
                _tokens.Add(new MainToken(source[start..p], start));
                if (_tokens.Count > 2_048) throw new FormatException("E_COUNTER_MAIN: token budget");
            }
            _tokens.Add(new MainToken("", source.Length));
        }

        private MainToken At => _tokens[_index];
        internal void Expect(string word)
        {
            if (At.Text != word)
                throw new FormatException($"E_COUNTER_MAIN: expected '{word}' at offset {At.Offset}");
            _index++;
        }
        internal string Identifier()
        {
            string s = At.Text;
            if (s.Length is < 1 or > 128 ||
                s[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_') ||
                s.Skip(1).Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
                throw new FormatException($"E_COUNTER_MAIN: identifier expected at {At.Offset}");
            _index++;
            return s;
        }
        internal long Number()
        {
            bool negative = At.Text == "-";
            if (negative) _index++;
            string raw = At.Text;
            if (raw.Length == 0 || raw[0] is not (>= '0' and <= '9') ||
                raw[^1] is not (>= '0' and <= '9') ||
                raw.Contains("__", StringComparison.Ordinal) ||
                raw.Any(ch => ch != '_' && (ch < '0' || ch > '9')))
                throw new FormatException($"E_COUNTER_MAIN: signed i64 expected at {At.Offset}");
            _index++;
            if (!long.TryParse((negative ? "-" : "") + raw.Replace("_", "", StringComparison.Ordinal),
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
                throw new FormatException("E_COUNTER_MAIN: signed i64 overflow");
            return v;
        }
        internal void Done()
        {
            if (At.Text.Length != 0)
                throw new FormatException($"E_COUNTER_MAIN: trailing source at {At.Offset}");
        }
    }
}
