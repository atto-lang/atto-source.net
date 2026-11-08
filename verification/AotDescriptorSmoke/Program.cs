using Ores.Net.Runtime;
using System.Runtime.CompilerServices;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception("AOT_METADATA_FAILURE: " + message);
}
static void Reject(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new Exception("AOT_METADATA_REJECTION_MISSING: " + message);
}

var fields = new List<AotPublicField> { new("value", AotPublicFieldKind.Int) };
var descriptor = new AotPublicTypeDescriptor("Example.PublicCounter", fields,
    new[] { new AotPublicMethod("read", 0), new AotPublicMethod("add", 1) });
var catalog = new AotPublicTypeCatalog(new[] { descriptor });
Require(catalog.Count == 1, "only explicitly published type is visible");
Require(catalog.TryGetPublicType("Example.PublicCounter", out var found) &&
    ReferenceEquals(found, descriptor), "exact public lookup");
Require(!catalog.TryGetPublicType("Example.InternalPrivateState", out _),
    "private actor/state entries must not be discovered");
Require(!catalog.TryGetPublicType("System.String", out _),
    "ambient CLR types must not be reflected into guest lookup");
Require(!catalog.TryGetPublicType("Example.PublicCounter<Runtime>", out _),
    "late generic specialization must fail closed");
Require(!catalog.TryGetPublicType("../Example.PublicCounter", out _),
    "non-canonical query must fail closed");
fields.Clear();
Require(descriptor.Fields.Count == 1, "caller cannot mutate copied public metadata");
Require(descriptor.Fields[0].Name == "value", "public field retention");

Reject(() => new AotPublicTypeCatalog(new[] { descriptor, descriptor }), "duplicate types");
Reject(() => new AotPublicTypeCatalog(new AotPublicTypeDescriptor?[] { descriptor, null! }!),
    "null type descriptors");
Reject(() => new AotPublicTypeDescriptor("Example.PublicCounter",
    new[] { new AotPublicField("value", AotPublicFieldKind.Int),
            new AotPublicField("value", AotPublicFieldKind.Bool) },
    Array.Empty<AotPublicMethod>()), "duplicate fields");
Reject(() => new AotPublicTypeDescriptor("Example.PublicCounter",
    new[] { new AotPublicField("actor_private_ptr", (AotPublicFieldKind)99) },
    Array.Empty<AotPublicMethod>()), "unsupported field kind");
Reject(() => new AotPublicTypeDescriptor("A",
    Enumerable.Range(0, AotPublicTypeDescriptor.MaxFields + 1)
        .Select(i => new AotPublicField("F" + i, AotPublicFieldKind.Int)),
    Array.Empty<AotPublicMethod>()), "bounded field list");
Reject(() => new AotPublicTypeDescriptor("A", Array.Empty<AotPublicField>(),
    Enumerable.Range(0, AotPublicTypeDescriptor.MaxMethods + 1)
        .Select(i => new AotPublicMethod("M" + i, 0))), "bounded method list");
Reject(() => new AotPublicTypeCatalog(
    Enumerable.Range(0, AotPublicTypeCatalog.MaxTypes + 1)
        .Select(i => new AotPublicTypeDescriptor("T" + i,
            Array.Empty<AotPublicField>(), Array.Empty<AotPublicMethod>()))),
    "bounded public type catalog");
foreach (var bad in new[] { "Bad..Name", "Other.Private.", ".Other", "A<B>",
    "System.String, System.Private", "bad/path", "éclair", "A\nB", "",
    "A_B.9Start", "A\tB" })
{
    Require(!AotPublicTypeCatalog.IsCanonicalName(bad), "reject symbol: " + bad);
}
Reject(() => new AotPublicTypeDescriptor("A", Array.Empty<AotPublicField>(),
    new[] { new AotPublicMethod("f", 2), new AotPublicMethod("f", 2) }),
    "duplicate method arity");
Reject(() => new AotPublicTypeDescriptor("A", Array.Empty<AotPublicField>(),
    new[] { new AotPublicMethod("f", 17) }), "method arity bounds");

if (Environment.GetEnvironmentVariable("ATTO_EXPECT_NATIVE_AOT") == "1")
{
    Require(!RuntimeFeature.IsDynamicCodeSupported,
        "NativeAOT mode must disable runtime code generation");
    Console.WriteLine("AOT_PUBLIC_METADATA_NATIVE_BINARY|PASS|NOT_GUEST|NOT_TRIM_VERIFIED");
}
else
{
    Console.WriteLine("AOT_PUBLIC_METADATA_HOST_ONLY|PASS|NOT_GUEST|NOT_TRIM_VERIFIED");
}
