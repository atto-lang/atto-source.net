using Ores.Net.Compiler;

// Build-time compiler driver, used only for explicit bounded native evidence.
if (args.Length != 2) return 2;
try {
    var graph=OresStructuredCfgLowering.LowerAttachedStructScalar(File.ReadAllText(args[0]));
    CilEmitter.EmitLibrary(graph,args[1]);
    return 0;
} catch(FormatException ex) { Console.Error.WriteLine(ex.Message); return 1; }
