using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ores.Net.Runtime.Net;

/// <summary>
/// Trusted NativeAOT-compatible UDP kernel, not an Atto guest import.
/// Restricted to numeric IPv4 loopback until a policy-bound socket ABI and
/// actor-owned physical retirement are implemented. No DNS or fallback.
/// The host must supply a trusted authority callback; never pass guest code.
/// </summary>
internal sealed class UdpLoopbackHost : IDisposable
{
    internal enum Action { Bind, Local, Send, Source }
    internal delegate void Authorize(int port, Action action);

    internal sealed class Packet
    {
        private readonly byte[] _data;
        internal Packet(byte[] data, int peerPort)
        {
            _data = (byte[])data.Clone();
            SourcePort = peerPort;
        }
        internal int SourcePort { get; }
        internal byte[] CopyBytes() => (byte[])_data.Clone();
        internal int Length => _data.Length;
    }

    internal const int MaxPayload = 65507;
    private readonly Socket _socket;
    private readonly Authorize _authorize;
    private readonly int _requestedPort;
    // One logical operation owns the descriptor across suspension. This is
    // not a monitor held across await, nor a queue of pending callers.
    private const int Idle = 0, Active = 1, Retiring = 2, Closed = 3;
    private int _lifecycle;

    private UdpLoopbackHost(Socket socket, int requestedPort, Authorize authorize)
    {
        _socket = socket;
        _requestedPort = requestedPort;
        _authorize = authorize;
    }

    internal static UdpLoopbackHost Bind(int port, Authorize authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        authorize(port, Action.Bind); // Must occur before opening a descriptor.
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false
        };
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return new UdpLoopbackHost(socket, port, authorize);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal int LocalPort
    {
        get
        {
            // Ephemeral-port observation is itself a scoped NETWORK action.
            // Authorize the original bind endpoint (port zero remains zero)
            // before reading socket metadata. A guest actor must not park
            // behind another operation's permission callback.
            Enter();
            try
            {
                _authorize(_requestedPort, Action.Local);
                return ((IPEndPoint)_socket.LocalEndPoint!).Port;
            }
            finally { Exit(); }
        }
    }

    // Never block a carrier waiting for another logical operation. The
    // active lease survives thread migration until the socket await settles.
    private void Enter()
    {
        int previous = Interlocked.CompareExchange(ref _lifecycle, Active, Idle);
        if (previous == Closed || previous == Retiring)
            throw new ObjectDisposedException(nameof(UdpLoopbackHost));
        if (previous != Idle)
            throw new InvalidOperationException("concurrent or reentrant UDP operation");
    }

    private void Exit() => Volatile.Write(ref _lifecycle, Idle);

    // Cancellation settles the local socket operation; it is not a guarantee
    // that a datagram was never accepted by the operating system.
    internal async ValueTask SendToAsync(int destinationPort, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (destinationPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destinationPort));
        Enter();
        try
        {
            _authorize(_requestedPort, Action.Local);
            _authorize(destinationPort, Action.Send);
            // Authorize before observing/copying guest-supplied bytes.
            if (payload.Length > MaxPayload)
                throw new ArgumentOutOfRangeException(nameof(payload));
            cancellationToken.ThrowIfCancellationRequested();
            byte[] owned = payload.ToArray();
            int sent = await _socket.SendToAsync(owned.AsMemory(), SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, destinationPort), cancellationToken)
                .ConfigureAwait(false);
            if (sent != owned.Length) throw new IOException("partial UDP datagram send");
        }
        finally { Exit(); }
    }

    /// <summary>
    /// Wait asynchronously for one datagram, or settle cancellation. Length
    /// zero is a valid datagram. Over-cap datagrams are consumed and rejected,
    /// never truncated into a successful packet. No caller-owned memory is
    /// retained, and no pending operation releases its descriptor lease.
    /// </summary>
    internal async ValueTask<Packet> ReceiveFromAsync(int capacity,
        CancellationToken cancellationToken)
    {
        if (capacity is < 1 or > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Enter();
        try
        {
            _authorize(_requestedPort, Action.Local);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] scratch = new byte[capacity + 1];
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(scratch.AsMemory(),
                    SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                throw new InvalidDataException("UDP datagram truncated and discarded", ex);
            }
            // Authorization is checked again after the await. A policy that
            // was revoked while waiting must not publish an owned packet.
            _authorize(_requestedPort, Action.Local);
            if (received.RemoteEndPoint is not IPEndPoint peer ||
                !peer.Address.Equals(IPAddress.Loopback) ||
                peer.Port is < 1 or > 65535)
                throw new UnauthorizedAccessException("UDP packet source outside loopback");
            _authorize(peer.Port, Action.Source);
            if (received.ReceivedBytes > capacity)
                throw new InvalidDataException("UDP datagram truncated and discarded");
            return new Packet(scratch.AsSpan(0, received.ReceivedBytes).ToArray(), peer.Port);
        }
        finally { Exit(); }
    }

    // The trusted teardown worker must cancel and await pending operations
    // before retiring the handle. Active retirement is rejected immediately;
    // no blocking lock, early close, or reuse of an in-flight descriptor.
    public void Dispose()
    {
        int previous = Interlocked.CompareExchange(ref _lifecycle, Retiring, Idle);
        if (previous == Closed) return;
        if (previous != Idle)
            throw new InvalidOperationException("UDP descriptor is active or retiring");
        // Do not acknowledge a concurrent Dispose before physical teardown.
        // Even if disposal throws, the descriptor remains permanently sealed.
        _socket.Dispose();
        Volatile.Write(ref _lifecycle, Closed);
    }
}
