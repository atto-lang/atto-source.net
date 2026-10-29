# QUIC host adapter: loopback-only permit gate

**Status:** trusted host-only MsQuic client admission, no guest `std/net/quic`,
no HTTP/3/QPACK guest framing, no general Internet egress, no proven native
interop/AOT execution.

The original naked `ConnectHttp3Async(IPEndPoint, string, CancellationToken)`
remains **fail closed**. The narrow host-only overload
`ConnectHttp3Async(QuicLoopbackPermit, ActorContext, CancellationToken)`
consumes a single-use permit, registers a pending operation in the
**actor-local CONTROL-owned ledger before suspension**, and returns an
opaque `ActorQuicConnectionLease` rather than an untracked
`QuicConnection`. The `ActorContext` is issued only by `ActorRuntime`,
and registration validates its active physical turn and takes a lifetime pin.

Actor Stop seals registration synchronously and cancels the owner's pending
native operations off the actor carrier. Publication of a handshake result
is linearized with Stop: the losing handle must be disposed before the
registry reports completion. Each actor admits at most 16 QUIC connections;
each connection admits at most 32 locally tracked outgoing streams. Native
resource teardown waits for all in-flight stream opens **and active reads/writes**;
each stream is cancelled and disposed before the connection. `StopAndDrainQuicAsync` waits for both actor-dispatch
finalization and native resource disposal, returning `true` only if the
actor arena has physically reclaimed. A disposal exception faults this
receipt rather than being reported as success. A plain synchronous `Stop`
does **not** wait for the receipt.

`StopWithReceipt(actor)` preserves the prior void `Stop(actor)` API but
returns a durable `ActorRetirementReceipt`. Callers may await
`receipt.WaitAsync()` or `StopAndDrainQuicAsync(receipt)` **after** the
actor was removed from the live dictionary; no stale actor lookup is needed.
A bare `StopAndDrainQuicAsync(actorRef)` is appropriate when stopping an
*as-yet-live* actor atomically, not when re-reading an already stopped actor.
The runtime itself only retains finalized actor cells whose native
retirement is still incomplete, instead of retaining unbounded tombstones.

`ActorRuntime.DisposeAsync()` revokes all live actors and waits for both
active and already-finalized native QUIC cleanup, and for carrier joining.
Legacy synchronous `Dispose()` also waits on this same completion task
from a non-actor thread. Multiple concurrent or repeated disposal calls
join the same task. Native retirement failure remains observable even if
the actor was removed from the live registry; physical reclamation must
be observed before successful shutdown. These are **soft-real-time host
control APIs** and may wait indefinitely if a native operation cannot
physically retire.

**Remaining blockers:** This is host-only .NET runtime scaffolding, not
verified source-level guest lowering. It has not been compiled or run on its
current exact source tree due to zero-step source CI.
There is no complete verifier of destination path migration, actor-wide
socket buffers, production egress DNS provenance or full QUIC/TLS peer
interoperability through this private implementation. Stream I/O still
requires executed stop/cancellation race tests. Hard-real-time guarantees
are **not** claimed: cleanup uses asynchronous managed control work.

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
returns after cancellation. The opaque owner lease and actor-local ledger dispose connections and
streams on stop. Callers requesting a physical retirement receipt await
`StopAndDrainQuicAsync`, not merely a cancellation notification.

**Important limitation:** this issuer does not itself perform DNS and cannot
prove a caller supplied the complete DNS response. It is only suitable for a
trusted runtime-host injection/test path and cannot authorize public egress.
It binds to host-runtime `ActorContext` and actor-local CONTROL stop, but
not yet to a compiled guest `ActorRef` or a production CONTROL-issued egress
capability. It cannot prevent native provider migration, enforce complete
global socket/stream-buffer quotas, or prove shutdown completion without
executed exact-head tests. None of those guarantees may be
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

### Independent source-free .NET toolchain and Native AOT handshake

The public-only .NET 10 probe in `beamscale/bmscl-compiler` PR #63 was
extended after its initial `MSQUIC_UNAVAILABLE` run. On Ubuntu 24.04 it
installed the version-pinned Microsoft-signed `libmsquic=2.5.12` package.
[Run 40770468363](https://github.com/beamscale/bmscl-compiler/actions/runs/40770468363)
completed **nine executed steps**, and its logs contain both
`SOURCE_FREE_QUIC_TLS13_STREAM_PASS` and
`SOURCE_FREE_NATIVE_AOT_QUIC_TLS13_STREAM_PASS`. It demonstrated real
TLS 1.3, test-only pinned certificate/SNI/ALPN and bidirectional stream
exchange in **both managed .NET and Linux Native AOT**. It did **not**
compile the private Oreslang runtime or prove production PKI, HTTP/3 or
actor lifecycle parity. No private Oreslang source was transferred.


The source-owned issuer was additionally hardened to snapshot IP address
**bytes before classification**. This prevents mutable legacy `IPAddress`
address/scope values from changing the authorization between validation and
connection. Adversarial tests cover iterator/index disagreement, changed
IP address values, scoped IPv6 mutations and single-use consumption.
The native .NET adapter uses Linux/Windows/macOS fail-closed guards and
explicit supported-platform annotations on OS-specific methods, retaining
warnings-as-errors analysis. The source-free fixture verified the platform
analyzer requires these explicit callsite guards or annotations.

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
protected-main and independent review. The C# actor-native ledger, connection/stream lease, cancellation and
retirement regression changes are **authored but uncompiled**.
