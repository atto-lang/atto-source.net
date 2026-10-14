using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ores.Net.Compiler;

/// <summary>Emits a statically referenced managed DLL; the published executable is Native AOT.</summary>
public static class CilEmitter
{
    private const int MaxAstNodes = 100_000;

    public static void EmitLibrary(Expression program, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        var fullPath = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("OresGenerated", new Version(1, 0, 0, 0)),
            "OresGenerated", ModuleKind.Dll);
        var module = assembly.MainModule;
        var type = new TypeDefinition("Oreslang.Generated", "OresProgram",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new MethodDefinition("Run",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Int64);
        type.Methods.Add(method);
        method.Body.MaxStackSize = 256;
        var il = method.Body.GetILProcessor();
        int nodes = 0;
        Write(program, il, 0, ref nodes);
        il.Emit(OpCodes.Ret);
        // Write the completed assembly atomically to prevent a truncated DLL on failure.
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            assembly.Write(tempPath);
            File.Move(tempPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void Write(Expression node, ILProcessor il, int depth, ref int nodes)
    {
        if (++nodes > MaxAstNodes || depth > 128)
            throw new FormatException("AST budget exceeded");
        switch (node)
        {
            case Literal literal:
                il.Emit(OpCodes.Ldc_I8, literal.Value);
                return;
            case Unary { Operator: '+' } unary:
                Write(unary.Value, il, depth + 1, ref nodes);
                return;
            case Unary { Operator: '-' } unary:
                il.Emit(OpCodes.Ldc_I8, 0L);
                Write(unary.Value, il, depth + 1, ref nodes);
                il.Emit(OpCodes.Sub_Ovf);
                return;
            case Binary binary:
                Write(binary.Left, il, depth + 1, ref nodes);
                Write(binary.Right, il, depth + 1, ref nodes);
                il.Emit(binary.Operator switch
                {
                    '+' => OpCodes.Add_Ovf,
                    '-' => OpCodes.Sub_Ovf,
                    '*' => OpCodes.Mul_Ovf,
                    '/' => OpCodes.Div,
                    _ => throw new FormatException("Unknown operator")
                });
                return;
            default:
                throw new FormatException("Unrecognized syntax tree node");
        }
    }
}
