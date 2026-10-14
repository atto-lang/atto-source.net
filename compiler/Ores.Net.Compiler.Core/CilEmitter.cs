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
        // Refer to .NET's facade assembly explicitly instead of emitting
        // framework-era mscorlib types from Cecil's default TypeSystem.
        var runtimeRef = new AssemblyNameReference("System.Runtime", new Version(10, 0, 0, 0));
        module.AssemblyReferences.Add(runtimeRef);
        var objectType = new TypeReference("System", "Object", module, runtimeRef);
        var int64Type = new TypeReference("System", "Int64", module, runtimeRef, true);
        var type = new TypeDefinition("Oreslang.Generated", "OresProgram",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            objectType);
        module.Types.Add(type);
        var method = new MethodDefinition("Run",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            int64Type);
        type.Methods.Add(method);
        var il = method.Body.GetILProcessor();
        method.Body.MaxStackSize = Write(program, il);
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

    // Iterative postorder lowering: a valid flat left-associative expression can
    // contain tens of thousands of operations without exhausting the C# call stack.
    // Track actual evaluation stack use and reject malformed or excessive trees.
    private readonly record struct Pending(Expression Node, bool Finish);

    private static int Write(Expression root, ILProcessor il)
    {
        const int maxEvaluationStack = 256;
        var pending = new Stack<Pending>();
        pending.Push(new Pending(root, false));
        int visited = 0, stackDepth = 0, maximum = 0;

        while (pending.Count != 0)
        {
            var item = pending.Pop();
            if (item.Finish)
            {
                if (stackDepth < 2)
                    throw new FormatException("Invalid CIL evaluation stack");
                var op = item.Node switch
                {
                    Unary { Operator: '-' } => OpCodes.Sub_Ovf,
                    Binary { Operator: '+' } => OpCodes.Add_Ovf,
                    Binary { Operator: '-' } => OpCodes.Sub_Ovf,
                    Binary { Operator: '*' } => OpCodes.Mul_Ovf,
                    Binary { Operator: '/' } => OpCodes.Div,
                    _ => throw new FormatException("Unsupported operator")
                };
                il.Emit(op);
                stackDepth--;
            }
            else
            {
                if (++visited > MaxAstNodes)
                    throw new FormatException("AST instruction budget exceeded");
                switch (item.Node)
                {
                    case Literal literal:
                        il.Emit(OpCodes.Ldc_I8, literal.Value);
                        stackDepth++;
                        break;
                    case Unary { Operator: '+' } unary:
                        pending.Push(new Pending(unary.Value, false));
                        break;
                    case Unary { Operator: '-' } unary:
                        il.Emit(OpCodes.Ldc_I8, 0L);
                        stackDepth++;
                        pending.Push(new Pending(unary, true));
                        pending.Push(new Pending(unary.Value, false));
                        break;
                    case Binary binary when binary.Operator is '+' or '-' or '*' or '/':
                        pending.Push(new Pending(binary, true));
                        pending.Push(new Pending(binary.Right, false));
                        pending.Push(new Pending(binary.Left, false));
                        break;
                    default:
                        throw new FormatException("Unrecognized syntax tree node");
                }
            }
            maximum = Math.Max(maximum, stackDepth);
            if (maximum > maxEvaluationStack)
                throw new FormatException("CIL evaluation stack limit exceeded");
        }

        if (stackDepth != 1 || maximum == 0)
            throw new FormatException("Invalid final CIL evaluation stack");
        return maximum;
    }
}
