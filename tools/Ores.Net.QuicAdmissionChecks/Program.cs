using System.Net;
using System.Net.Security;
using System.Security;
using Ores.Net.Runtime.Net;

static void MustReject(Action action, string label)
{
    try { action(); throw new Exception("Accepted invalid QUIC admission: " + label); }
    catch (ArgumentException) { }
}

using var liveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var token = liveDeadline.Token;
var h3 = new SslApplicationProtocol("h3");
var endpoint = new IPEndPoint(IPAddress.Loopback, 443);
NativeQuicClient.ValidateEndpoint(endpoint, "origin.example", h3, token);

MustReject(() => NativeQuicClient.ValidateEndpoint(endpoint, "origin.example",
    h3, CancellationToken.None), "unbounded token");
MustReject(() => NativeQuicClient.ValidateEndpoint(endpoint, "wrong.example.",
    h3, token), "noncanonical SNI");
MustReject(() => NativeQuicClient.ValidateEndpoint(endpoint, "WRONG.EXAMPLE",
    h3, token), "uppercase SNI");
MustReject(() => NativeQuicClient.ValidateEndpoint(new IPEndPoint(IPAddress.Any, 443),
    "origin.example", h3, token), "wildcard remote");
MustReject(() => NativeQuicClient.ValidateEndpoint(endpoint, "origin.example",
    default, token), "no ALPN");
MustReject(() => NativeQuicClient.ValidateEndpoint(
    new IPEndPoint(IPAddress.Parse("239.2.3.4"), 443),
    "origin.example", h3, token), "IPv4 multicast");
MustReject(() => NativeQuicClient.ValidateEndpoint(
    new IPEndPoint(IPAddress.Parse("ff02::1"), 443),
    "origin.example", h3, token), "IPv6 multicast");

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    NativeQuicClient.ValidateEndpoint(endpoint, "origin.example", h3, cancelled.Token);
    throw new Exception("Accepted a pre-cancelled QUIC connection");
}
catch (OperationCanceledException) { }

// No network is opened: naked inputs MUST NOT bypass missing egress permits.
// The QUIC entrypoint is annotated for Linux/Windows/macOS; guard its caller
// too, so CA1416 remains a warnings-as-errors gate on all target RIDs.
if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() ||
    OperatingSystem.IsMacOS())
{
    try
    {
        NativeQuicClient.ConnectHttp3Async(endpoint, "origin.example", token)
            .AsTask().GetAwaiter().GetResult();
        throw new Exception("Missing egress permit bypassed");
    }
    catch (SecurityException) { }
}
else if (NativeQuicClient.IsSupported)
{
    throw new Exception("Unsupported OS unexpectedly advertises native QUIC");
}

// No network is opened in this admission test. It is not QUIC/HTTP3 interop.
Console.WriteLine("QUIC host admission negative checks passed; interop not asserted");
