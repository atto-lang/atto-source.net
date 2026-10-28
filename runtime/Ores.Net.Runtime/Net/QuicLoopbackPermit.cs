using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security;

namespace Ores.Net.Runtime.Net;

/// <summary>
/// Host-only, single-use QUIC loopback permit for trusted runtime integration.
/// This is NOT a production/public-internet egress policy or guest capability.
/// The trusted issuer must supply the complete DNS answer set for this attempt.
/// Only loopback addresses are authorized, even when the caller is compromised.
/// </summary>
internal sealed class QuicLoopbackPermit
{
    private const int MaxDnsAddresses = 16;
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(10);
    private readonly byte[] _addressBytes;
    private readonly int _port;
    private readonly string _canonicalDnsName;
    private readonly ulong _actorId;
    private readonly ulong _actorGeneration;
    private readonly ulong _resolutionGeneration;
    private readonly long _issuedAtTimestamp;
    private readonly TimeSpan _lifetime;
    private int _consumed;

    private QuicLoopbackPermit(byte[] addressBytes, int port, string canonicalDnsName,
                               ulong actorId, ulong actorGeneration,
                               ulong resolutionGeneration, TimeSpan lifetime)
    {
        _addressBytes = (byte[])addressBytes.Clone();
        _port = port;
        _canonicalDnsName = canonicalDnsName;
        _actorId = actorId;
        _actorGeneration = actorGeneration;
        _resolutionGeneration = resolutionGeneration;
        _issuedAtTimestamp = Stopwatch.GetTimestamp();
        _lifetime = lifetime;
    }

    /// <summary>
    /// Trusted-host-only authorization: no DNS lookup takes place in this
    /// function. The caller MUST obtain the full DNS answer set for this
    /// physical connection. This helper cannot authorize non-loopback IPs.
    /// </summary>
    internal static QuicLoopbackPermit AuthorizeForTrustedHost(
        string canonicalDnsName,
        int port,
        IReadOnlyList<IPAddress> completeDnsAnswer,
        int selectedAddress,
        ulong actorId,
        ulong actorGeneration,
        ulong resolutionGeneration,
        TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(canonicalDnsName) ||
            !string.Equals(canonicalDnsName, canonicalDnsName.Trim(), StringComparison.Ordinal) ||
            !string.Equals(canonicalDnsName, canonicalDnsName.ToLowerInvariant(),
                           StringComparison.Ordinal) ||
            canonicalDnsName.EndsWith('.', StringComparison.Ordinal) ||
            Uri.CheckHostName(canonicalDnsName) != UriHostNameType.Dns)
            throw new SecurityException("QUIC requires a canonical DNS TLS identity");
        if (port is < 1 or > 65535)
            throw new SecurityException("QUIC port is outside 1..65535");
        if (actorId == 0 || actorGeneration == 0 || resolutionGeneration == 0)
            throw new SecurityException("QUIC permit requires a valid runtime actor and DNS generation");
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
            throw new SecurityException("QUIC permit lifetime must be finite and at most 10 seconds");
        if (completeDnsAnswer is null ||
            completeDnsAnswer.Count is < 1 or > MaxDnsAddresses ||
            selectedAddress < 0 || selectedAddress >= completeDnsAnswer.Count)
            throw new SecurityException("QUIC DNS answer set or selected index is invalid");

        // Full-set denial, not one-address allowlisting. Never normalize or
        // silently discard disallowed addresses; this would permit DNS rebinding.
        foreach (var ip in completeDnsAnswer)
        {
            if (ip is null ||
                (ip.AddressFamily != AddressFamily.InterNetwork &&
                 ip.AddressFamily != AddressFamily.InterNetworkV6) ||
                ip.IsIPv4MappedToIPv6 || !IPAddress.IsLoopback(ip) ||
                (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0))
                throw new SecurityException("QUIC DNS answer set contains a denied address");
        }

        return new QuicLoopbackPermit(
            completeDnsAnswer[selectedAddress].GetAddressBytes(),
            port, canonicalDnsName, actorId, actorGeneration,
            resolutionGeneration, lifetime);
    }

    /// <summary>
    /// Consuming a permit is deliberately destructive, even if the owner check
    /// fails or the deadline has elapsed. No second physical connection can
    /// reuse the same DNS authorization.
    /// </summary>
    internal (IPEndPoint Address, string DnsName, TimeSpan Remaining)
        Consume(ulong actorId, ulong actorGeneration)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new SecurityException("QUIC permit already consumed");
        if (actorId != _actorId || actorGeneration != _actorGeneration ||
            _resolutionGeneration == 0)
            throw new SecurityException("QUIC permit actor generation mismatch");
        var remaining = _lifetime - Stopwatch.GetElapsedTime(_issuedAtTimestamp);
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("QUIC permit expired before connect admission");
        return (new IPEndPoint(new IPAddress((byte[])_addressBytes.Clone()), _port),
                _canonicalDnsName, remaining);
    }
}
