using System.Security.Cryptography;
using Ores.Net.Compiler;

if (args.Length != 2)
    throw new ArgumentException("Expected exact .atto scalar fixture path and output DLL path");

string source = Path.GetFullPath(args[0]);
string generated = Path.GetFullPath(args[1]);
if (!File.Exists(source) || !source.EndsWith(".atto", StringComparison.Ordinal))
    throw new ArgumentException("Only an existing .atto compiler fixture is accepted");
if (string.Equals(source, generated, StringComparison.Ordinal))
    throw new ArgumentException("Compiler input and output must be different");
var verified = OresStructuredCfgLowering.LowerModule(File.ReadAllText(source));
CilEmitter.EmitActorFrameLibrary(verified, generated);
if (!File.Exists(generated) || new FileInfo(generated).Length == 0)
    throw new InvalidOperationException("Actor frame CIL emitter created no nonempty DLL");

Console.WriteLine("ACTOR_COMPILED_CFG_INPUT_SHA256=" +
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant());
Console.WriteLine("ACTOR_COMPILED_CFG_PE_SHA256=" +
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(generated))).ToLowerInvariant());
Console.WriteLine("PASS actor-compiled-cfg-build: verified .atto scalar source emitted deterministic opt-in actor-frame CIL; not yet executed");
