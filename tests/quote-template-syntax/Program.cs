using Ores.Net.Compiler;

static void Good(string source)
{
    int end = QuotedSourceSyntax.Scan(source, 0);
    if (end != source.Length)
        throw new Exception($"Incomplete lexical span {end}/{source.Length}: {source}");
}
static void Bad(string source)
{
    try
    {
        QuotedSourceSyntax.Scan(source, 0);
    }
    catch (FormatException)
    {
        return;
    }
    throw new Exception("Unexpected success: " + source);
}

string tick = new((char)0x60, 1);
string open = "$" + "{";
foreach (var source in new[]
{
    "'single'", "\"double\"", "'opposite \"quotes\"'", "\"opposite 'quotes'\"",
    "'escaped \\' apostrophe'", "\"escaped \\\" quote\"",
    "'continuation\\\nline'", "'hex \\x41'", "'unicode \\u0041'",
    "\"raw\nnewline\"", "'\\q'", "'\\x0G'", "'\\uD83D\\uDE00'",
    tick + "raw\nmultiline" + tick,
    tick + "escaped \\$" + "{still_text}" + tick,
    tick + "x " + open + "fnc({key: '}', str: \"{\"}, /* { */ " +
        tick + "nested " + open + "value}" + tick + ")} y" + tick,
})
    Good(source);
foreach (var source in new[]
{
    "'unclosed", "'\\u12xz'", "'\\uD800'", "'\\uDFFF'", "'\\u{1f600}'",
    tick + "invalid \\q" + tick, tick + "invalid \\x41" + tick,
    tick + "invalid \\u{1f600}" + tick,
    tick + "invalid \\" + "\n" + tick,
    tick + "bad " + open + " }" + tick,
    tick + "bad " + open + "call(1)" + tick,
    tick + "bad " + open + "/* unclosed}" + tick,
    tick + "bad " + open + "'unterminated}" + tick
})
    Bad(source);
Bad(tick + string.Concat(Enumerable.Repeat(open + tick, 70)) + "x" +
    string.Concat(Enumerable.Repeat(tick + "}", 70)) + tick);
string CountTemplate(int n) => tick + string.Concat(Enumerable.Repeat("${'x'}", n)) + tick;
Good(CountTemplate(1024));
Bad(CountTemplate(1025));
string BracesTemplate(int n) => tick + open + new string('{', n) + "x" + new string('}', n) + "}" + tick;
Good(BracesTemplate(255));
Bad(BracesTemplate(256));
string NestedTemplate(int n) => tick + string.Concat(Enumerable.Repeat(open + tick, n)) + "x" +
    string.Concat(Enumerable.Repeat(tick + "}", n)) + tick;
Good(NestedTemplate(16));
Bad(NestedTemplate(17));
// Raw UTF-16 source text must never smuggle isolated surrogate code units.
foreach (string delimiter in new[] { "'", "\"", tick })
{
    Good(delimiter + char.ConvertFromUtf32(0x1f600) + delimiter);
    foreach (char isolated in new[] { (char)0xd800, (char)0xdbff, (char)0xdc00, (char)0xdfff })
        Bad(delimiter + isolated + delimiter);
}
string NestedComments(int count) => tick + open +
    string.Concat(Enumerable.Repeat("/*", count)) + "ignored" +
    string.Concat(Enumerable.Repeat("*/", count)) + "x}" + tick;
Good(NestedComments(128));
Bad(NestedComments(129));
// A literal backslash must not conceal an unpaired raw UTF-16 scalar.
foreach (string delimiter in new[] { "'", "\"" })
{
    Good(delimiter + "\\" + char.ConvertFromUtf32(0x1f600) + delimiter);
    foreach (char isolated in new[] { (char)0xd800, (char)0xdbff, (char)0xdc00, (char)0xdfff })
        Bad(delimiter + "\\" + isolated + delimiter);
}
Console.WriteLine("quoted/template source syntax smoke: PASS");
