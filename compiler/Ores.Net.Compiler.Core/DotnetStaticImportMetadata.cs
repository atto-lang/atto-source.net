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
    internal static MemberReferenceHandle MathAbs(MetadataBuilder metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var runtime = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0),
            default,
            metadata.GetOrAddBlob(new byte[] { 0xB0, 0x3F, 0x5F, 0x7F, 0x11, 0xD5, 0x0A, 0x3A }),
            default, default);
        var math = metadata.AddTypeReference(runtime,
            metadata.GetOrAddString("System"), metadata.GetOrAddString("Math"));
        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature().Parameters(
            1,
            returnType => returnType.Type().Int64(),
            parameters => parameters.AddParameter().Type().Int64());
        return metadata.AddMemberReference(math,
            metadata.GetOrAddString("Abs"), metadata.GetOrAddBlob(signature));
    }
}
