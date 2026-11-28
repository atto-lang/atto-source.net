using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ores.Net.Runtime.Net;

static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<UdpLoopbackHost.Packet> AwaitPacket(UdpLoopbackHost receiver, int capacity)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await receiver.ReceiveFromAsync(capacity, deadline.Token);
    }

    private static async Task AssertTruncated(UdpLoopbackHost receiver)
    {
        try { _ = await AwaitPacket(receiver, 2); }
        catch (InvalidDataException) { return; }
        throw new Exception("truncation accepted");
    }

    private static async Task AssertDeniedSource(UdpLoopbackHost receiver)
    {
        try { _ = await AwaitPacket(receiver, 4); }
        catch (UnauthorizedAccessException) { return; }
        throw new Exception("denied source packet escaped authorization");
    }

    private static async Task Send(UdpLoopbackHost sender, int port, byte[] bytes)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sender.SendToAsync(port, bytes, deadline.Token);
    }

    private static async Task<int> Main()
    {
        bool enabled = true;
        int deniedPort = -1;
        int deniedSourcePort = -1;
        UdpLoopbackHost.Authorize gate = (port, op) =>
        {
            if (!enabled
                || (op == UdpLoopbackHost.Action.Send && port == deniedPort)
                || (op == UdpLoopbackHost.Action.Source && port == deniedSourcePort))
                throw new UnauthorizedAccessException("UDP permission denied");
        };
        enabled = false;
        bool rejected = false;
        try { using var denied = UdpLoopbackHost.Bind(0, gate); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "bind must require trusted permission");
        enabled = true;

        using var first = UdpLoopbackHost.Bind(0, gate);
        using var second = UdpLoopbackHost.Bind(0, gate);
        Require(first.LocalPort > 0 && second.LocalPort > 0 && first.LocalPort != second.LocalPort,
            "invalid ephemeral bind");

        deniedPort = second.LocalPort;
        rejected = false;
        try { await Send(first, second.LocalPort, new byte[] { 1 }); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "peer port deny bypass");
        byte[] oversizedDenied = new byte[UdpLoopbackHost.MaxPayload + 1];
        rejected = false;
        try { await Send(first, second.LocalPort, oversizedDenied); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "denied destination must win before oversized payload inspection");
        deniedPort = -1;
        rejected = false;
        try { await Send(first, second.LocalPort, oversizedDenied); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected, "authorized oversized datagram must be rejected");

        byte[] data = { 1, 0, 255, 42 };
        await Send(first, second.LocalPort, data);
        data[0] = 99;
        var received = await AwaitPacket(second, 4);
        Require(received.Length == 4 && received.SourcePort == first.LocalPort, "UDP peer/payload lost");
        byte[] copy = received.CopyBytes();
        Require(copy[0] == 1 && copy[2] == 255, "UDP bytes modified");
        copy[0] = 77;
        Require(received.CopyBytes()[0] == 1, "UDP ownership alias escaped");

        // An accepted bind grant is not permission to observe the selected
        // ephemeral port forever. Assert the 0-port grant is rechecked.
        enabled = false;
        rejected = false;
        try { _ = first.LocalPort; }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "revoked local_port permitted");
        enabled = true;

        // Receive permission is scoped to the actual sender, not just the
        // bound local socket. A rejected source must not publish packet bytes.
        deniedSourcePort = first.LocalPort;
        await Send(first, second.LocalPort, new byte[]{ 17 });
        await AssertDeniedSource(second);
        deniedSourcePort = -1;
        await Send(first, second.LocalPort, new byte[]{ 18 });
        Require((await AwaitPacket(second, 4)).CopyBytes()[0] == 18,
            "denied source poisoned future datagrams");

        await Send(first, second.LocalPort, Array.Empty<byte>());
        Require((await AwaitPacket(second, 4)).Length == 0, "empty datagram lost");
        await Send(first, second.LocalPort, new byte[] {1,2,3,4});
        await AssertTruncated(second);
        await Send(first, second.LocalPort, new byte[]{ 6 });
        Require((await AwaitPacket(second, 4)).CopyBytes()[0] == 6, "truncation poisoned receiver");

        enabled = false;
        rejected = false;
        try { await Send(first, second.LocalPort, new byte[]{ 1 }); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "revoked send permitted");
        rejected = false;
        try { _ = await AwaitPacket(second, 1); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "revoked receive permitted");
        enabled = true;

        // Recursive trusted policy callbacks must not reenter or retire the
        // active logical descriptor lease.
        UdpLoopbackHost? recursiveSocket = null;
        bool rejectedReentrancy = false;
        bool rejectedRetirement = false;
        UdpLoopbackHost.Authorize reentrantGate = (port, action) =>
        {
            if (action == UdpLoopbackHost.Action.Send)
            {
                try { _ = recursiveSocket!.LocalPort; }
                catch (InvalidOperationException) { rejectedReentrancy = true; }
                try { recursiveSocket!.Dispose(); }
                catch (InvalidOperationException) { rejectedRetirement = true; }
            }
        };
        using (var reentrant = UdpLoopbackHost.Bind(0, reentrantGate))
        {
            recursiveSocket = reentrant;
            await Send(reentrant, second.LocalPort, new byte[]{ 39 });
            Require(rejectedReentrancy,
                "nested UDP accessor must not acquire an active lease");
            Require(rejectedRetirement,
                "same-thread authorization callback retired an active UDP descriptor");
            Require((await AwaitPacket(second, 4)).CopyBytes()[0] == 39,
                "rejected reentrant call altered valid datagram");
        }

        // The empty receive must remain pending without holding a carrier
        // thread or a monitor. Retirement and other ingress must fail until
        // cancellation physically settles the operation.
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var pending = second.ReceiveFromAsync(4, cancel.Token);
            Require(!pending.IsCompleted, "empty receive did not suspend");
            rejected = false;
            try { second.Dispose(); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "retirement during suspended receive was permitted");
            rejected = false;
            try { _ = second.LocalPort; }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "concurrent ingress during suspended receive was permitted");
            cancel.Cancel();
            rejected = false;
            try { _ = await pending; }
            catch (OperationCanceledException) { rejected = true; }
            Require(rejected, "pending receive did not settle cancellation");
        }
        await Send(first, second.LocalPort, new byte[]{ 41 });
        Require((await AwaitPacket(second, 4)).CopyBytes()[0] == 41,
            "cancelled receive poisoned descriptor or consumed later datagram");

        // Revoke a receiver-local permission after the socket await starts.
        bool localEnabled = true;
        using (var receiver = UdpLoopbackHost.Bind(0, (_, action) =>
        {
            if (!Volatile.Read(ref localEnabled) && action == UdpLoopbackHost.Action.Local)
                throw new UnauthorizedAccessException("revoked receiver");
        }))
        {
            int port = receiver.LocalPort;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = receiver.ReceiveFromAsync(4, deadline.Token);
            Require(!pending.IsCompleted, "revocation test receive did not suspend");
            Volatile.Write(ref localEnabled, false);
            await Send(first, port, new byte[]{ 42 });
            rejected = false;
            try { _ = await pending; }
            catch (UnauthorizedAccessException) { rejected = true; }
            Require(rejected, "revoked receiver published a completed packet");
            Volatile.Write(ref localEnabled, true);
            await Send(first, port, new byte[]{ 43 });
            Require((await AwaitPacket(receiver, 4)).CopyBytes()[0] == 43,
                "revoked receive poisoned future datagrams");
        }

        first.Dispose();
        first.Dispose();
        rejected = false;
        try { _ = await AwaitPacket(first, 1); }
        catch (ObjectDisposedException) { rejected = true; }
        Require(rejected, "closed UDP socket accepted I/O");
        Console.WriteLine("Atto .NET UDP host loopback checks passed (not guest/AOT execution)");
        return 0;
    }
}
