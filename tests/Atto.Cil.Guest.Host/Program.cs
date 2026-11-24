// Fixed NativeAOT entry bridge; the Atto guest logic is ONLY in generated CIL.
internal static class Program
{
    private static int Main() => checked((int)AttoGenerated.Functions.main());
}
