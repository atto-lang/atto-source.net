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
    tick + "raw\nmultiline" + tick,
    tick + "escaped \\$" + "{still_text}" + tick,
    tick + "x " + open + "fnc({key: '}', str: \"{\"}, /* { */ " +
        tick + "nested " + open + "value}" + tick + ")} y" + tick,
})
    Good(source);
foreach (var source in new[]
{
    "'unclosed", "\"raw\nnewline\"", "'\\q'", "'\\u12xz'", "'\\x0G'",
    tick + "bad " + open + " }" + tick,
    tick + "bad " + open + "call(1)" + tick,
    tick + "bad " + open + "/* unclosed}" + tick,
    tick + "bad " + open + "'unterminated}" + tick
})
    Bad(source);
Bad(tick + string.Concat(Enumerable.Repeat(open + tick, 70)) + "x" +
    string.Concat(Enumerable.Repeat(tick + "}", 70)) + tick);
Bad(tick + string.Concat(Enumerable.Repeat(open + "x}", 129)) + tick);
Console.WriteLine("quoted/template source syntax smoke: PASS");
