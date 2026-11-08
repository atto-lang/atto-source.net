using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Ores.Net.Compiler;

/// <summary>
/// Closed, metadata-only AOT .NET host import v0.  Assembly resolution is
/// explicit, no runtime reflection or Assembly.Load is permitted.  The
/// System.Runtime facade reference is resolved by the pinned .NET 10 build.
/// Broader NuGet imports require an independently reviewed manifest/linker.
/// </summary>
internal static class DotnetStaticImportMetadata
{
    // One System.Runtime AssemblyRef per emitted assembly. This handle is
    // created by CilEmitter before writing any body and reused by every
    // static BCL member. Adding duplicate AssemblyRef rows is forbidden.
    internal static AssemblyReferenceHandle AddSystemRuntime(MetadataBuilder metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0),
            default,
            metadata.GetOrAddBlob(new byte[] { 0xB0, 0x3F, 0x5F, 0x7F, 0x11, 0xD5, 0x0A, 0x3A }),
            default, default);
    }

    internal static MemberReferenceHandle MathAbs(
        MetadataBuilder metadata, AssemblyReferenceHandle runtime) =>
        CreateMathCall(metadata, runtime, "Abs", 1);

    internal static MemberReferenceHandle MathBinary(
        MetadataBuilder metadata, AssemblyReferenceHandle runtime, string method)
    {
        if (method is not ("Max" or "Min"))
            throw new FormatException("Unapproved .NET static host method");
        return CreateMathCall(metadata, runtime, method, 2);
    }

    private static MemberReferenceHandle CreateMathCall(
        MetadataBuilder metadata, AssemblyReferenceHandle runtime,
        string method, int arity)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (runtime.IsNil)
            throw new FormatException("Unlinked .NET System.Runtime assembly reference");
        var math = metadata.AddTypeReference(runtime,
            metadata.GetOrAddString("System"), metadata.GetOrAddString("Math"));
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature().Parameters(
            arity,
            returnType => returnType.Type().Int64(),
            parameters =>
            {
                for (int i = 0; i < arity; i++)
                    parameters.AddParameter().Type().Int64();
            });
        return metadata.AddMemberReference(math,
            metadata.GetOrAddString(method), metadata.GetOrAddBlob(signature));
    }
}
