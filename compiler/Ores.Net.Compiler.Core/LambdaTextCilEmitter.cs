using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace Ores.Net.Compiler;

/// <summary>
/// Deterministic metadata-only CIL emitter for the narrow String -> String
/// Lambda source subset. Generated code is statically linked by NativeAOT; this
/// emitter never loads the generated assembly.
/// </summary>
public static class LambdaTextCilEmitter
{
    public static void EmitLibrary(
        LambdaTextProgram program,
        string outputFile)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        if (!string.Equals(
                program.FunctionName,
                "invoke",
                StringComparison.Ordinal))
            throw new FormatException(
                "Lambda NativeAOT host currently requires public function 'invoke'");

        var ilStream = new BlobBuilder();
        var bodies = new MethodBodyStreamEncoder(ilStream);
        var metadata = new MetadataBuilder();

        var runtime = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0),
            culture: default,
            publicKeyOrToken: metadata.GetOrAddBlob(
                new byte[] {
                    0xB0, 0x3F, 0x5F, 0x7F,
                    0x11, 0xD5, 0x0A, 0x3A
                }),
            flags: default,
            hashValue: default);
        var systemObject = metadata.AddTypeReference(
            runtime,
            metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Object"));
        var systemString = metadata.AddTypeReference(
            runtime,
            metadata.GetOrAddString("System"),
            metadata.GetOrAddString("String"));

        var equalsSignature = new BlobBuilder();
        new BlobEncoder(equalsSignature)
            .MethodSignature(isInstanceMethod: false)
            .Parameters(
                2,
                returnType => returnType.Type().Boolean(),
                parameters =>
                {
                    parameters.AddParameter().Type().String();
                    parameters.AddParameter().Type().String();
                });
        var stringEquals = metadata.AddMemberReference(
            systemString,
            metadata.GetOrAddString("op_Equality"),
            metadata.GetOrAddBlob(equalsSignature));

        var code = new BlobBuilder();
        var il = new InstructionEncoder(code, new ControlFlowBuilder());
        foreach (var arm in program.Arms)
        {
            var next = il.DefineLabel();
            il.LoadArgument(0);
            il.LoadString(metadata.GetOrAddUserString(arm.Match));
            il.Call(stringEquals);
            il.Branch(ILOpCode.Brfalse, next);
            il.LoadString(metadata.GetOrAddUserString(arm.Response));
            il.OpCode(ILOpCode.Ret);
            il.MarkLabel(next);
        }
        il.LoadString(metadata.GetOrAddUserString(program.DefaultResponse));
        il.OpCode(ILOpCode.Ret);

        const int maxMethodBytes = 4 * 1024 * 1024;
        if (code.Count > maxMethodBytes)
            throw new FormatException(
                "Lambda text CIL bytecode budget exceeded");
        var bodyOffset = bodies.AddMethodBody(il, maxStack: 2);

        var moduleId = ProgramModuleId(program, ilStream);
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
            hashAlgorithm: AssemblyHashAlgorithm.None);

        var invokeSignature = new BlobBuilder();
        new BlobEncoder(invokeSignature)
            .MethodSignature(isInstanceMethod: false)
            .Parameters(
                1,
                returnType => returnType.Type().String(),
                parameters =>
                    parameters.AddParameter().Type().String());

        var parameter = metadata.AddParameter(
            ParameterAttributes.None,
            metadata.GetOrAddString(program.ParameterName),
            sequenceNumber: 1);

        var invoke = metadata.AddMethodDefinition(
            MethodAttributes.Public |
                MethodAttributes.Static |
                MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("Invoke"),
            metadata.GetOrAddBlob(invokeSignature),
            bodyOffset,
            parameter);

        metadata.AddTypeDefinition(
            default,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            invoke);
        metadata.AddTypeDefinition(
            TypeAttributes.Public |
                TypeAttributes.Abstract |
                TypeAttributes.Sealed |
                TypeAttributes.Class |
                TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString("Oreslang.Generated"),
            metadata.GetOrAddString("OresProgram"),
            systemObject,
            MetadataTokens.FieldDefinitionHandle(1),
            invoke);

        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(
                imageCharacteristics:
                    Characteristics.ExecutableImage |
                    Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            ilStream,
            strongNameSignatureSize: 0,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: DeterministicImageId);
        var peBytes = new BlobBuilder();
        pe.Serialize(peBytes);

        PublishAtomically(outputFile, peBytes.ToArray());
    }

    private static Guid ProgramModuleId(
        LambdaTextProgram program,
        BlobBuilder ilStream)
    {
        using var hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(ilStream.ToArray());
        AppendString(hash, program.ModuleName ?? "");
        AppendString(hash, program.FunctionName);
        AppendString(hash, program.ParameterName);
        foreach (var arm in program.Arms)
        {
            AppendString(hash, arm.Match);
            AppendString(hash, arm.Response);
        }
        AppendString(hash, program.DefaultResponse);
        return new Guid(hash.GetHashAndReset().AsSpan(0, 16));
    }

    private static void AppendString(
        IncrementalHash hash,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static BlobContentId DeterministicImageId(
        IEnumerable<Blob> blobs)
    {
        using var hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var blob in blobs)
            hash.AppendData(blob.GetBytes().AsSpan());
        return BlobContentId.FromHash(hash.GetHashAndReset());
    }

    private static void PublishAtomically(
        string outputFile,
        byte[] bytes)
    {
        var fullPath = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)!);
        var temporary =
            fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
