using Ores.Net.Runtime;

internal static class NativeSourceIdentityV1SelfTest
{
    private static int checks;
    private static void Check(bool condition, string why)
    {
        ++checks;
        if (!condition) throw new Exception("NativeAOT source IDs: " + why);
    }
    private static void Reject<TException>(Action work, string why) where TException : Exception
    {
        ++checks;
        try { work(); }
        catch (TException) { return; }
        throw new Exception("Expected source-ID rejection: " + why);
    }
    internal static void Run()
    {
        var routine = NativeSourceIdentityV1.Parse(
            "rid_d5f8e146ba97430dac6bf17430477e2a",
            NativeSourceIdentityV1.Kind.Routine);
        var site = NativeSourceIdentityV1.Parse(
            "sid_652e1632e7464fdb936fc40fe24f1c7b",
            NativeSourceIdentityV1.Kind.Site);
        var another = NativeSourceIdentityV1.Parse(
            "sid_738b13448f724fc5905841e9037b410a",
            NativeSourceIdentityV1.Kind.Site);
        Check(routine.SourceKind == NativeSourceIdentityV1.Kind.Routine, "typed routine identity");
        Check(site.SourceKind == NativeSourceIdentityV1.Kind.Site, "typed site identity");

        var frozen = NativeSourceIdentityV1.FreezeValidated(new [] {
            new NativeSourceIdentityV1.Site(routine, site, 7),
            new NativeSourceIdentityV1.Site(routine, another, 8)
        });
        Check(frozen.Count == 2 && frozen[0].DescriptorIndex == 7, "immutable published table");
        Reject<NotSupportedException>(() =>
            ((IList<NativeSourceIdentityV1.Site>)frozen).Add(
                new NativeSourceIdentityV1.Site(routine, another, 9)), "table is immutable");
        Reject<FormatException>(() => NativeSourceIdentityV1.Parse(
            "sid_d5f8e146ba97430dac6bf17430477e2a",
            NativeSourceIdentityV1.Kind.Routine), "wrong source kind");
        Reject<FormatException>(() => NativeSourceIdentityV1.Parse(
            "rid_D5f8e146ba97430dac6bf17430477e2a",
            NativeSourceIdentityV1.Kind.Routine), "mixed uppercase");
        Reject<FormatException>(() => NativeSourceIdentityV1.Parse(
            "sid_652e1632e7464fdb936fc40fe24f1c7b",
            (NativeSourceIdentityV1.Kind)99), "unknown enum source kind");
        Reject<FormatException>(() => NativeSourceIdentityV1.Parse(
            "rid_00000000000000000000000000000000",
            NativeSourceIdentityV1.Kind.Routine), "all zero");
        Reject<FormatException>(() => NativeSourceIdentityV1.Parse(
            "4bf92f3577b34da6a3ce929d0e0e4736",
            NativeSourceIdentityV1.Kind.Routine), "runtime trace ID isn't source ID");
        Reject<ArgumentException>(() =>
            NativeSourceIdentityV1.FreezeValidated(new [] {
                new NativeSourceIdentityV1.Site(routine, site, 1),
                new NativeSourceIdentityV1.Site(routine, site, 2)
            }), "duplicate site ID");
        Reject<ArgumentException>(() =>
            NativeSourceIdentityV1.FreezeValidated(new [] {
                new NativeSourceIdentityV1.Site(routine, site, 1),
                new NativeSourceIdentityV1.Site(routine, another, 1)
            }), "duplicate compact descriptor");
        Reject<ArgumentException>(() =>
            NativeSourceIdentityV1.FreezeValidated(new [] {
                new NativeSourceIdentityV1.Site(site, routine, 1)
            }), "routine/site kinds cannot be reversed");
        Console.WriteLine("PASS NET_NATIVE_SOURCE_ID_HOST_METADATA_V1:" + checks);
    }
}
