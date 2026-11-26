using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Ores.Net.Compiler;

/// <summary>Trusted-host admission for the closed single-method scalar CIL
/// profile. Not a general IL verifier, signed provenance or native code sandbox.
/// Expected digest must come from an independent trusted host record.</summary>
public sealed class PinnedScalarCilArtifact
{
    private readonly byte[] bytes;
    public string Sha256 { get; }
    private PinnedScalarCilArtifact(byte[] bytes, string sha256)
    {
        this.bytes = bytes;
        Sha256 = sha256;
    }
    public byte[] CopyBytes() => (byte[])bytes.Clone();

    public static PinnedScalarCilArtifact Admit(byte[] candidate, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Length is < 128 or > 4 * 1024 * 1024 ||
            expectedSha256 is null || expectedSha256.Length != 64 ||
            expectedSha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new FormatException("E_CIL_ADMISSION: invalid size or pin");
        byte[] snapshot = (byte[])candidate.Clone();
        string actual = Convert.ToHexStringLower(SHA256.HashData(snapshot));
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new FormatException("E_CIL_ADMISSION: digest mismatch");
        using var pe = new PEReader(new MemoryStream(snapshot, writable: false));
        var cor = pe.PEHeaders.CorHeader;
        if (!pe.HasMetadata || cor is null || cor.Flags != CorFlags.ILOnly ||
            cor.EntryPointTokenOrRelativeVirtualAddress != 0 ||
            cor.ManagedNativeHeaderDirectory.Size != 0 ||
            cor.ResourcesDirectory.Size != 0 ||
            cor.StrongNameSignatureDirectory.Size != 0)
            throw new FormatException("E_CIL_ADMISSION: not closed IL-only library");
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly || md.GetString(md.GetAssemblyDefinition().Name) != "OresGenerated" ||
            md.GetString(md.GetModuleDefinition().Name) != "OresGenerated.dll" ||
            md.TypeDefinitions.Count != 2 || md.MethodDefinitions.Count != 1 ||
            md.FieldDefinitions.Count != 0 || md.MemberReferences.Count != 0 ||
            md.AssemblyReferences.Count != 1 || md.TypeReferences.Count != 1)
            throw new FormatException("E_CIL_ADMISSION: unexpected metadata inventory");
        foreach (var handle in md.AssemblyReferences)
        {
            var r = md.GetAssemblyReference(handle);
            if (md.GetString(r.Name) != "System.Runtime" || r.Version != new Version(10,0,0,0) ||
                !md.GetBlobBytes(r.PublicKeyOrToken).AsSpan().SequenceEqual(new byte[]{0xB0,0x3F,0x5F,0x7F,0x11,0xD5,0x0A,0x3A}))
                throw new FormatException("E_CIL_ADMISSION: framework identity mismatch");
        }
        int owners = 0;
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            string name = md.GetString(type.Name);
            if (name == "<Module>" && type.GetMethods().Count == 0) continue;
            if (name != "OresProgram" || md.GetString(type.Namespace) != "Oreslang.Generated" ||
                type.GetMethods().Count != 1 || type.GetInterfaceImplementations().Count != 0)
                throw new FormatException("E_CIL_ADMISSION: unexpected method owner");
            owners++;
        }
        if (owners != 1) throw new FormatException("E_CIL_ADMISSION: missing owner");
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (md.GetString(method.Name) != "Run" ||
                method.Attributes != (MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                method.ImplAttributes != MethodImplAttributes.IL ||
                !md.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(new byte[]{0,0,0x0a}) ||
                method.RelativeVirtualAddress == 0)
                throw new FormatException("E_CIL_ADMISSION: scalar entry ABI mismatch");
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (body.ExceptionRegions.Length != 0 || body.MaxStack > 256 ||
                body.GetILBytes() is not { Length: > 0 and <= 65536 })
                throw new FormatException("E_CIL_ADMISSION: invalid method body shape");
        }
        return new PinnedScalarCilArtifact(snapshot, actual);
    }
}
