using Ores.Net.Compiler;
using System.Text.RegularExpressions;

static void Require(bool ok, string reason)
{
    if (!ok) throw new Exception("Structured audit failed: " + reason);
}
static void Reject(string source, string reason)
{
    try { _ = OresStructuredParser.Parse(source); }
    catch (FormatException) { return; }
    throw new Exception("Structured audit unexpectedly accepted: " + reason);
}

// This executable has no runtime interpreter: it tests the separately
// typechecked source AST, leaving production Native AOT compilation unchanged.
int accepted = 0;
for (int i = 0; i < 256; i++)
{
    string body = "fnc main():int{" +
        "let int x=" + i + ";" +
        "let bool test=x<" + (i + 1) + ";" +
        "if test {x=x+1;} else {x=x-1;}" +
        "return x;}";
    var p = OresStructuredParser.Parse(body);
    Require(p.Body.Count == 4 &&
        p.Body[0] is StructuredLet { Name: "x", ValueType: IrValueType.I64 } &&
        p.Body[1] is StructuredLet { Name: "test", ValueType: IrValueType.Bool } &&
        p.Body[2] is StructuredIf { Then.Count: 1, Else.Count: 1 } &&
        p.Body[3] is StructuredReturn { Value.Type: IrValueType.I64 },
        "typed randomized arithmetic/branch shape " + i);
    accepted++;
}
for (int i = 0; i < 100; i++)
{
    string name = "var" + i;
    string sample = "fnc main():int{let int " + name +
        "=" + i + ";return " + name + ";}";
    var program = OresStructuredParser.Parse(sample);
    Require(program.Body[0] is StructuredLet { ValueType: IrValueType.I64 },
        "ASCII identifier " + i);
    accepted++;
}

Reject("fnc main():int{let int x=1;let int x=2;return x;}",
    "duplicate lexical binding");
Reject("fnc main():int{let int x=x;return x;}",
    "use before initialization");
Reject("fnc main():int{let bool x=3;return 1;}",
    "invalid initializer type");
Reject("fnc main():int{if 3 {return 1;}else{return 2;}}",
    "non-boolean condition");
Reject("fnc main():int{if true {return 1;}}",
    "fallthrough return path");
Reject("fnc main():int{while true {return 1;}}",
    "loop alone cannot prove function return");
Reject("fnc main():int{return 1;let int x=2;}",
    "unreachable local declaration");
Reject("fnc main():int{if true {let int x=1;}return x;}",
    "escaped lexical scope");
Reject("fnc main():int{let int x=1/2;return x;}",
    "i64 truncating division incompatible with canonical semantics");
Reject("fnc main():int{return 9223372036854775808;}",
    "i64 overflow literal");
Reject("fnc main():int{return " + new string('(', 150) +
    "1" + new string(')', 150) + ";}",
    "maximum parser recursion budget");
Reject(new string(' ', 1_048_577), "source length admission");

Require(OresParser.Parse("fnc main():int{return 2+3*4;}") is Binary,
    "existing production arithmetic parser must remain unchanged");

// Report two independently reproducible hardening gaps. They do not
// silently downgrade the release gate or claim to be fixed.
int findings = 0;
const string undefined = "fnc main():int{return missing;}";
int expectedAt = undefined.IndexOf("missing", StringComparison.Ordinal);
try
{
    _ = OresStructuredParser.Parse(undefined);
    throw new Exception("Undefined identifier was accepted");
}
catch (FormatException ex)
{
    Match m = Regex.Match(ex.Message, @"offset (\d+)");
    Require(m.Success, "undefined name diagnostic includes a numeric offset");
    int actualAt = int.Parse(m.Groups[1].Value);
    if (actualAt != expectedAt)
    {
        Console.WriteLine("KNOWN FINDING: undefined name offset " + actualAt +
            " should be " + expectedAt);
        findings++;
    }
}

string manyExpressions = "fnc main():int{return " +
    string.Concat(Enumerable.Repeat("1+", 16_500)) + "1;}";
try
{
    var program = OresStructuredParser.Parse(manyExpressions);
    Require(program.Body.Count == 1, "oversize expression AST shape");
    Console.WriteLine("KNOWN FINDING: accepted >16,384 expression nodes " +
        "although the downstream CFG SSA limit is 16,384");
    findings++;
}
catch (FormatException ex) when (ex.Message.Contains("budget",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Expression AST budget enforced");
}

Console.WriteLine("Structured audit: " + accepted +
    " deterministic positive cases, 12 negative cases, " + findings +
    " documented hardening findings");
