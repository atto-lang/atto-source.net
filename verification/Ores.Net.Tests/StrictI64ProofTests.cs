using Ores.Net.Compiler;

static class StrictI64ProofTests
{
    public static void Run()
    {
        const string pre = "define dimension Time; define unit Second: Time; type Bounded = int where self >= 1 and self <= 42; ";
        static string Main(string expression) => "pub fnc main() -> int { return " + expression + "; }";
        foreach (string body in new[] {
            Main("40+2"), Main("84 / 2"), Main("42 as Bounded"),
            "fnc f(Bounded x) -> int { return x+1; } " + Main("f(41)"),
            Main("-9223372036854775808"),
        })
            _ = OresParser.LowerVerifiedStrict(pre + body);
        foreach (string body in new[] {
            Main("42/0"), Main("-9223372036854775808 / -1"),
            Main("9223372036854775807+1"), Main("(9223372036854775807+1)*0"),
            "fnc f(int x) -> int { return -x; } " + Main("f(-42)"),
            "fnc f(int x) -> int { return x+1; } " + Main("f(41)"),
            "fnc f(int x) -> Bounded { return x as Bounded; } " + Main("f(42)"),
            "pub fnc main() -> int { val int x = 9223372036854775807+1; return 42; }",
        })
            Reject(pre+body, "E_VERIFY_UNPROVEN");
        foreach(string body in new[] {
            "fnc f(int x) -> int { if x != 0 { return 42/x; } return 0; } " + Main("f(1)"),
            "fnc f(int x) -> int { if x != 0 && 42/x > 0 { return 1; } return 42; } " + Main("f(0)"),
            "fnc f(int x) -> int { if x == 0 || 42/x > 0 { return 42; } return 0; } " + Main("f(0)"),
            "fnc f(int x) -> int { if x != 0 && x != -1 { return -9223372036854775808/x; } return 0; } " + Main("f(2)"),
            "fnc f(int x) -> int { if !(x == 0) { return 42/x; } else { return 42; } } " + Main("f(0)"),
            "fnc f(int x) -> int { if x < 0 || x > 0 { return 42/x; } return 0; } " + Main("f(-1)")
        }) _=OresParser.LowerVerifiedStrict(pre+body);
        foreach(string body in new[] {
            "fnc f(int x) -> int { if 42/x > 0 && x != 0 { return 42; } return 0; } " + Main("f(1)"),
            "fnc f(int x) -> int { if x != 0 { return -9223372036854775808/x; } return 0; } " + Main("f(2)"),
            "fnc f(int x) -> int { if x != 0 || 42/x > 0 { return 42; } return 0; } " + Main("f(0)"),
            "fnc f(int x, int y) -> int { if x != 0 { return 42/y; } return 0; } " + Main("f(1,1)"),
            "fnc f(int x) -> int { if x != 0 || x == 0 { return 42/x; } return 0; } " + Main("f(1)")
        }) Reject(pre+body,"E_VERIFY_UNPROVEN");
        Reject(Main("42"),"E_VERIFY_UNSUPPORTED");
        Reject(pre+"type Narrow = i32 where self > 0; "+Main("42"),"E_VERIFY_UNSUPPORTED");
        Console.WriteLine("PASS strict signed-i64 source-to-CFG admission (not native guest execution)");
    }

    private static void Reject(string source,string code)
    {
        try { _ = OresParser.LowerVerifiedStrict(source); }
        catch (FormatException e) when (e.Message.Contains(code + ":",StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected " + code + " for " + source);
    }
}
