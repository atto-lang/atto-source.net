# Native QUIC host primitive (not guest support)

`NativeQuicClient` uses `System.Net.Quic` / MsQuic and performs a genuinely QUIC/TLS 1.3 handshake to an IP-literal `IPEndPoint`, while validating the canonical source hostname through SNI / TLS certificate verification. HTTP/3 selects ALPN `h3`. No `HttpClient` HTTP/2 fallback is allowed.

**Status**: trusted host-only building block. Guest `std/net/quic`, HTTP/3 framing/QPACK, inbound listener, actor handle ownership, policy-authorized DNS-set minting, physical retirement receipts and running interop tests are NOT implemented. Never represent this as guest protocol parity. No public guest binding should call the adapter until the complete egress policy, bounded resource/loan and exact-head AOT evidence exists.

Security: the caller MUST authorize the complete DNS answer set against current actor/tenant egress policy and pass only the selected pinned IP; the host primitive does **not** perform policy admission. Tokens need an externally enforced deadline, and cancellation is not a substitute for awaiting stream/connection native disposal. Production use requires MsQuic supported on the target RID and a .NET Native AOT publish.
