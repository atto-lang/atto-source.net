using System.Reflection;
using System.Runtime.Loader;
using Ores.Net.Compiler;

// Compile real source through the shipping structured CFG/CIL route and execute
// the generated assembly on CoreCLR. No interpreter or host model is used.
// NativeAOT and other compiler routes require their own evidence.
static void Check(string source, long expected, string label)
{
    var dir = Path.Combine(Path.GetTempPath(), "atto-bool-alias-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var context = new AssemblyLoadContext("atto-bool-alias", isCollectible: true);
    try
    {
        var output = Path.Combine(dir, "OresGenerated.dll");
        CilEmitter.EmitLibrary(OresStructuredCfgLowering.LowerModule(source), output);
        using var bytes = new MemoryStream(File.ReadAllBytes(output));
        var assembly = context.LoadFromStream(bytes);
        var run = assembly.GetType("Oreslang.Generated.OresProgram", throwOnError: true)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("Generated guest entry missing: " + label);
        if (run.Invoke(null, null) is not long result || result != expected)
            throw new Exception("Wrong generated guest value: " + label);
        Console.WriteLine("PASS guest " + label);
    }
    finally
    {
        context.Unload();
        Directory.Delete(dir, recursive: true);
    }
}

static void Reject(string source, string diagnostic, string label)
{
    try { _ = OresStructuredCfgLowering.LowerModule(source); }
    catch (FormatException ex) when (ex.Message.Contains(diagnostic, StringComparison.Ordinal))
    {
        Console.WriteLine("PASS negative " + label);
        return;
    }
    throw new Exception("Expected " + diagnostic + ": " + label);
}

var alias = CoreTypeRegistry.RequireExecutableStructuredNet("boolean", 0);
if (alias.CanonicalIdentity != CoreTypeRegistry.RequireExecutableStructuredNet("bool", 0).CanonicalIdentity ||
    alias.Ownership != CoreTypeRegistry.Ownership.Copy || CoreTypeRegistry.IsProtectedIdentity("boolean"))
    throw new Exception("Contextual alias metadata mismatch");
if (CoreTypeRegistry.Lookup("Bool")!.IsExecutableInStructuredNet ||
    CoreTypeRegistry.Lookup("String")!.CanonicalIdentity == CoreTypeRegistry.Lookup("string")!.CanonicalIdentity)
    throw new Exception("Existing uppercase or string semantics changed");
Console.WriteLine("PASS registry metadata");

Check("fnc yes(boolean x):bool{return x;}fnc main():int{return yes(true)?42:0;}", 42, "type-first parameter");
Check("fnc yes(x:bool):boolean{return x;}fnc main():int{return yes(false)?0:42;}", 42, "alias return and false");
Check("fnc yes(x:boolean):boolean{return x;}fnc main():int{return yes(true)?42:0;}", 42, "name-first parameter");
Check("fnc main():int{const boolean x=true;return x?42:0;}", 42, "typed const");
Check("fnc main():int{boolean x=true;return x?42:0;}", 42, "bare type-first const");
Check("fnc main():int{let x:boolean=false;x=true;return x?42:0;}", 42, "typed mutable local");
Check("fnc main():int{let int boolean=40;boolean=boolean+2;return boolean;}", 42, "legacy value identifier");
Check("fnc main():int{let boolean=40;boolean=boolean+2;return boolean;}", 42, "legacy inferred identifier");
Check("fnc main():int{return (true as boolean)?42:0;}", 42, "checked identity cast");
Reject("fnc main():int{const boolean x=1;return 42;}", "Local initializer has wrong type", "integer not boolean");
Reject("fnc main():int{return 1 as boolean;}", "Impossible checked scalar cast", "no checked int-to-bool conversion");
Reject("fnc main():int{let x:boolean?=true;return 42;}", "E_OPTION_I64_ONLY", "unlowered optional bool");
Reject("fnc f(x:Bool):bool{return true;}fnc main():int{return 42;}", "E_CORE_TYPE_LOWERING_UNSUPPORTED", "uppercase Bool still unlowered");
Reject("fnc f(x:Boolean):bool{return true;}fnc main():int{return 42;}", "Expected int, bool", "wrong case");
Console.WriteLine("BOOLEAN_ALIAS_GUEST_PASS: 9 CoreCLR guests, 5 compiler negatives, 1 registry check");
