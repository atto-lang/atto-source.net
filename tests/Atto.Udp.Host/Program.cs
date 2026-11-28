using System;
using System.IO;
using System.Threading;
using Ores.Net.Runtime.Net;

static class Program
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static UdpLoopbackHost.Packet AwaitPacket(UdpLoopbackHost receiver, int capacity)
    {
        for (int i = 0; i < 1000; i++)
        {
            var packet = receiver.TryReceiveFrom(capacity);
            if (packet is not null) return packet;
            Thread.Sleep(1); // Test process only, never an Atto actor.
        }
        throw new TimeoutException("UDP host loopback receive did not complete");
    }

    private static void AssertTruncated(UdpLoopbackHost receiver)
    {
        for (int i = 0; i < 1000; i++)
        {
            try { if (receiver.TryReceiveFrom(2) is not null) throw new Exception("truncation accepted"); }
            catch (InvalidDataException) { return; }
            Thread.Sleep(1);
        }
        throw new TimeoutException("oversize datagram not observed");
    }

    private static void AssertDeniedSource(UdpLoopbackHost receiver)
    {
        for (int i = 0; i < 1000; i++)
        {
            try
            {
                if (receiver.TryReceiveFrom(4) is not null)
                    throw new Exception("denied source packet escaped authorization");
            }
            catch (UnauthorizedAccessException) { return; }
            Thread.Sleep(1); // Host test harness only.
        }
        throw new TimeoutException("denied source packet was not received");
    }

    private static int Main()
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
        try { first.TrySendTo(second.LocalPort, new byte[] { 1 }); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "peer port deny bypass");
        byte[] oversizedDenied = new byte[UdpLoopbackHost.MaxPayload + 1];
        rejected = false;
        try { first.TrySendTo(second.LocalPort, oversizedDenied); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "denied destination must win before oversized payload inspection");
        deniedPort = -1;
        rejected = false;
        try { first.TrySendTo(second.LocalPort, oversizedDenied); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected, "authorized oversized datagram must be rejected");

        byte[] data = { 1, 0, 255, 42 };
        Require(first.TrySendTo(second.LocalPort, data), "UDP host send would-block");
        data[0] = 99;
        var received = AwaitPacket(second, 4);
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
        Require(first.TrySendTo(second.LocalPort, new byte[]{ 17 }), "denied-source send failed");
        AssertDeniedSource(second);
        deniedSourcePort = -1;
        Require(first.TrySendTo(second.LocalPort, new byte[]{ 18 }), "post-revocation recovery send failed");
        Require(AwaitPacket(second, 4).CopyBytes()[0] == 18,
            "denied source poisoned future datagrams");

        Require(first.TrySendTo(second.LocalPort, Array.Empty<byte>()), "empty UDP send would-block");
        Require(AwaitPacket(second, 4).Length == 0, "empty datagram lost");
        Require(first.TrySendTo(second.LocalPort, new byte[] {1,2,3,4}), "oversize test send failed");
        AssertTruncated(second);
        Require(first.TrySendTo(second.LocalPort, new byte[]{ 6 }), "recovery send failed");
        Require(AwaitPacket(second, 4).CopyBytes()[0] == 6, "truncation poisoned receiver");

        enabled = false;
        rejected = false;
        try { first.TrySendTo(second.LocalPort, new byte[]{ 1 }); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "revoked send permitted");
        rejected = false;
        try { second.TryReceiveFrom(1); }
        catch (UnauthorizedAccessException) { rejected = true; }
        Require(rejected, "revoked receive permitted");
        enabled = true;

        // C# Monitor.TryEnter is reentrant, so policy callback recursion
        // must be independently fenced, not mistaken for a new operation.
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
            Require(reentrant.TrySendTo(second.LocalPort, new byte[]{ 39 }),
                "reentrant guard send must remain usable");
            Require(rejectedReentrancy,
                "nested UDP accessor must not pass the same-thread monitor");
            Require(rejectedRetirement,
                "same-thread authorization callback retired an active UDP descriptor");
            Require(AwaitPacket(second, 4).CopyBytes()[0] == 39,
                "rejected reentrant call altered valid datagram");
        }

        first.Dispose();
        first.Dispose();
        rejected = false;
        try { first.TryReceiveFrom(1); }
        catch (ObjectDisposedException) { rejected = true; }
        Require(rejected, "closed UDP socket accepted I/O");
        Console.WriteLine("Atto .NET UDP host loopback checks passed (not guest/AOT execution)");
        return 0;
    }
}
