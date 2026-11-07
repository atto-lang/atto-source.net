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
    // The CLR method header stores the maximum evaluation stack depth.
    // Keep this far below the ECMA-335 maximum to bound compiler work.
    private const int MaxEvaluationStack = 256;

    public static void EmitLibrary(Expression program, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);

        EmitLibraryCore(outputFile, (metadata, bodies) =>
        {
            var methodCode = new BlobBuilder();
            var instructions = new InstructionEncoder(methodCode);
            int maxStack = Write(program, instructions);
            instructions.OpCode(ILOpCode.Ret);
            int bodyOffset = bodies.AddMethodBody(instructions, maxStack: maxStack);
            return (bodyOffset, methodCode, null);
        });
    }

    // Production path: only a verified OresIR function may reach the CIL
    // backend. Each SSA value has a typed i64 local slot, so shared subgraphs
    // and unused instructions have defined, non-exponential execution.
    public static void EmitLibrary(VerifiedIrFunction program, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        const int maxLocalSlots = 16_384;
        if (program.Instructions.Count > maxLocalSlots)
            throw new FormatException("OresIR CIL local slot budget exceeded");

        EmitLibraryCore(outputFile, (metadata, bodies) =>
        {
            int count = program.Instructions.Count;
            var localsBlob = new BlobBuilder();
            var locals = new BlobEncoder(localsBlob).LocalVariableSignature(count);
            for (int i = 0; i < count; i++)
                locals.AddVariable().Type().Int64();
            var localSignature = metadata.AddStandaloneSignature(
                metadata.GetOrAddBlob(localsBlob));

            var methodCode = new BlobBuilder();
            var il = new InstructionEncoder(methodCode);
            int maxStack = WriteVerifiedIr(program, il);
            int bodyOffset = bodies.AddMethodBody(il, maxStack: maxStack,
                localVariablesSignature: localSignature,
                attributes: MethodBodyAttributes.InitLocals);
            return (bodyOffset, methodCode, localsBlob);
        });
    }

    // Experimental CFG entry point. Only a verified function is accepted;
    // the production scalar CLI continues to use straight-line OresIR.
    public static void EmitLibrary(VerifiedCfgFunction program, string outputFile) =>
        CfgCilEmitter.EmitLibrary(program, outputFile);

    // Only parser-checked and independently verified function modules may
    // reach the multi-method emitter; no Reflection.Emit or runtime code load.
    public static void EmitLibrary(VerifiedCfgModule module, string outputFile) =>
        CfgCilEmitter.EmitLibrary(module, outputFile);

    private static int WriteVerifiedIr(VerifiedIrFunction program, InstructionEncoder il)
    {
        int maxStack = 1;
        foreach (var operation in program.Instructions)
        {
            switch (operation.Opcode)
            {
                case IrOpcode.ConstantI64:
                    il.LoadConstantI8(operation.Constant!.Value);
                    break;
                case IrOpcode.SignedRemainderLiteral:
                    throw new FormatException("Removed Oreslang operator '%' cannot reach CIL emission");
                case IrOpcode.CheckedAdd:
                case IrOpcode.CheckedSubtract:
                case IrOpcode.CheckedMultiply:
                case IrOpcode.CheckedDivide:
                    il.LoadLocal(operation.Left!.Value);
                    il.LoadLocal(operation.Right!.Value);
                    il.OpCode(operation.Opcode switch
                    {
                        IrOpcode.CheckedAdd => ILOpCode.Add_ovf,
                        IrOpcode.CheckedSubtract => ILOpCode.Sub_ovf,
                        IrOpcode.CheckedMultiply => ILOpCode.Mul_ovf,
                        _ => ILOpCode.Div
                    });
                    maxStack = 2;
                    break;
                default:
                    throw new FormatException("Unsupported verified IR opcode");
            }
            // Evaluation stack is empty between SSA definitions.
            il.StoreLocal(operation.ResultId);
        }
        il.LoadLocal(program.ReturnId);
        il.OpCode(ILOpCode.Ret);
        return maxStack;
    }

    internal static void EmitLibraryCore(string outputFile,
        Func<MetadataBuilder, MethodBodyStreamEncoder,
            (int BodyOffset, BlobBuilder Code, BlobBuilder? Locals)> buildBody)
    {
        var ilStream = new BlobBuilder();
        var bodyStream = new MethodBodyStreamEncoder(ilStream);
        var metadata = new MetadataBuilder();
        var (bodyOffset, _, localSignatureBlob) = buildBody(metadata, bodyStream);
        // The finalized method body includes branch fixups and the signature
        // *token*, but the token number is not a fingerprint of its blob.
        // Two programs can have identical IL and signature token 0x11000001
        // while having different typed local signatures. Hash both inputs.
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        contentHash.AppendData(ilStream.ToArray());
        if (localSignatureBlob is not null)
            contentHash.AppendData(localSignatureBlob.ToArray());
        byte[] hash = contentHash.GetHashAndReset();
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
            // System.Runtime carries the Microsoft strong-name token; emitting a
            // token-less reference prevents downstream C# and Native AOT binding.
            publicKeyOrToken: metadata.GetOrAddBlob(new byte[] { 0xB0, 0x3F, 0x5F, 0x7F, 0x11, 0xD5, 0x0A, 0x3A }),
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
            flags: CorFlags.ILOnly,
            deterministicIdProvider: DeterministicImageId);
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

    // Experimental multi-method Native AOT assembly. MethodDef handles are
    // reserved before body emission so statically resolved calls target only
    // verified methods in this exact module, never reflection/dynamic code.
    internal static void EmitLibraryModuleCore(string outputFile,
        VerifiedCfgModule module,
        Func<VerifiedCfgFunction, MetadataBuilder, MethodBodyStreamEncoder,
            IReadOnlyDictionary<string, MethodDefinitionHandle>,
            (int BodyOffset, BlobBuilder Code, BlobBuilder? Locals)> buildBody)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        var methods = new[] { module.Main }.Concat(module.Helpers).ToArray();
        if (methods.Length is < 1 or > 65 || methods[0].Name != "main" ||
            methods[0].ResultType != IrValueType.I64 ||
            methods[0].Parameters.Count != 0)
            throw new FormatException("Invalid verified CFG module entry signature");

        var handles = new Dictionary<string, MethodDefinitionHandle>(
            StringComparer.Ordinal);
        for (int i = 0; i < methods.Length; i++)
        {
            if (!handles.TryAdd(methods[i].Name, MetadataTokens.MethodDefinitionHandle(i + 1)))
                throw new FormatException("Duplicate CFG method definition");
        }

        // Independently link-check the entire module before emitting even
        // one method body. An individually verified CFG's call signatures
        // could otherwise disagree with the MethodDefs actually packaged
        // alongside it by a future front end.
        ValidateStaticModuleLinkage(methods, handles);

        var ilStream = new BlobBuilder();
        var bodyStream = new MethodBodyStreamEncoder(ilStream);
        var metadata = new MetadataBuilder();
        var bodies = new List<(int Offset, BlobBuilder? Locals)>();
        long instructions = 0;
        foreach (var method in methods)
        {
            instructions += method.Blocks.Sum(b => b.Parameters.Count + b.Instructions.Count);
            if (instructions > 100_000)
                throw new FormatException("CFG module total instruction budget exceeded");
            var (offset, _, locals) = buildBody(method, metadata, bodyStream, handles);
            bodies.Add((offset, locals));
        }
        if (ilStream.Count > 8 * 1024 * 1024)
            throw new FormatException("CFG module emitted CIL byte budget exceeded");

        var signatureBlobs = new List<BlobBuilder>();
        foreach (var method in methods)
        {
            var blob = new BlobBuilder();
            new BlobEncoder(blob).MethodSignature().Parameters(
                method.Parameters.Count,
                returnType =>
                {
                    if (method.ResultType == IrValueType.I64) returnType.Type().Int64();
                    else if (method.ResultType == IrValueType.Bool) returnType.Type().Boolean();
                    else throw new FormatException("Unsupported CIL method result");
                },
                parameters =>
                {
                    foreach (var parameter in method.Parameters)
                    {
                        var encoded = parameters.AddParameter().Type();
                        if (parameter == IrValueType.I64) encoded.Int64();
                        else if (parameter == IrValueType.Bool) encoded.Boolean();
                        else throw new FormatException("Unsupported CIL method parameter");
                    }
                });
            signatureBlobs.Add(blob);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(ilStream.ToArray());
        foreach (var entry in bodies)
            if (entry.Locals is not null) hash.AppendData(entry.Locals.ToArray());
        foreach (var signature in signatureBlobs)
            hash.AppendData(signature.ToArray());
        // The MVID must also change when a method is renamed but its body
        // and signature stay byte-for-byte identical.
        foreach (var method in methods)
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(method.Name + "\0"));
        var mvid = new Guid(hash.GetHashAndReset().AsSpan(0, 16));
        metadata.AddModule(0, metadata.GetOrAddString("OresGenerated.dll"),
            metadata.GetOrAddGuid(mvid), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("OresGenerated"),
            new Version(1, 0, 0, 0), default, default, default,
            System.Reflection.AssemblyHashAlgorithm.None);
        var runtime = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0), default,
            metadata.GetOrAddBlob(new byte[] { 0xB0, 0x3F, 0x5F, 0x7F, 0x11, 0xD5, 0x0A, 0x3A }),
            default, default);
        var systemObject = metadata.AddTypeReference(runtime,
            metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

        // Explicit Param rows are essential for stable method-local argument
        // metadata. Each MethodDef.ParamList points to the first row owned by
        // that method (or the next row for a zero-argument method). Parameter
        // *types* remain authoritative in the already verified signature.
        int nextParameterRow = 1;
        for (int i = 0; i < methods.Length; i++)
        {
            var handle = metadata.AddMethodDefinition(
                (i == 0 ? MethodAttributes.Public : MethodAttributes.Private) |
                    MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodImplAttributes.IL,
                metadata.GetOrAddString(i == 0 ? "Run" : "helper_" + methods[i].Name),
                metadata.GetOrAddBlob(signatureBlobs[i]),
                bodies[i].Offset, MetadataTokens.ParameterHandle(nextParameterRow));
            if (handle != handles[methods[i].Name])
                throw new FormatException("CFG method table token mismatch");
            for (int argument = 0; argument < methods[i].Parameters.Count; argument++)
            {
                metadata.AddParameter(ParameterAttributes.None,
                    metadata.GetOrAddString("arg" + argument), argument + 1);
                nextParameterRow++;
            }
        }

        var run = handles["main"];
        metadata.AddTypeDefinition(default, default,
            metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), run);
        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed |
            TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString("Oreslang.Generated"),
            metadata.GetOrAddString("OresProgram"), systemObject,
            MetadataTokens.FieldDefinitionHandle(1), run);
        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics:
                Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), ilStream,
            strongNameSignatureSize: 0, flags: CorFlags.ILOnly,
            deterministicIdProvider: DeterministicImageId);
        var bytes = new BlobBuilder();
        pe.Serialize(bytes);

        string full = Path.GetFullPath(outputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes.ToArray());
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // This second gate cross-checks actual module member signatures after
    // SSA verification. Calls must be intra-module, precisely typed, and
    // acyclic; helpers may only call earlier helpers. The entry may call any
    // helper, but helpers can never call main.
    private static void ValidateStaticModuleLinkage(
        IReadOnlyList<VerifiedCfgFunction> methods,
        IReadOnlyDictionary<string, MethodDefinitionHandle> handles)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < methods.Count; index++)
            if (!positions.TryAdd(methods[index].Name, index))
                throw new FormatException("Duplicate CFG method definition");

        long definitions = 0;
        for (int index = 0; index < methods.Count; index++)
        {
            var caller = methods[index];
            int count = caller.Blocks.Sum(b => b.Parameters.Count + b.Instructions.Count);
            definitions += count;
            if (definitions > 100_000 || count is <= 0 or > OresCfgVerifier.MaxValues)
                throw new FormatException("CFG module SSA budget exceeded");

            // SSA IDs have already been checked for uniqueness and density
            // by the per-function verifier. Repeat type admission here to
            // ensure call operands match the actual packaged MethodDef.
            var valueTypes = new IrValueType[count];
            foreach (var block in caller.Blocks)
            {
                foreach (var parameter in block.Parameters)
                    valueTypes[parameter.Id] = parameter.Type;
                foreach (var op in block.Instructions)
                    valueTypes[op.ResultId] = op.Type;
            }

            foreach (var op in caller.Blocks.SelectMany(b => b.Instructions))
            {
                if (op.Opcode != CfgOpcode.Call) continue;
                if (op.Target is null || op.Arguments is null ||
                    !positions.TryGetValue(op.Target, out int targetIndex) ||
                    !handles.ContainsKey(op.Target) ||
                    targetIndex == 0 ||
                    (index != 0 && targetIndex >= index))
                    throw new FormatException("Unlinked or cyclic CFG static call");
                var callee = methods[targetIndex];
                if (op.Type != callee.ResultType ||
                    op.Arguments.Count != callee.Parameters.Count)
                    throw new FormatException("CFG MethodDef call signature mismatch");
                for (int argument = 0; argument < op.Arguments.Count; argument++)
                {
                    int operandId = op.Arguments[argument];
                    if (operandId < 0 || operandId >= count ||
                        valueTypes[operandId] != callee.Parameters[argument])
                        throw new FormatException("CFG MethodDef argument type mismatch");
                }
            }
        }
    }

    // Without an explicit provider PEBuilder creates a time-based COFF stamp,
    // even when the metadata MVID is stable. Hash the entire pre-stamped
    // content to produce a reproducible native-toolchain input.
    private static BlobContentId DeterministicImageId(IEnumerable<Blob> blobs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var blob in blobs)
            hash.AppendData(blob.GetBytes().AsSpan());
        return BlobContentId.FromHash(hash.GetHashAndReset());
    }

    private readonly record struct Pending(Expression Node, bool EmitOperator);

    // A flat expression such as a+b+c+... may create thousands of AST nodes,
    // even with shallow source nesting. Traversal MUST NOT recurse on the CLR
    // call stack. Emit exact postorder CIL and track its evaluation stack.
    private static int Write(Expression root, InstructionEncoder il)
    {
        var pending = new Stack<Pending>();
        pending.Push(new Pending(root, false));
        int nodes = 0;
        int evaluationDepth = 0;
        int maximum = 0;

        while (pending.Count > 0)
        {
            var work = pending.Pop();
            if (work.EmitOperator)
            {
                if (evaluationDepth < 2)
                    throw new FormatException("Invalid CIL operator stack");
                var opcode = work.Node switch
                {
                    Unary { Operator: '-' } => ILOpCode.Sub_ovf,
                    Binary { Operator: '+' } => ILOpCode.Add_ovf,
                    Binary { Operator: '-' } => ILOpCode.Sub_ovf,
                    Binary { Operator: '*' } => ILOpCode.Mul_ovf,
                    Binary { Operator: '/' } => ILOpCode.Div,
                    _ => throw new FormatException("Invalid CIL operator")
                };
                il.OpCode(opcode);
                evaluationDepth--;
            }
            else
            {
                if (++nodes > MaxAstNodes)
                    throw new FormatException("AST budget exceeded");

                switch (work.Node)
                {
                    case Literal literal:
                        il.LoadConstantI8(literal.Value);
                        evaluationDepth++;
                        break;
                    case Unary { Operator: '+' } unary:
                        pending.Push(new Pending(unary.Value, false));
                        break;
                    case Unary { Operator: '-' } unary:
                        il.LoadConstantI8(0);
                        evaluationDepth++;
                        pending.Push(new Pending(unary, true));
                        pending.Push(new Pending(unary.Value, false));
                        break;
                    case Binary binary when binary.Operator is '+' or '-' or '*' or '/':
                        pending.Push(new Pending(binary, true));
                        pending.Push(new Pending(binary.Right, false));
                        pending.Push(new Pending(binary.Left, false));
                        break;
                    default:
                        throw new FormatException("Unsupported AST expression");
                }
            }
            maximum = Math.Max(maximum, evaluationDepth);
            if (maximum > MaxEvaluationStack)
                throw new FormatException("CIL evaluation stack budget exceeded");
        }

        if (evaluationDepth != 1 || maximum < 1)
            throw new FormatException("Invalid final CIL stack");
        return maximum;
    }
}
