using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Ores.Net.Compiler;

/// <summary>
/// Build-time, metadata-only CIL emitter. Never loads generated assemblies or
/// generates runtime code; compatible with .NET Native AOT and strict trimming.
/// </summary>
public static class CilEmitter
{
    private const int MaxAstNodes = 100_000;
    private const int MaxDepth = 128;

    public static void EmitLibrary(Expression program, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        var ilStream = new BlobBuilder();
        var bodyStream = new MethodBodyStreamEncoder(ilStream);
        var methodCode = new BlobBuilder();
        var instructions = new InstructionEncoder(methodCode);
        int nodes = 0;
        Write(program, instructions, 0, ref nodes);
        instructions.OpCode(ILOpCode.Ret);
        int bodyOffset = bodyStream.AddMethodBody(instructions, maxStack: 256);

        var metadata = new MetadataBuilder();
        // Derive a reproducible module ID from the verified IL stream. A fixed
        // MVID for unrelated programs would let assembly caches conflate them.
        byte[] hash = SHA256.HashData(methodCode.ToArray());
        var moduleId = new Guid(hash.AsSpan(0, 16));
        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString("OresGenerated.dll"),
            mvid: metadata.GetOrAddGuid(moduleId),
            encId: default,
            encBaseId: default);
        metadata.AddAssembly(
            metadata.GetOrAddString("OresGenerated"),
            new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: default,
            hashAlgorithm: System.Reflection.AssemblyHashAlgorithm.None);

        var runtime = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0),
            culture: default,
            publicKeyOrToken: default,
            flags: default,
            hashValue: default);
        var systemObject = metadata.AddTypeReference(
            runtime,
            metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Object"));

        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature().Parameters(
            0,
            returnType => returnType.Type().Int64(),
            parameters => { });

        // Method and type tables are ordered deliberately: <Module> owns zero
        // methods, and the public static OresProgram owns method row #1.
        var run = metadata.AddMethodDefinition(
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("Run"),
            metadata.GetOrAddBlob(signature),
            bodyOffset,
            MetadataTokens.ParameterHandle(1));

        metadata.AddTypeDefinition(
            default, default, metadata.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), run);
        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed |
            TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString("Oreslang.Generated"),
            metadata.GetOrAddString("OresProgram"),
            systemObject,
            MetadataTokens.FieldDefinitionHandle(1),
            run);

        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics:
                Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            ilStream,
            strongNameSignatureSize: 0,
            flags: CorFlags.ILOnly);
        var peBytes = new BlobBuilder();
        pe.Serialize(peBytes);

        var fullPath = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, peBytes.ToArray());
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Write(Expression node, InstructionEncoder il, int depth, ref int nodes)
    {
        if (++nodes > MaxAstNodes || depth > MaxDepth)
            throw new FormatException("AST budget exceeded");
        switch (node)
        {
            case Literal literal:
                il.LoadConstantI8(literal.Value);
                return;
            case Unary { Operator: '+' } unary:
                Write(unary.Value, il, depth + 1, ref nodes);
                return;
            case Unary { Operator: '-' } unary:
                il.LoadConstantI8(0);
                Write(unary.Value, il, depth + 1, ref nodes);
                il.OpCode(ILOpCode.Sub_ovf);
                return;
            case Binary binary:
                Write(binary.Left, il, depth + 1, ref nodes);
                Write(binary.Right, il, depth + 1, ref nodes);
                il.OpCode(binary.Operator switch
                {
                    '+' => ILOpCode.Add_ovf,
                    '-' => ILOpCode.Sub_ovf,
                    '*' => ILOpCode.Mul_ovf,
                    '/' => ILOpCode.Div,
                    _ => throw new FormatException("Unknown operator")
                });
                return;
            default:
                throw new FormatException("Unrecognized syntax tree node");
        }
    }
}
