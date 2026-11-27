using Ores.Net.Compiler;

internal static class AttachedStructTargetTests
{
    public static void Run()
    {
        const string grouped = """
            type Point = struct { int x; int y; } with {
                constructor(int x, int y) { self.x = x; self.y = y; }
                mtd int • sum() { return self.x + self.y; }
                mtd int <- product() { return self.x * self.y; }
                mtd is_origin() : bool { return self.x == 0 && self.y == 0; }
            }
            fnc main():int { return 0; }
            """;
        const string chained = """
            type Point = struct { int x; int y; }
            with constructor(int x, int y) { self.x = x; self.y = y; }
            with mtd int • sum() { return self.x + self.y; }
            with mtd int <- product() { return self.x * self.y; }
            with mtd is_origin() : bool { return self.x == 0 && self.y == 0; }
            fnc main():int { return 0; }
            """;
        foreach (var source in new[] { grouped, chained,
            grouped.Replace("int • sum()", "sum():int"),
            grouped.Replace("int <- product()", "int • product()"),
            grouped.Replace("is_origin() : bool", "bool <- is_origin()"),
            grouped.Replace("type Point = struct", "define struct Point").Replace("is_origin() : bool", "is_origin() -> bool"),
            chained.Replace("type Point = struct", "define struct Point").Replace("is_origin() : bool", "is_origin() -> bool"),
            grouped.Replace("mtd int • sum()", "mut mtd shift(int n):void { self.x = n; } mtd int • sum()"),
            grouped.Replace("constructor(int x, int y)", "constructor(bool flag) { self.x = 1; self.y = 0; } constructor(int x, int y)"),
            grouped.Replace("with {", "with /* nested /* trivia */ comment */ {")
        })
            Reject(source, "NET_STRUCT_WITH_NATIVEAOT_UNSUPPORTED");
        Reject(grouped.Replace("is_origin() : bool", "bool * is_origin()"), "NET_STRUCT_WITH_SYNTAX");
        Reject(grouped.Replace("int <- product()", "int <- sum()"), "NET_STRUCT_WITH_DUPLICATE");
        Reject(grouped.Replace("type Point = struct", "define struct Point").Replace("int <- product()", "sum() -> int"), "NET_STRUCT_WITH_DUPLICATE");
        Reject(grouped.Replace("int y;", "int x;"), "NET_STRUCT_WITH_DUPLICATE");
        Reject(grouped.Replace("constructor(int x, int y)", "constructor(int a, int b) {} constructor(int x, int y)"), "NET_STRUCT_WITH_DUPLICATE");
        Reject(grouped.Replace("constructor(int x, int y)", "mut constructor(int x, int y)"), "NET_STRUCT_WITH_SYNTAX");
        Reject(grouped.Replace("constructor(int x, int y)", "print(1); constructor(int x, int y)"), "NET_STRUCT_WITH_SYNTAX");
        Reject("fnc main():int { const a=1; const b=a with {x:2}; return 0; }", "WITH_BACKEND_UNSUPPORTED");
        _ = OresStructuredParser.Parse("/* type Point = struct { int x; } with {} */ fnc main():int{return 42;}");
        Console.WriteLine("NET_ATTACHED_STRUCT_HEADER_ADMISSION_PASS: 10 target forms; 8 rejection cases; scalar regression");
    }
    private static void Reject(string source, string diagnostic)
    {
        try { _ = OresStructuredParser.Parse(source); }
        catch (FormatException ex) when (ex.Message.Contains(diagnostic, StringComparison.Ordinal)) { return; }
        throw new Exception("Expected " + diagnostic + " before CIL");
    }
}
