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
    /// A strictly canonical, lowercase ASCII DNS identity. Reject Unicode,
    /// bad label boundaries and overlength labels before TLS SNI validation;
    /// IDN callers must supply an already validated A-label (punycode).
    /// </summary>
    internal static bool IsCanonicalDnsName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 253 ||
            name[0] == '.' || name[^1] == '.') return false;
        var labelLength = 0;
        char previous = '.';
        foreach (var ch in name)
        {
            var letter = ch is >= 'a' and <= 'z';
            var digit = ch is >= '0' and <= '9';
            if (ch == '.')
            {
                if (labelLength == 0 || previous == '-') return false;
                labelLength = 0;
            }
            else
            {
                if (!letter && !digit && ch != '-') return false;
                if (labelLength == 0 && ch == '-') return false;
                if (++labelLength > 63) return false;
            }
            previous = ch;
        }
        // A numeric address literal is not a DNS SNI identity.
        return labelLength != 0 && previous != '-' &&
            !IPAddress.TryParse(name, out _);
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
        if (!IsCanonicalDnsName(canonicalDnsName))
            throw new SecurityException("QUIC requires a canonical DNS TLS identity");
        if (port is < 1 or > 65535)
            throw new SecurityException("QUIC port is outside 1..65535");
        if (actorId == 0 || actorGeneration == 0 || resolutionGeneration == 0)
            throw new SecurityException("QUIC permit requires a valid runtime actor and DNS generation");
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
            throw new SecurityException("QUIC permit lifetime must be finite and at most 10 seconds");
        // Capture a bounded snapshot. An IReadOnlyList need not be immutable;
        // never validate one enumeration and later index into the caller's
        // potentially mutated or adversarial DNS data.
        var count = completeDnsAnswer?.Count ?? 0;
        if (count is < 1 or > MaxDnsAddresses ||
            selectedAddress < 0 || selectedAddress >= count)
            throw new SecurityException("QUIC DNS answer set or selected index is invalid");

        var approvedAddressBytes = new byte[count][];
        for (int i = 0; i < count; ++i)
        {
            IPAddress ip;
            try
            {
                ip = completeDnsAnswer![i];
            }
            catch (ArgumentOutOfRangeException error)
            {
                throw new SecurityException("QUIC DNS answer changed during admission", error);
            }
            if (ip is null ||
                (ip.AddressFamily != AddressFamily.InterNetwork &&
                 ip.AddressFamily != AddressFamily.InterNetworkV6) ||
                ip.IsIPv4MappedToIPv6 || !IPAddress.IsLoopback(ip) ||
                (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0))
                throw new SecurityException("QUIC DNS answer set contains a denied address");

            // Capture exactly the bytes we verified; never fetch selectedAddress
            // from the external collection after validation is complete.
            approvedAddressBytes[i] = ip.GetAddressBytes();
        }

        return new QuicLoopbackPermit(
            approvedAddressBytes[selectedAddress],
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
