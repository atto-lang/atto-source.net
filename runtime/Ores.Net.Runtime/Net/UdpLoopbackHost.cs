using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

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
    private readonly object _gate = new();
    private bool _closed;
    // Monitor.TryEnter is reentrant on the SAME thread: a trusted policy
    // callback must not recursively operate on its active UDP descriptor.
    // This flag is protected by _gate and fences nested ingress explicitly.
    private bool _inOperation;

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
                RequireOpen();
                _authorize(_requestedPort, Action.Local);
                return ((IPEndPoint)_socket.LocalEndPoint!).Port;
            }
            finally { Exit(); }
        }
    }

    private void RequireOpen()
    {
        if (_closed) throw new ObjectDisposedException(nameof(UdpLoopbackHost));
    }

    // Nonblocking operation: concurrent actor call attempts fail rather than
    // park on a monitor held by a different thread.
    private void Enter()
    {
        if (!Monitor.TryEnter(_gate))
            throw new InvalidOperationException("concurrent UDP operation");
        if (_inOperation)
        {
            Monitor.Exit(_gate);
            throw new InvalidOperationException("reentrant UDP operation");
        }
        _inOperation = true;
    }

    private void Exit()
    {
        _inOperation = false;
        Monitor.Exit(_gate);
    }

    internal bool TrySendTo(int destinationPort, ReadOnlySpan<byte> payload)
    {
        if (destinationPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destinationPort));
        Enter();
        try
        {
            RequireOpen();
            _authorize(_requestedPort, Action.Local);
            _authorize(destinationPort, Action.Send);
            // A denied destination takes precedence over observation or
            // copying of guest-supplied bytes. The span is a borrowed view,
            // so retain an owned snapshot only after peer authorization.
            if (payload.Length > MaxPayload)
                throw new ArgumentOutOfRangeException(nameof(payload));
            byte[] owned = payload.ToArray();
            try
            {
                int sent = _socket.SendTo(owned, SocketFlags.None,
                    new IPEndPoint(IPAddress.Loopback, destinationPort));
                if (sent != owned.Length) throw new IOException("partial UDP datagram send");
                return true; // zero byte datagram is valid and returns zero
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                return false;
            }
        }
        finally { Exit(); }
    }

    /// <summary>
    /// Null means would-block, Packet with length 0 means a received empty
    /// datagram. Over-cap datagrams are consumed and rejected, never truncated
    /// into a successful packet or published into caller-managed memory.
    /// </summary>
    internal Packet? TryReceiveFrom(int capacity)
    {
        if (capacity is < 1 or > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Enter();
        try
        {
            RequireOpen();
            _authorize(_requestedPort, Action.Local);
            byte[] scratch = new byte[capacity + 1];
            EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            int size;
            try
            {
                size = _socket.ReceiveFrom(scratch.AsSpan(), SocketFlags.None, ref endpoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                return null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                throw new InvalidDataException("UDP datagram truncated and discarded", ex);
            }
            if (endpoint is not IPEndPoint peer ||
                !peer.Address.Equals(IPAddress.Loopback) ||
                peer.Port is < 1 or > 65535)
                throw new UnauthorizedAccessException("UDP packet source outside loopback");
            _authorize(peer.Port, Action.Source);
            if (size > capacity)
                throw new InvalidDataException("UDP datagram truncated and discarded");
            return new Packet(scratch.AsSpan(0, size).ToArray(), peer.Port);
        }
        finally { Exit(); }
    }

    // Teardown worker, not a guest actor carrier, owns synchronous dispose.
    // The caller must establish physical quiescence before retiring the handle.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _socket.Dispose();
        }
    }
}
