using Ores.Net.Compiler;

internal static class StructuredSourceTests
{
    private static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("Structured source regression: " + reason);
    }

    private static void Reject(string source, string reason)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException ex)
        {
            Require(ex.Message.Contains("offset", StringComparison.Ordinal) ||
                    ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase),
                reason + ": missing useful diagnostic");
            return;
        }
        throw new Exception("Structured source incorrectly accepted: " + reason);
    }

    public static void Run()
    {
        // Canonical Java foo() == foo(self) uses one caller-visible slot.
        // The .NET bootstrap has neither nominal class AST nor CIL dispatch:
        // validate a *specific* fail-closed diagnostic for the exact sources
        // instead of calling parser acceptance "implementation parity".
        foreach (var source in new[] {
            "define class X1 as foo():void {} end",
            "define class X2 as foo(self):void {} end",
            "define class X as foo():void {} foo(self):void {} end",
            "define class X as foo(int x):void {} foo(self,int x):void {} end",
            "define class X as foo(self,int x):int {return x;} end",
            "define class X as foo(mut self):void {} end",
            "define actor X as on_start(self):void {} end"
        })
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("receiver-erased method slots", StringComparison.Ordinal)
                        && ex.Message.Contains("Native AOT CIL lowering", StringComparison.Ordinal)
                        && ex.Message.Contains("offset", StringComparison.Ordinal),
                    "class/actor must be rejected by a specific parity gate: " + ex.Message);
                continue;
            }
            throw new Exception(".NET structured frontend silently admitted unlowered method source: " + source);
        }

        const string sum = """
            define pub fnc main(): int {
                let int total = 0;
                let int i = 0;
                while i < 10 {
                    total = total + i;
                    i = i + 1;
                }
                return total;
            }
            """;
        var parsed = OresStructuredParser.Parse(sum);
        Require(parsed.Name == "main" && parsed.Body.Count == 4,
            "structured main statement count");
        Require(parsed.Body[0] is StructuredLet { Name: "total",
            ValueType: IrValueType.I64, Initializer: StructuredInt { Value: 0 } },
            "typed initialized integer");
        Require(parsed.Body[2] is StructuredWhile {
            Condition: StructuredBinary {
                Operator: StructuredOperator.LessThanI64,
                Type: IrValueType.Bool
            }, Body.Count: 2
        }, "typed signed comparison and loop body");
        Require(parsed.Body[3] is StructuredReturn {
            Value: StructuredRead { Name: "total", Type: IrValueType.I64 }
        }, "typed return");
        Require(!parsed.Body.IsReadOnlyButMutable(), "AST must be immutable snapshot");

        var diamond = OresStructuredParser.Parse("""
            fnc main(): int {
                let int x = 0;
                let bool condition = 2 == 2;
                if condition {
                    x = 11;
                } else {
                    x = 22;
                }
                return x;
            }
            """);
        Require(diamond.Body[2] is StructuredIf {
            Condition: StructuredRead { Type: IrValueType.Bool },
            Then.Count: 1, Else.Count: 1
        }, "if/else typed joining assignments");

        var earlyReturn = OresStructuredParser.Parse("""
            pub define fnc main(): int {
                if true { return -9223372036854775808; }
                else { return 7; }
            }
            """);
        Require(earlyReturn.Body.Count == 1 &&
            earlyReturn.Body[0] is StructuredIf {
                Then.Count: 1, Else.Count: 1
            }, "both-branch early return");
        Require(OresStructuredParser.Parse("fnc main():int{return 2+3*4;}").Body[0]
            is StructuredReturn { Value: StructuredBinary {
                Operator: StructuredOperator.Add
            } }, "arithmetic AST operator precedence");

        var arithmetic76 = OresStructuredParser.Parse("""
            pub fnc main(): int {
              return 100 - (3 + 2) - 19;
            }
            """);
        Require(arithmetic76.Body[0] is StructuredReturn {
            Value: StructuredBinary {
                Operator: StructuredOperator.Subtract,
                Left: StructuredBinary {
                    Operator: StructuredOperator.Subtract,
                    Left: StructuredInt { Value: 100 },
                    Right: StructuredBinary {
                        Operator: StructuredOperator.Add,
                        Left: StructuredInt { Value: 3 },
                        Right: StructuredInt { Value: 2 }
                    }
                },
                Right: StructuredInt { Value: 19 }
            }
        }, "shared arithmetic-76 typed AST");

        static void RejectTupleV2(string source, string reason)
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("Tuple-v2", StringComparison.Ordinal) &&
                        ex.Message.Contains("offset", StringComparison.Ordinal),
                    reason + ": tuple-v2 rejection was incidental: " + ex.Message);
                return;
            }
            throw new Exception("Tuple-v2 source incorrectly accepted: " + reason);
        }
        RejectTupleV2("fnc main(tuple (int,int) value):int{return 1;}",
            "explicit tuple parameter");
        RejectTupleV2("fnc main(structural (int,int) value):int{return 1;}",
            "structural tuple parameter");
        RejectTupleV2("fnc main((int,int) value):int{return 1;}",
            "shorthand tuple parameter");
        RejectTupleV2("fnc main():tuple (int,int){return 1;}",
            "tuple return");
        RejectTupleV2("fnc main():int{return call(...pair);}",
            "explicit tuple spread");

        foreach (var operation in new[] { "ref", "unref", "deref", "deref mut", "raw" })
        {
            try { _ = OresStructuredParser.Parse(
                $"fnc main():int{{return rt {operation} value;}}"); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("requires provenance-checked pointer lowering",
                    StringComparison.Ordinal),
                    "structured pointer rejection must name provenance lowering");
                continue;
            }
            throw new Exception("Structured source incorrectly accepted pointer operation: rt " +
                operation);
        }

        foreach (var operation in new[] { "copy", "share", "take", "borrow", "mut borrow" })
        {
            try { _ = OresStructuredParser.Parse(
                $"fnc main():int{{return rt {operation} value;}}"); }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("requires typed actor-arena lowering",
                    StringComparison.Ordinal),
                    "structured ownership rejection must name actor-arena lowering");
                continue;
            }
            throw new Exception("Structured source incorrectly accepted ownership operation: rt " +
                operation);
        }
        try
        {
            _ = OresStructuredParser.Parse(
                "fnc main():int{return rt borrow mut value;}");
            throw new Exception("Structured source admitted legacy rt borrow mut");
        }
        catch (FormatException ex)
        {
            Require(ex.Message.Contains("use 'rt mut borrow'", StringComparison.Ordinal),
                "legacy mutable borrow diagnostic must name canonical spelling");
        }

        // AST height must be bounded even when the source is a flat chain.
        // Grammar recursion depth alone cannot enforce this invariant.
        var maxHeightExpression = string.Join("+", Enumerable.Repeat("1", 128));
        Require(OresStructuredParser.Parse("fnc main():int{return " +
            maxHeightExpression + ";}").Body[0] is StructuredReturn,
            "128-leaf left-associative sum stays inside AST height budget");
        Reject("fnc main():int{return " +
            string.Join("+", Enumerable.Repeat("1", 129)) + ";}",
            "129-leaf sum exceeds AST height limit");
        Reject("fnc main():int{return " +
            string.Join("*", Enumerable.Repeat("1", 129)) + ";}",
            "129-leaf product exceeds AST height limit");
        Reject("fnc main():int{return -(" +
            string.Join("+", Enumerable.Repeat("1", 128)) + ");}",
            "negation cannot silently exceed AST height limit");

        static void ExpectAt(string source, string message, string identifier)
        {
            try { _ = OresStructuredParser.Parse(source); }
            catch (FormatException ex)
            {
                var expected = source.IndexOf(identifier, StringComparison.Ordinal);
                Require(ex.Message.Contains($"offset {expected}", StringComparison.Ordinal),
                    message + ": expected offset " + expected + ", got " + ex.Message);
                return;
            }
            throw new Exception("Structured source incorrectly accepted: " + message);
        }
        ExpectAt("fnc main():int{return missing;}",
            "undefined read points to identifier", "missing");
        ExpectAt("fnc main():int{missing=2;return 1;}",
            "undefined assignment points to identifier", "missing");
        ExpectAt("fnc main():int{let int x=1;let int x=2;return x;}",
            "duplicate declaration points to second identifier", "x=2");

        // The AST stores names, not unique binding IDs; enforce the
        // no-shadowing contract until SSA lowering has explicit symbol IDs.
        ExpectAt("fnc main():int{let int x=1;if true {let int x=2;}return x;}",
            "nested shadowing", "x=2");
        ExpectAt("fnc main():int{let bool ok=true;while ok {let int ok=1;}return 0;}",
            "cross-type shadowing", "ok=1");
        Require(OresStructuredParser.Parse("fnc main():int{let int x=1;if true {x=2;}return x;}")
            .Body.Count == 3, "assignment to enclosing binding remains legal");
        Require(OresStructuredParser.Parse(
            "fnc main():int{if true {let int x=1;}else{let int x=2;}return 0;}")
            .Body.Count == 2, "same spelling in sibling lexical scopes is legal");

        // Seeded adversarial mutations of a valid typed source program.
        // All outcomes must be either a typed AST or a FormatException;
        // indexing failures, recursion faults and other exceptions fail CI.
        const string baseProgram = "fnc main():int{let int i=0;while i<4 {i=i+1;}return i;}";
        const string replacements = "abcXYZ01(){};:+-*<=>@/ ";
        var fuzzRandom = new Random(17303);
        int rejectedMutations = 0;
        for (int mutation = 0; mutation < 512; mutation++)
        {
            var changed = baseProgram.ToCharArray();
            int position = fuzzRandom.Next(changed.Length);
            changed[position] = replacements[fuzzRandom.Next(replacements.Length)];
            string candidate = new(changed);
            try { _ = OresStructuredParser.Parse(candidate); }
            catch (FormatException) { rejectedMutations++; }
        }
        Require(rejectedMutations >= 128,
            "adversarial mutation corpus must exercise substantial parser rejection");

        // The shipping arithmetic frontend is intentionally unchanged.
        // Stage-one binding names must remain unambiguous.
        Require(OresParser.Parse("fnc main():int{return 2+3;}") is Binary,
            "legacy verified arithmetic parser still works");
        Reject("fnc main():int{let int x=x; return x;}", "self-reference");
        Reject("fnc main():int{return missing;}", "undefined read");
        Reject("fnc main():int{missing=2;return 1;}", "undefined assignment");
        Reject("fnc main():int{let int x=1;let int x=2;return x;}",
            "same-scope duplicate binding");
        Reject("fnc main():int{let int x=true;return 0;}",
            "type mismatch on declaration");
        Reject("fnc main():int{let bool ok=false;ok=3;return 0;}",
            "type mismatch on assignment");
        Reject("fnc main():int{if 1 {return 0;}else{return 1;}}",
            "non-boolean if condition");
        Reject("fnc main():int{while 1 {return 0;}return 1;}",
            "non-boolean while condition");
        Reject("fnc main():int{if true {return 0;}}",
            "missing else return path");
        Reject("fnc main():int{while true {return 0;}}",
            "loop is not proof of return");
        Reject("fnc main():int{return true;}", "wrong return type");
        Reject("fnc main():int{return 1;let int x=2;}",
            "unreachable declaration");
        Reject("fnc main():int{if true {return 1;}else{return 2;} return 3;}",
            "unreachable after both branches return");
        Reject("fnc main():int{if true {let int x=1;}return x;}",
            "block local escaped scope");
        Reject("fnc main():int{let bool x=true==false;return 0;}",
            "bool equality unsupported in first CFG slice");
        Reject("fnc main():int{let bool b=true;return b+1;}",
            "boolean arithmetic");
        Reject("fnc main():int{let int x=1/2;return x;}",
            "int division is not i64 in canonical semantics");
        Reject("fnc main():int{let int x=9223372036854775808;return x;}",
            "literal exceeding i64 range");
        Reject("fnc main():int{let int x=-9223372036854775809;return x;}",
            "negative literal below i64 range");
        Reject("fnc main():int{let int x=1@2;return x;}",
            "invalid character");
        Reject("fnc main():int{let int x=1;}\u0000", "NUL");
        Reject("fnc main():int{return 1;}\uD800", "invalid UTF16");
        Reject("fnc main():int{return 1;}//comment\nextra", "trailing syntax");
        Reject("fnc main():int{return 1;".PadRight(140, ' '),
            "unterminated block");
        Reject(new string(' ', 1_048_577), "source length bound");
        Reject("fnc main():int{return " +
            new string('(', 150) + "1" + new string(')', 150) +
            ";}", "nested expression depth bound");

        // The parser must not publish a block-scoped local after its block.
        var nested = OresStructuredParser.Parse("""
            fnc main(): int {
                let int a = 9;
                if a < 10 {
                    let int b = a + 1;
                    a = b;
                } else {
                    let int b = a - 1;
                    a = b;
                }
                return a;
            }
            """);
        Require(nested.Body[1] is StructuredIf {
            Then.Count: 2, Else.Count: 2
        }, "parallel branch-local scopes");

        Console.WriteLine("Structured source parser and typed diagnostics passed");
    }

    private static bool IsReadOnlyButMutable<T>(this IReadOnlyList<T> list) =>
        list is T[];
}
