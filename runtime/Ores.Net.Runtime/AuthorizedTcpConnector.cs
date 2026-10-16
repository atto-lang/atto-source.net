using System.Net;
using System.Net.Sockets;

namespace Ores.Net.Runtime;

/// <summary>
/// Trusted host-only outbound TCP boundary, compiled into Native AOT.
/// Internal visibility prevents guest/generated assemblies from directly
/// obtaining raw Socket objects through this helper. No guest
/// language socket API is exposed by the current scalar backend. Enforces the
/// requested endpoint and selected DNS IPv4 address on the EXACT socket used
/// for connect, never a second hostname resolution. Unsupported IPv6 denies.
/// </summary>
internal static class AuthorizedTcpConnector
{
    public static Socket Connect(ScopedPermissionPolicy policy, string host, int port,
        TimeSpan? connectTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (host is null || port is < 1 or > 65535)
            throw new ArgumentException("invalid requested TCP endpoint");
        var requested = host + ":" + port;
        if (!policy.Allows(ScopedPermissionPolicy.Kind.Net, requested))
            throw new UnauthorizedAccessException("requested TCP endpoint denied");

        // Resolver is called only after the requested hostname passes admission.
        // The only address used for connect is the same numeric IP authorized
        // by AllowsNetworkConnection; never connect by hostname or re-resolve.
        var resolved = Dns.GetHostAddresses(host);
        if (resolved.Length > 64)
            throw new UnauthorizedAccessException("too many DNS endpoints");
        var limit = connectTimeout ?? TimeSpan.FromSeconds(15);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        foreach (var address in resolved)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            var selected = address.ToString() + ":" + port;
            if (!policy.AllowsNetworkConnection(requested, selected))
                continue;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var deadline = new CancellationTokenSource(limit);
                socket.ConnectAsync(new IPEndPoint(address, port), deadline.Token)
                    .AsTask().GetAwaiter().GetResult();
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw; // No retry with unchecked or independently resolved address.
            }
        }
        throw new UnauthorizedAccessException("no resolved TCP address authorized");
    }
}
