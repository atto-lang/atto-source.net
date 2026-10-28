# QUIC host adapter: loopback-only permit gate

**Status:** trusted host-only MsQuic client admission, no guest `std/net/quic`,
no HTTP/3/QPACK guest framing, no general Internet egress, no proven native
interop/AOT execution.

The original naked `ConnectHttp3Async(IPEndPoint, string, CancellationToken)`
remains **fail closed**, even if MsQuic is supported. The new overload
`ConnectHttp3Async(QuicLoopbackPermit, ActorArena, cancellationToken)`
can use real `QuicConnection.ConnectAsync` **only** after consuming a
`QuicLoopbackPermit`, issued by the trusted runtime. The adapter now checks
actual arena identity/incarnation and rejects already-retired owners, rather
than trusting caller-provided numeric actor IDs. It also rechecks retirement
and cancellation after the physical handshake and disposes on refusal.

**Still outstanding:** a borrowed `ActorArena` reference is not a native
connection ownership lease, and a stop occurring after the final retirement
check can race the raw connection return. Automatic actor-stop cancellation,
stream ownership, a lifetime pin, migration fencing, and physically witnessed
final disposal MUST be implemented before exposing guest QUIC. The current
change is only an admission-time reduction of stale-actor authority.

## Current, intentionally restricted issuer

`QuicLoopbackPermit.AuthorizeForTrustedHost` requires a trusted caller to
supply its **complete** connection-local DNS answer set, a selected index,
canonical DNS name, actor identity/generation, positive resolution generation,
and an expiring budget of at most ten seconds. It rejects the *entire* answer
set if **any** address is not loopback, if any address is IPv4-mapped IPv6, if
scoped IPv6 is supplied, or if there are more than 16 answers. A selected
address outside the set is denied; it is never modulo-wrapped.

The issuer and the MsQuic entrypoint now share the same strict
lowercase-ASCII DNS name validation: labels must be 1..63 bytes, have no
leading/trailing hyphens, and total name length must not exceed 253 bytes.
Unicode names must already be converted to valid canonical A-labels.
Concurrent attempts to consume a permit have exactly one winner via
`Interlocked.Exchange`; the standalone negative suite includes a 128-way
race and rejects duplicate/expired permit reuse.

After a successful QUIC/TLS 1.3 handshake, the host adapter rechecks the
reported remote literal IP+port, negotiated `h3` ALPN, TLS 1.3 version and
original TLS target hostname. Any mismatch causes physical
`DisposeAsync` before the connection may escape. This **does not**
enforce the remote address after handshake or disable migration, and a
future source-level lifetime wrapper must address that separately.

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

## Current-source security hardening (October 28, 2026)

The trusted host permit issuer no longer validates a caller-controlled
`IReadOnlyList<IPAddress>` by enumeration and then re-indexes that same
possibly mutable list to select a pin. It now captures a **bounded, indexed
snapshot** of validated address bytes and selects the connection IP from the
snapshot, not from an untrusted second read. Negative fixtures supply a DNS
collection whose iterator claims loopback but whose indexer returns a public
IP, and another whose indexer changes from loopback to a public IP on a second
read. Both patterns must fail closed or preserve the first validated pin.
Canonical TLS identities now also reject numeric IP literals.

This closes a **time-of-check/time-of-use flaw in the host admission helper**.
It does **not** prove that the caller's list came from an authoritative
policy-owned DNS resolution; therefore general public egress is still
disabled and a compiler/runtime CONTROL-issued lease remains required.

A separate, source-free .NET 10 SDK/NativeAOT carrier probe was created in
`canonical-cloud-test/readiness-edge-e2e` PR #38. Its first workflow
run `40749652387` failed with zero executed steps, so this carrier did not
demonstrate runner or SDK capacity. No Oreslang private source was mirrored.

### Verified source-free .NET toolchain, remaining QUIC provider failure

On 2026-10-28 the **independent, source-free** probe in
`beamscale/bmscl-compiler` PR #63 executed .NET 10 compilation and Linux
x64 Native AOT publish/run successfully on the same exact public API surface.
Run `40750819089` completed successfully with **six executed steps**.
However both managed and Native AOT probes printed
`MSQUIC_UNAVAILABLE` from `QuicConnection.IsSupported`. Per official .NET
documentation Linux requires the separate `libmsquic` native package (and
TLS 1.3 support). A green Native AOT compile does **not** prove a
QUIC/TLS handshake. Install/review the native dependency from an approved
source before attempting real peer interoperability.

The source-owned issuer was additionally hardened to snapshot IP address
**bytes before classification**. This prevents mutable legacy `IPAddress`
address/scope values from changing the authorization between validation and
connection. Adversarial tests cover iterator/index disagreement, changed
IP address values, scoped IPv6 mutations and single-use consumption.
The native .NET adapter uses an explicit Linux/Windows/macOS fail-closed
runtime guard and a **narrow** CA1416 suppression only within the guarded
MsQuic implementation; the .NET 10 source-free probe verified that an
unannotated OS-disjunction guard does not, by itself, suppress all analyzer
warnings in a warnings-as-errors build.

None of these source changes have been built or executed on their own exact
PR tree; source org Actions remain zero-step and the Beamscale destination
is unprotected/unapproved for private-source transfer.

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
