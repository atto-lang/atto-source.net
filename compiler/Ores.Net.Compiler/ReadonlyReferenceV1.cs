using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ores.Net.Compiler;

// Closed source-to-C# profile. No general ownership/GC or stack-placement
// claim; the generated program forwards one managed object reference.
internal static class ReadonlyReferenceV1
{
    private static readonly HashSet<string> Reserved = new(
        "define class as end pub let int fnc main return new mut const val self actor unsafe suspends await import void bool float string true false null if else while for match defer recover borrow take copy move ref deref unref task spawn send critical static private constructor namespace module using with set Option Result Array Map List RawPointer Storage".Split(' '), StringComparer.Ordinal);
    private static FormatException Bad(string reason) => new("E_NATIVE_READONLY: " + reason);
    private sealed record Field(string Name, long Value);
    private sealed record Helper(string Name, bool Forward, int Index);
    private sealed class Parser
    {
        private readonly List<string> tokens = new();
        private int at;
        internal readonly List<Field> Fields = new();
        internal readonly List<Helper> Helpers = new();
        internal readonly List<int> Calls = new();
        internal readonly List<string> Ops = new();
        internal Parser(string source)
        {
            if (source.Length > 65536 || source.Split('\n', 2)[0].TrimEnd('\r') != "// atto-ownership: borrow-default-v1")
                throw Bad("marker or source budget");
            foreach (char c in source)
                if (c > 126 || (c < 32 && !"\t\r\n\f".Contains(c))) throw Bad("source character");
            var scanner = new Regex(@"\G(?:[\t\r\n\f ]+|//[^\r\n]*|[A-Za-z][A-Za-z0-9_]*|[0-9]+|[{}():;,.=+\-])", RegexOptions.CultureInvariant);
            for (int i = 0; i < source.Length;)
            {
                var m = scanner.Match(source, i);
                if (!m.Success || m.Index != i) throw Bad("token");
                i += m.Length;
                if (!string.IsNullOrWhiteSpace(m.Value) && !m.Value.StartsWith("//", StringComparison.Ordinal)) tokens.Add(m.Value);
                if (tokens.Count > 1024) throw Bad("token budget");
            }
        }
        private string Peek() => at < tokens.Count ? tokens[at] : "";
        private string Pop() => at < tokens.Count ? tokens[at++] : throw Bad("unexpected end");
        private void Need(string s) { if (Pop() != s) throw Bad("expected " + s); }
        private string Name()
        {
            var s = Pop();
            if (!Regex.IsMatch(s, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z") || Reserved.Contains(s)) throw Bad("identifier");
            return s;
        }
        private long Number()
        {
            var s = Pop();
            if (!Regex.IsMatch(s, @"\A[0-9]{1,19}\z") || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) throw Bad("literal");
            return n;
        }
        private int Call(string owner)
        {
            var n = Name(); int i = Helpers.FindIndex(h => h.Name == n);
            if (i < 0) throw Bad("call graph");
            Need("("); Need(owner); Need(")"); return i;
        }
        internal void Parse()
        {
            Need("define"); Need("class"); var cls = Name(); Need("as");
            while (Peek() == "pub")
            {
                if (Fields.Count >= 8) throw Bad("field budget");
                Need("pub"); Need("let"); Need("int"); var id = Name();
                if (Fields.Any(f => f.Name == id)) throw Bad("duplicate field");
                Need("="); long value = Number(); Need(";"); Fields.Add(new(id, value));
            }
            if (Fields.Count == 0) throw Bad("fields"); Need("end");
            while (Peek() == "fnc")
            {
                if (Helpers.Count >= 8) throw Bad("helper budget");
                Need("fnc"); var id = Name();
                if (id == cls || Helpers.Any(h => h.Name == id)) throw Bad("duplicate helper");
                Need("("); Need(cls); var p = Name(); Need(")"); Need(":"); Need("int"); Need("{"); Need("return");
                if (Peek() == p)
                {
                    Need(p); Need("."); var name = Name(); int i = Fields.FindIndex(f => f.Name == name);
                    if (i < 0) throw Bad("unknown field");
                    Helpers.Add(new(id, false, i));
                }
                else Helpers.Add(new(id, true, Call(p)));
                Need(";"); Need("}");
            }
            if (Helpers.Count == 0) throw Bad("helpers");
            Need("pub"); Need("fnc"); Need("main"); Need("("); Need(")"); Need(":"); Need("int"); Need("{");
            Need("let"); var owner = Name(); Need("="); Need("new"); Need(cls); Need("("); Need(")"); Need(";");
            Need("return"); Calls.Add(Call(owner));
            while (Peek() is "+" or "-")
            {
                if (Calls.Count >= 8) throw Bad("call budget");
                Ops.Add(Pop()); Calls.Add(Call(owner));
            }
            Need(";"); Need("}"); if (at != tokens.Count) throw Bad("trailing source");
        }
    }
    // The admitted closed helper graph contains only immutable scalar
    // field reads and forwards to earlier helpers. Resolve once at compile time.
    private static int[] ResolveFields(Parser p)
    {
        var fields = new int[p.Helpers.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            var h = p.Helpers[i];
            if (h.Forward)
            {
                if (h.Index < 0 || h.Index >= i) throw Bad("unproved forwarding edge");
                fields[i] = fields[h.Index];
            }
            else
            {
                if (h.Index < 0 || h.Index >= p.Fields.Count) throw Bad("unproved field");
                fields[i] = h.Index;
            }
        }
        return fields;
    }

    internal static string Emit(string source)
    {
        var p = new Parser(source); p.Parse();
        var fields = ResolveFields(p);
        var b = new StringBuilder("public static class AttoReadonlyGuest {\nprivate sealed class Obj {\n");
        for (int i = 0; i < p.Fields.Count; i++) b.Append("public readonly long f").Append(i).Append('=').Append(p.Fields[i].Value.ToString(CultureInfo.InvariantCulture)).Append("L;\n");
        b.Append("}\n");
        b.Append("public static long Run(){Obj p=new Obj();long r=p.f").Append(fields[p.Calls[0]]).Append(";\n");
        for (int i = 1; i < p.Calls.Count; i++) b.Append("r=checked(r").Append(p.Ops[i-1]).Append("p.f").Append(fields[p.Calls[i]]).Append(");\n");
        return b.Append("return r;}\npublic static void Main(){System.Console.WriteLine(Run());}\n}\n").ToString();
    }
}
