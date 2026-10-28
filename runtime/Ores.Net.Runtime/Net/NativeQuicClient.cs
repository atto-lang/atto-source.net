using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security;

namespace Ores.Net.Runtime.Net;

/// <summary>
/// Trusted host-only, pinned-IP QUIC client primitive. Not a guest intrinsic,
/// not an HTTP/3/QPACK implementation, and not an egress policy decision.
/// A trusted caller MUST authorize the entire DNS answer set and the selected
/// IP before constructing a peer. NativeAOT/RID support must be verified.
/// </summary>
internal static class NativeQuicClient
{
    private const int MaxInboundBidiStreams = 16;
    private const int MaxInboundUniStreams = 8;
    private static readonly SslApplicationProtocol Http3Alpn = new("h3");

    internal static bool IsSupported => QuicConnection.IsSupported;

    /// <summary>HTTP/3 ALPN over QUIC/UDP; no HTTP/2 or TCP fallback.</summary>
    internal static ValueTask<QuicConnection> ConnectHttp3Async(
        IPEndPoint pinnedAuthorizedAddress,
        string canonicalDnsName,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(pinnedAuthorizedAddress, canonicalDnsName,
                         Http3Alpn, cancellationToken);
        // A naked IP and hostname are NOT evidence of authorized DNS/egress.
        // No policy-bound opaque permit exists on this backend yet, so the
        // outbound path remains closed even if MsQuic is installed.
        return ValueTask.FromException<QuicConnection>(new SecurityException(
            "QUIC egress policy permit unavailable; no UDP or HTTP/2 fallback"));
    }

    /// <summary>
    /// A narrowly authorized host-only QUIC connection. The supplied opaque,
    /// single-use permit can authorize LOOPBACK destinations only. This is
    /// not a guest feature and must not be widened to arbitrary egress.
    /// </summary>
    internal static async ValueTask<QuicConnection> ConnectHttp3Async(
        QuicLoopbackPermit permit,
        ulong actorId,
        ulong actorGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permit);
        if (!cancellationToken.CanBeCanceled)
            throw new ArgumentException("QUIC owner cancellation token required",
                                        nameof(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        var admitted = permit.Consume(actorId, actorGeneration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(admitted.Remaining);
        // Important: no DNS lookup is performed here. The authorized pinned IP
        // is copied into the provider options; TLS still verifies original SNI.
        return await ConnectUncheckedAfterPolicyAsync(
            admitted.Address, admitted.DnsName, Http3Alpn,
            admitted.Remaining, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// QUIC transport for an explicitly selected application ALPN.
    /// The cancellation token must carry a caller-owned finite deadline.
    /// Cancellation does not prove physical retirement: caller must await
    /// DisposeAsync on the returned connection and all owned streams.
    /// </summary>
    private static async ValueTask<QuicConnection> ConnectUncheckedAfterPolicyAsync(
        IPEndPoint pinnedAuthorizedAddress,
        string canonicalDnsName,
        SslApplicationProtocol applicationProtocol,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(pinnedAuthorizedAddress, canonicalDnsName,
                         applicationProtocol, cancellationToken);

        // .NET QUIC is supported on these OS families only. Keep this guard
        // explicit for the platform-compatibility analyzer (CA1416) as well
        // as for runtime safety; IsSupported alone is not an OS predicate.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows() &&
            !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "QUIC requires supported Linux, Windows, or macOS native runtime");

        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "Native QUIC provider unavailable; HTTP/2 fallback is forbidden");

        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(pinnedAuthorizedAddress.Address,
                                           pinnedAuthorizedAddress.Port),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = canonicalDnsName,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol> { applicationProtocol }
                // Never override chain or hostname validation with a permissive callback.
            },
            HandshakeTimeout = handshakeTimeout,
            IdleTimeout = TimeSpan.FromSeconds(30),
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            MaxInboundBidirectionalStreams = MaxInboundBidiStreams,
            MaxInboundUnidirectionalStreams = MaxInboundUniStreams
        };

        // ConnectAsync negotiates TLS 1.3 and ALPN. The IP is never re-resolved
        // here: TargetHost authenticates the policy-authorized original name.
        var connection = await QuicConnection.ConnectAsync(options, cancellationToken)
            .ConfigureAwait(false);
        // The provider owns the QUIC/TLS protocol state. Revalidate the
        // negotiated result before returning an open native handle. This does
        // not by itself prove path migration is disabled for its lifetime.
        var mismatch =
            connection.RemoteEndPoint is not IPEndPoint remote ||
            !remote.Address.Equals(pinnedAuthorizedAddress.Address) ||
            remote.Port != pinnedAuthorizedAddress.Port ||
            connection.SslProtocol != SslProtocols.Tls13 ||
            !connection.NegotiatedApplicationProtocol.Equals(applicationProtocol) ||
            !string.Equals(connection.TargetHostName, canonicalDnsName,
                           StringComparison.Ordinal);
        if (mismatch || cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new SecurityException(
                "QUIC peer pin, TLS 1.3, SNI, or negotiated ALPN mismatch");
        }
        return connection;
    }

    internal static void ValidateEndpoint(
        IPEndPoint pinnedAuthorizedAddress,
        string canonicalDnsName,
        SslApplicationProtocol applicationProtocol,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pinnedAuthorizedAddress);
        if (pinnedAuthorizedAddress.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(pinnedAuthorizedAddress));
        if (pinnedAuthorizedAddress.Address.AddressFamily is not (
                System.Net.Sockets.AddressFamily.InterNetwork or
                System.Net.Sockets.AddressFamily.InterNetworkV6))
            throw new ArgumentException("QUIC requires an IPv4 or IPv6 pinned address",
                                        nameof(pinnedAuthorizedAddress));
        if (pinnedAuthorizedAddress.Address.IsIPv4MappedToIPv6 ||
            pinnedAuthorizedAddress.Address.IsIPv6Multicast ||
            (pinnedAuthorizedAddress.Address.AddressFamily ==
                 System.Net.Sockets.AddressFamily.InterNetwork &&
             pinnedAuthorizedAddress.Address.GetAddressBytes()[0] >= 224) ||
            pinnedAuthorizedAddress.Address.Equals(IPAddress.Any) ||
            pinnedAuthorizedAddress.Address.Equals(IPAddress.IPv6Any) ||
            pinnedAuthorizedAddress.Address.Equals(IPAddress.None) ||
            pinnedAuthorizedAddress.Address.Equals(IPAddress.IPv6None))
            throw new ArgumentException("QUIC remote address must be a specific IP",
                                        nameof(pinnedAuthorizedAddress));
        if (!QuicLoopbackPermit.IsCanonicalDnsName(canonicalDnsName))
            throw new ArgumentException("QUIC needs a canonical DNS SNI/verification name",
                                        nameof(canonicalDnsName));
        var alpn = applicationProtocol.Protocol.Span;
        if (alpn.Length is < 1 or > 255 || alpn.IndexOf((byte)0) >= 0)
            throw new ArgumentException("QUIC requires a valid explicit ALPN",
                                        nameof(applicationProtocol));
        if (!cancellationToken.CanBeCanceled)
            throw new ArgumentException("QUIC connect requires a finite-deadline cancellation token",
                                        nameof(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
    }
}
