using Ores.Net.Runtime;

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
var fields = new List<AotPublicField> {
    new("value", AotPublicFieldKind.Int),
};
var descriptor = new AotPublicTypeDescriptor("Example.PublicCounter", fields,
    new[] { new AotPublicMethod("read", 0), new AotPublicMethod("add", 1) });
var catalog = new AotPublicTypeCatalog(new[] { descriptor });
Require(catalog.Count == 1, "only explicitly rooted type is visible");
Require(catalog.TryGetPublicType("Example.PublicCounter", out var found) &&
    ReferenceEquals(found, descriptor), "exact public lookup");
Require(!catalog.TryGetPublicType("Example.InternalPrivateState", out _),
    "private actor/state entries must not be discovered");
Require(!catalog.TryGetPublicType("System.String", out _),
    "ambient CLR types must not be reflected into guest lookup");
Require(!catalog.TryGetPublicType("Example.PublicCounter<Runtime>", out _),
    "late generic specialization must fail closed");
fields.Clear();
Require(descriptor.Fields.Count == 1, "caller cannot mutate copied public metadata");
Require(descriptor.Fields[0].Name == "value", "public field retention");
Reject(() => new AotPublicTypeCatalog(new[] { descriptor, descriptor }), "duplicate types");
Reject(() => new AotPublicTypeDescriptor("Example.PublicCounter",
    new[] { new AotPublicField("value", AotPublicFieldKind.Int),
            new AotPublicField("value", AotPublicFieldKind.Bool) },
    Array.Empty<AotPublicMethod>()), "duplicate fields");
Reject(() => new AotPublicTypeDescriptor("Example.PublicCounter",
    new[] { new AotPublicField("actor_private_ptr", (AotPublicFieldKind)99) },
    Array.Empty<AotPublicMethod>()), "unsupported field kind");
foreach (var bad in new[] { "Bad..Name", "Other.Private.", "A<B>",
    "System.String, System.Private", "bad/path", "éclair", "A\nB", "" })
{
    Require(!AotPublicTypeCatalog.IsCanonicalName(bad), "reject symbol: " + bad);
}
Reject(() => new AotPublicTypeDescriptor("A",
    Array.Empty<AotPublicField>(),
    new[] { new AotPublicMethod("f", 2), new AotPublicMethod("f", 2) }),
    "duplicate method arity");
Reject(() => new AotPublicTypeDescriptor("A",
    Array.Empty<AotPublicField>(),
    new[] { new AotPublicMethod("f", 17) }),
    "method arity bounds");
Console.WriteLine("AOT_PUBLIC_METADATA_HOST_ONLY|PASS|NOT_GUEST|NOT_TRIM_VERIFIED");
