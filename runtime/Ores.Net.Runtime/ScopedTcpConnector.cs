using System.Net;
using System.Net.Sockets;

namespace Ores.Net.Runtime;

/// <summary>
/// Trusted host socket adapter. DNS is performed once; each attempted address
/// is independently authorized and the same numeric IPEndPoint is connected.
/// Guests must never supply or replace the host-owned policy instance.
/// Internal visibility restricts raw Socket access to trusted host/test assemblies.
/// This is not yet linked into the bootstrap scalar-only guest compiler.
/// </summary>
internal static class ScopedTcpConnector
{
    public static IPEndPoint AuthorizeSelectedAddress(
        ScopedPermissionPolicy policy, string requested, IPAddress selected, int port)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(selected);
        if (port is < 1 or > 65535 ||
            selected.AddressFamily != AddressFamily.InterNetwork)
            throw new UnauthorizedAccessException("selected socket address is not supported IPv4");

        // IPAddress.ToString emits a canonical dotted quad for InterNetwork.
        // The adapter has already resolved the name: no second DNS lookup.
        string numeric = selected + ":" + port.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        if (!policy.AllowsNetworkConnection(requested, numeric))
            throw new UnauthorizedAccessException("selected socket address denied by network policy");
        return new IPEndPoint(selected, port);
    }

    public static async Task<Socket> ConnectAsync(
        ScopedPermissionPolicy policy, string host, int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            throw new ArgumentException("invalid network connection target");
        string requested = host.ToLowerInvariant() + ":" +
            port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!policy.Allows(ScopedPermissionPolicy.Kind.Net, requested))
            throw new UnauthorizedAccessException("requested network endpoint denied");

        // Resolve only after the hostname policy has been checked. Never call
        // Socket.ConnectAsync(host, port), which could resolve a different IP.
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Length > 64)
            throw new UnauthorizedAccessException("DNS answer set exceeds security limit");

        SocketException? lastSocketError = null;
        bool anyAuthorized = false;
        foreach (IPAddress candidate in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IPEndPoint pinned;
            try
            {
                pinned = AuthorizeSelectedAddress(policy, requested, candidate, port);
            }
            catch (UnauthorizedAccessException)
            {
                // IPv6, denied IPs, and port mismatches are never connected.
                continue;
            }

            anyAuthorized = true;
            // Raw TCP sockets have no implicit HTTP/SOCKS proxy fallback.
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(pinned, cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException failure)
            {
                socket.Dispose();
                lastSocketError = failure;
                // A retry is allowed only after a new selected-IP check.
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        if (!anyAuthorized)
            throw new UnauthorizedAccessException("no resolved network address is authorized");
        throw new IOException("all authorized socket connection attempts failed", lastSocketError);
    }
}
