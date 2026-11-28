using Ores.Net.Compiler;
using System.Runtime.Loader;

// This focused compiler test does NOT waive the full runtime suite or its module initializer.
string[] positive = {
    "let b=21; let a <- b; return a+b;",
    "let b=21; let int a <- b; return a+b;",
    "let a=0; let b=21; set a <- b; return a+b;",
    "let a=0; set a <- 42; return a;",
    "let a=0; set a <- 21+21; return a;",
    "let flag <- true; return if flag then 42 else 0 fi;"
};
string[] negative = {
    "const a <- 42; return a;", "int a <- 42; return a;",
    "val a <- 42; return a;", "let a=0; set! a <- 42; return a;",
    "let a=0; a <- 42; return a;", "let a=42; set a <- a; return a;",
    "set missing <- 42; return 0;", "const a=0; set a <- 42; return a;",
    "let a=0; set a <- true; return a;", "let a < - 42; return a;",
    "let a • 42; return a;", "let Option<int> a <- Some(42); return 0;"
};
foreach (string body in positive)
{
    var graph = OresStructuredCfgLowering.Lower("fnc main():int{" + body + "}");
    string folder = Path.Combine(Path.GetTempPath(), "atto-arrow-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var ctx = new AssemblyLoadContext("arrow-guest", isCollectible: true);
    try
    {
        string output = Path.Combine(folder, "guest.dll");
        CilEmitter.EmitLibrary(graph, output);
        using var bytes = new MemoryStream(File.ReadAllBytes(output));
        var guest = ctx.LoadFromStream(bytes);
        var method = guest.GetType("Oreslang.Generated.OresProgram", throwOnError: true)!
            .GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new Exception("Missing generated guest");
        if (Convert.ToInt64(method.Invoke(null, null)) != 42) throw new Exception("Guest mismatch: " + body);
        Console.WriteLine("ARROW_GUEST_PASS " + body);
    }
    finally { ctx.Unload(); Directory.Delete(folder, recursive: true); }
}
foreach (string body in negative)
{
    try { _ = OresStructuredCfgLowering.Lower("fnc main():int{" + body + "}"); }
    catch (FormatException) { Console.WriteLine("ARROW_REJECT_PASS " + body); continue; }
    throw new Exception("Unexpected arrow admission: " + body);
}
Console.WriteLine("NET_ARROW_FOCUSED_PASS 6 emitted CLR guests, 12 rejections; full runtime suite separate");
