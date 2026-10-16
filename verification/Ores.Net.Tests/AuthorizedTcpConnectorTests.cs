using System.Net;
using System.Net.Sockets;
using Ores.Net.Runtime;

internal static class AuthorizedTcpConnectorTests
{
    private static ScopedPermissionPolicy Policy(params ScopedPermissionPolicy.Rule[] rules) =>
        ScopedPermissionPolicy.FromHost(
            new[] { ScopedPermissionPolicy.Kind.Net }, rules);

    private static void Denied(Action action, string label)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new Exception("Unchecked TCP admission: " + label);
    }

    public static void Run()
    {
        Denied(() => AuthorizedTcpConnector.Connect(
            ScopedPermissionPolicy.DefaultDeny(), "127.0.0.1", 443),
            "default deny before DNS");

        var broad = Policy(new(ScopedPermissionPolicy.Kind.Net, "*"));
        var blocked = Policy(new(ScopedPermissionPolicy.Kind.Net, "*"),
            new(ScopedPermissionPolicy.Kind.Net, "127.0.0.1", true));
        Denied(() => AuthorizedTcpConnector.Connect(blocked, "127.0.0.1", 443),
            "numeric selected IPv4 denied");
        Denied(() => AuthorizedTcpConnector.Connect(blocked, "localhost", 443),
            "DNS hostname cannot override resolved IPv4 denial");

        foreach (var alias in new[] { "127.1", "0x7f.0.0.1", "127.0.0.01" })
            Denied(() => AuthorizedTcpConnector.Connect(broad, alias, 443),
                "ambiguous IPv4 alias: " + alias);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var granted = Policy(new(ScopedPermissionPolicy.Kind.Net,
                "127.0.0.1:" + port));
            using var socket = AuthorizedTcpConnector.Connect(granted,
                "127.0.0.1", port, TimeSpan.FromSeconds(3));
            using var accepted = listener.AcceptSocket();
            if (!socket.Connected || !accepted.Connected ||
                !Equals(socket.RemoteEndPoint, new IPEndPoint(IPAddress.Loopback, port)))
                throw new Exception("TCP did not connect to the checked numeric IPv4 endpoint");

            var deniedPort = Policy(new(ScopedPermissionPolicy.Kind.Net, "*"),
                new(ScopedPermissionPolicy.Kind.Net, "127.0.0.1:" + port, true));
            Denied(() => AuthorizedTcpConnector.Connect(
                deniedPort, "127.0.0.1", port), "selected port deny");
        }
        finally
        {
            listener.Stop();
        }
        Console.WriteLine("NET_PINNED_TCP_HOST_BOUNDARY_PASS");
    }
}
