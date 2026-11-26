using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Ores.Net.Compiler;

// Restricted host-side managed scalar artifact admission. A digest supplied
// by an independently trusted publisher is mandatory. This is not a CLR
// verifier, an IL safety proof, a registry attestation or a loader.
public sealed class PinnedScalarCilArtifact
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly byte[] bytes;
    public string Sha256 { get; }

    private PinnedScalarCilArtifact(byte[] owned, string digest)
    {
        bytes = owned;
        Sha256 = digest;
    }

    public byte[] CopyBytes() => (byte[])bytes.Clone();

    public static PinnedScalarCilArtifact Admit(byte[] candidate, string expectedSha256)
    {
        if (candidate is null || candidate.Length is < 128 or > MaxBytes ||
            expectedSha256 is null || expectedSha256.Length != 64 ||
            expectedSha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new FormatException("E_CIL_ADMISSION: invalid size or digest pin");
        byte[] owned = (byte[])candidate.Clone();
        byte[] actual = SHA256.HashData(owned);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256)))
            throw new FormatException("E_CIL_ADMISSION: digest mismatch");
        try
        {
            using var stream = new MemoryStream(owned, writable: false);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            var cor = headers.CorHeader;
            if (headers.PEHeader is null || cor is null || !pe.HasMetadata ||
                (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0 ||
                cor.Flags != CorFlags.ILOnly || cor.EntryPointTokenOrRelativeVirtualAddress != 0)
                throw new BadImageFormatException("expected managed IL-only library");
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly || md.GetString(md.GetAssemblyDefinition().Name) != "OresGenerated" ||
                md.GetString(md.GetModuleDefinition().Name) != "OresGenerated.dll" ||
                md.TypeDefinitions.Count != 2 || md.MethodDefinitions.Count != 1 ||
                md.FieldDefinitions.Count != 0)
                throw new BadImageFormatException("unexpected scalar metadata inventory");
            bool entry = false, module = false;
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                string name = md.GetString(type.Name);
                if (name == "<Module>" && type.GetMethods().Count == 0 && !module)
                { module = true; continue; }
                if (name != "OresProgram" || md.GetString(type.Namespace) != "Oreslang.Generated" ||
                    type.GetMethods().Count != 1 || entry)
                    throw new BadImageFormatException("unexpected scalar type");
                var method = md.GetMethodDefinition(type.GetMethods().Single());
                if (md.GetString(method.Name) != "Run" ||
                    method.Attributes != (MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                    method.ImplAttributes != MethodImplAttributes.IL ||
                    !md.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(new byte[] { 0, 0, 0x0a }) ||
                    method.RelativeVirtualAddress <= 0 ||
                    pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() is not { Length: > 0 })
                    throw new BadImageFormatException("unexpected scalar entry ABI");
                entry = true;
            }
            if (!entry || !module) throw new BadImageFormatException("missing scalar entry");
        }
        catch (BadImageFormatException e)
        {
            throw new FormatException("E_CIL_ADMISSION: restricted PE metadata rejected", e);
        }
        return new PinnedScalarCilArtifact(owned, expectedSha256);
    }
}
