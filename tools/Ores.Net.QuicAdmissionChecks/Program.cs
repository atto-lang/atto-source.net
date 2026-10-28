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
try
{
    NativeQuicClient.ConnectHttp3Async(endpoint, "origin.example", token)
        .AsTask().GetAwaiter().GetResult();
    throw new Exception("Missing egress permit bypassed");
}
catch (SecurityException) { }


static void MustDeny(Action action, string label)
{
    try { action(); throw new InvalidOperationException("Accepted invalid QUIC permit: " + label); }
    catch (SecurityException) { }
}

static QuicLoopbackPermit Issue(ulong actorId = 42, ulong actorGeneration = 7,
                                TimeSpan? lifetime = null)
    => QuicLoopbackPermit.AuthorizeForTrustedHost(
        "origin.example", 443, new[] { IPAddress.Loopback, IPAddress.IPv6Loopback },
        0, actorId, actorGeneration, 9,
        lifetime ?? TimeSpan.FromSeconds(2));

MustDeny(() => QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, new[] { IPAddress.Loopback, IPAddress.Parse("8.8.8.8") },
    0, 42, 7, 9, TimeSpan.FromSeconds(2)), "mixed trusted and public DNS set");
MustDeny(() => QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, new[] { IPAddress.Loopback },
    1, 42, 7, 9, TimeSpan.FromSeconds(2)), "out-of-bounds pinned address");
MustDeny(() => QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, new[] { IPAddress.Loopback },
    0, 0, 7, 9, TimeSpan.FromSeconds(2)), "zero actor identity");
MustDeny(() => QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, new[] { IPAddress.Loopback },
    0, 42, 7, 9, TimeSpan.FromSeconds(20)), "unbounded permit TTL");
MustDeny(() => QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, new[] { IPAddress.Loopback },
    0, 42, 7, 0, TimeSpan.FromSeconds(2)), "zero resolution generation");

// No Unicode/raw IDN, label overrun, empty DNS labels or hyphen-edge SNI.
foreach (var invalidName in new[] { "BÜCHER.example", "bücher.example",
    "origin..example", "-bad.example", "bad-.example",
    new string('a', 64) + ".example", "origin.example.", "BAD.example",
    "bad_name.example", "a b.example", "127.0.0.1" })
{
    if (QuicLoopbackPermit.IsCanonicalDnsName(invalidName))
        throw new InvalidOperationException("Invalid canonical SNI accepted: " + invalidName);
    MustDeny(() => QuicLoopbackPermit.AuthorizeForTrustedHost(
        invalidName, 443, new[] { IPAddress.Loopback }, 0, 42, 7, 9,
        TimeSpan.FromSeconds(2)), "invalid permit SNI " + invalidName);
    MustReject(() => NativeQuicClient.ValidateEndpoint(
        endpoint, invalidName, h3, token), "invalid native client SNI " + invalidName);
}
if (!QuicLoopbackPermit.IsCanonicalDnsName("xn--bcher-kva.example"))
    throw new InvalidOperationException("Canonical ASCII IDN A-label rejected");

// A custom collection can lie between enumeration and index lookup. The
// issuer must reject the actual indexed value, not trust a previous iteration.
MustDeny(() => QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, new SpoofedDnsSet(), 0, 42, 7, 9,
    TimeSpan.FromSeconds(2)), "mutable DNS enumeration/index TOCTOU");

// Reading a selected IP twice would be a TOCTOU even with index-based
// validation. Once verified, the selected pin must come from the snapshot.
var reads = new ChangingDnsSet();
var noAlias = QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, reads, 0, 42, 7, 9,
    TimeSpan.FromSeconds(2));
if (reads.ReadCount != 1 ||
    !noAlias.Consume(42, 7).Address.Address.Equals(IPAddress.Loopback))
    throw new InvalidOperationException("Verified DNS pin was read more than once");

// Parallel consumption must have exactly one winner even if calls race.
var parallel = Issue(lifetime: TimeSpan.FromSeconds(8));
var successfulConsumes = 0;
var deniedConsumes = 0;
Parallel.For(0, 128, _ =>
{
    try
    {
        parallel.Consume(42, 7);
        Interlocked.Increment(ref successfulConsumes);
    }
    catch (SecurityException) { Interlocked.Increment(ref deniedConsumes); }
});
if (successfulConsumes != 1 || deniedConsumes != 127)
    throw new InvalidOperationException("QUIC permit replay race: " +
        successfulConsumes + " successes, " + deniedConsumes + " denials");

var issued = Issue();
MustDeny(() => issued.Consume(42, 8), "wrong actor generation");
MustDeny(() => issued.Consume(42, 7), "wrong generation must consume permit");

var consumed = Issue().Consume(42, 7);
if (!consumed.Address.Address.Equals(IPAddress.Loopback) ||
    consumed.Address.Port != 443 || consumed.DnsName != "origin.example" ||
    consumed.Remaining <= TimeSpan.Zero)
    throw new InvalidOperationException("Pinned loopback identity was lost");

var once = Issue();
var first = once.Consume(42, 7);
MustDeny(() => once.Consume(42, 7), "same permit used twice");
first.Address.Port = 444; // mutation of returned endpoint cannot alter the issuer.

var mutableAnswers = new[] { IPAddress.Loopback };
var copied = QuicLoopbackPermit.AuthorizeForTrustedHost(
    "origin.example", 443, mutableAnswers, 0, 42, 7, 9, TimeSpan.FromSeconds(2));
mutableAnswers[0] = IPAddress.Parse("8.8.8.8");
if (!copied.Consume(42, 7).Address.Address.Equals(IPAddress.Loopback))
    throw new InvalidOperationException("Permit retained mutable DNS answer alias");

var expiry = Issue(lifetime: TimeSpan.FromMilliseconds(1));
Thread.Sleep(15);
try
{
    expiry.Consume(42, 7);
    throw new InvalidOperationException("Expired QUIC permit was admitted");
}
catch (TimeoutException) { }

using var cancelledPermitConnect = new CancellationTokenSource();
cancelledPermitConnect.Cancel();
try
{
    NativeQuicClient.ConnectHttp3Async(
        Issue(), 42, 7, cancelledPermitConnect.Token).AsTask().GetAwaiter().GetResult();
    throw new InvalidOperationException("Cancelled permit connect admitted");
}
catch (OperationCanceledException) { }

// No network is opened in this admission test. It is not QUIC/HTTP3 interop.
Console.WriteLine("QUIC host admission and single-use loopback permit checks passed; interop not asserted");

internal sealed class SpoofedDnsSet : IReadOnlyList<IPAddress>
{
    public int Count => 1;
    public IPAddress this[int i] => IPAddress.Parse("8.8.8.8");
    public IEnumerator<IPAddress> GetEnumerator()
    {
        yield return IPAddress.Loopback;
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}

internal sealed class ChangingDnsSet : IReadOnlyList<IPAddress>
{
    public int Count => 1;
    public int ReadCount { get; private set; }
    public IPAddress this[int i]
    {
        get
        {
            ++ReadCount;
            return ReadCount == 1 ? IPAddress.Loopback : IPAddress.Parse("8.8.8.8");
        }
    }
    public IEnumerator<IPAddress> GetEnumerator()
    {
        yield return IPAddress.Loopback;
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}
