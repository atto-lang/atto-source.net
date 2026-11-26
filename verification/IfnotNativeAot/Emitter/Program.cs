using Ores.Net.Compiler;

// Compiler-side trusted verification tool: no reflection and no guest source
// fallback. Emits the exact bounded CFG/CIL that NativeAOT will link.
if (args.Length != 2)
    throw new ArgumentException("usage: Emitter FIXTURE.atto OUTPUT/OresGenerated.dll");
var sourcePath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
if (!sourcePath.EndsWith(".atto", StringComparison.Ordinal))
    throw new ArgumentException("NativeAOT fixture must be .atto");
var code = File.ReadAllText(sourcePath);
var checkedCfg = OresStructuredCfgLowering.Lower(code);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
CilEmitter.EmitLibrary(checkedCfg, outputPath);
Console.WriteLine("ATTO_IFNOT_NATIVEAOT_EMITTED_VERIFIED_CIL");
