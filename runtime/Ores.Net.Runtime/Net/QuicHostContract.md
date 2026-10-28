# QUIC host adapter: loopback-only permit gate

**Status:** trusted host-only MsQuic client admission, no guest `std/net/quic`,
no HTTP/3/QPACK guest framing, no general Internet egress, no proven native
interop/AOT execution.

The original naked `ConnectHttp3Async(IPEndPoint, string, CancellationToken)`
remains **fail closed**, even if MsQuic is supported. The new overload
`ConnectHttp3Async(QuicLoopbackPermit, actorId, actorGeneration, cancellationToken)`
can use real `QuicConnection.ConnectAsync` **only** after consuming a
`QuicLoopbackPermit`, issued by the trusted runtime.

## Current, intentionally restricted issuer

`QuicLoopbackPermit.AuthorizeForTrustedHost` requires a trusted caller to
supply its **complete** connection-local DNS answer set, a selected index,
canonical DNS name, actor identity/generation, positive resolution generation,
and an expiring budget of at most ten seconds. It rejects the *entire* answer
set if **any** address is not loopback, if any address is IPv4-mapped IPv6, if
scoped IPv6 is supplied, or if there are more than 16 answers. A selected
address outside the set is denied; it is never modulo-wrapped.

The permit clones its selected IP bytes, has a private constructor, binds
the actor identity/generation, and is consumed atomically **exactly once**,
including when the actor identity is wrong or the permit has expired. The
permit deadline is computed using the monotonic `Stopwatch` clock, not wall
clock time. The adapter builds a *new* concrete `IPEndPoint` from the pin,
retains the original SNI for TLS certificate verification, uses TLS 1.3 with
ALPN `h3`, enforces the remaining deadline with both a linked cancellation
token and `HandshakeTimeout`, and disallows HTTP/2 fallback.

The existing 3-argument client entrypoint cannot be used to evade this
boundary. A newly connected `QuicConnection` is disposed if the handshake
returns after cancellation. Callers still must close and dispose the
connection and all streams to obtain real physical retirement evidence.

**Important limitation:** this issuer does not itself perform DNS and cannot
prove a caller supplied the complete DNS response. It is only suitable for a
trusted runtime-host injection/test path and cannot authorize public egress.
It does not yet bind to the Oreslang CONTROL-verified actor lease, prevent
migration at the provider level, enforce global connection/socket buffers,
or prove actor cleanup after shutdown. None of those guarantees may be
inferred from an opaque permit or the provider's `IsSupported` property.

## Exact-head acceptance gates

- Compile and run `tools/Ores.Net.QuicAdmissionChecks` on .NET 10, with
  negative assertions for full-set denial, nonexistent/expired/single-use
  permit, generation mismatch, cancellation and copied DNS bytes.
- Prove Linux and Windows Native AOT publish; identify runtime MSQuic/OS
  availability and repeat the handshake in the published binary.
- Run an actual TLS 1.3 `h3` handshake against a local independent QUIC
  server; confirm correct SNI/chain validation and wrong-host rejection.
- Add production `OutboundEgressPolicy` equivalent that owns resolution and
  issues an unforgeable capability after *its own* whole-set checks.
- Wire CONTROL actor ownership, connection/stream quotas, generation fences,
  cancellation-versus-physical-retirement receipts and no active migration.
- Verify exact source Git tree, committed tests, real runner steps and logs;
  zero-step source Actions are **not evidence of execution**.

As of October 28, 2026 this environment has no .NET SDK and the
authorized funded cross-org private-source carrier remains blocked on
protected-main and independent review. The C# source/test changes are
**authored but uncompiled**.
