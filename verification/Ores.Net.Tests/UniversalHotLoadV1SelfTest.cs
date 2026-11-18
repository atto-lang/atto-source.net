using System;
using Ores.Net.Runtime;

internal static class UniversalHotLoadV1SelfTest
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("UHL-1: " + message);
    }

    public static void Run()
    {
        var api = UniversalHotLoadV1.QueryCapabilities(
            UniversalHotLoadV1.AbiVersion, out var caps);
        Require(api == UniversalHotLoadV1.Status.Ok, "v1 must negotiate");
        Require(caps.AbiVersion == 1, "returned ABI version");
        Require(caps.Supported == UniversalHotLoadV1.Features.None,
            "NativeAOT cannot advertise hot loading before source/ETS integration");
        Require(caps.MaxManifestBytes == 0 &&
                caps.MaxCheckpointBytes == 0 && caps.MaxLiveGenerations == 0,
            "no staging quota may be advertised for unsupported operations");

        foreach (uint wrong in new uint[] { 0, 2, uint.MaxValue })
        {
            var refused = UniversalHotLoadV1.QueryCapabilities(wrong, out var returned);
            Require(refused == UniversalHotLoadV1.Status.BadAbi,
                "unrecognized v1 ABI cannot negotiate");
            Require(returned == default, "rejected version leaks no capabilities");
        }

        var unsupported = UniversalHotLoadV1.Status.Unsupported;
        byte[] suspiciousManifest = new byte[] { 0, 1, 2, 3 };
        var initial = (byte[])suspiciousManifest.Clone();
        for (int i = 0; i < 10000; i++)
        {
            Require(UniversalHotLoadV1.Stage(suspiciousManifest) == unsupported,
                "native AOT actor staging must be unsupported");
            Require(UniversalHotLoadV1.Prepare(7) == unsupported,
                "no supervisor release plan support");
            Require(UniversalHotLoadV1.Freeze(7, 1) == unsupported,
                "no actor ingress freeze support");
            Require(UniversalHotLoadV1.Quiesce(7) == unsupported,
                "no actor/task/FFI/ETS drain proof support");
            Require(UniversalHotLoadV1.Commit(7, 1) == unsupported,
                "no route+code+child+ETS publication");
            Require(UniversalHotLoadV1.Abort(7) == unsupported,
                "no transaction was admitted");
            Require(UniversalHotLoadV1.Retire(7) == unsupported,
                "no actual code generation unload support");
            Require(UniversalHotLoadV1.AcknowledgeEtsHeir(7, 8, 9) == unsupported,
                "no ETS owner-death receipt support");
        }

        Require(suspiciousManifest.AsSpan().SequenceEqual(initial),
            "rejected manifest must not be rewritten");
        Require(UniversalHotLoadV1.QueryCapabilities(1, out caps) ==
                UniversalHotLoadV1.Status.Ok &&
                caps.Supported == UniversalHotLoadV1.Features.None,
            "10000 denied transactions cannot change capability bits");
        Console.WriteLine(
            "PASS .NET UHL-1 host-only NativeAOT capability gate (10000 denied rounds)");
    }
}
