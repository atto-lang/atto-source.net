# Source-generated state on the production actor runtime

This draft reconciles the source/host stack through #274 with main and the
execution-owned actor frame integration #459 (`9c0b65eb`). It preserves mailbox
nonce/generation checks, current NIO policy, queued-byte quotas, and actor-owned
native arena/frame tests. It does not establish full Java/LLVM/.NET parity.

## Production path

`emit-actor-csharp examples/actor-counter.atto` produces a statically compiled
`Actor_Counter.SpawnRuntime(runtime, replyTo)` entry. It creates an isolated
profile actor on the **actual ActorRuntime**, not only the older OresVmHost
Counter scheduler. Each instance owns separate native scalar state. Incoming
empty DATA messages invoke its one no-argument operation; replies contain
data-only actor ID/result bytes and go through a real supervisor mailbox.
There is no direct supervisor callback or wire reconstruction of an ActorRef.
This fixed envelope is not a universal MessagePack contract.

`ActorNativeScalarState` stores immutable defaults and opaque handles as host
metadata; live mutable scalar values occupy the actor's native arena. Access
requires a live physical ActorContext and its exact actor incarnation. A
nonparking exclusive fence rejects accidental cross-actor metadata aliasing.
The checked scalar operation stores its operands/result on the caller's existing
execution-owned logical stack. It creates neither an execution identity nor a
stack per critical callback. Finally pops/scrubs its activation, including after
checked overflow. The native arena and stack remain under the existing actor
budget and retirement protocol.

The compiler accepts canonical contiguous `critical-write` in this opt-in
increment profile and rejects `critical-read` mutation and split modifiers.
Bare `critical` is retained only as legacy write syntax. `.atto` fixtures and
generated static C# are verified together; no runtime emit/plugin/JIT is needed
by the NativeAOT probe. Regular scalar/structured check/compile still rejects
unlowered actors; the opt-in emitter is not general actor CIL lowering.

## Reconciliation fixes and evidence

The merged old scheduler's user dequeue bypassed main's newer tenant/host byte
refunds. A regression failed with `scheduled dequeue did not refund tenant/host
quotas`; the scheduler now uses the existing host-mediated dequeue, preserving
FIFO control discrimination and exact quota reuse. Typed control envelopes also
required updating the scrub test's observation of its privately owned DATA
buffer; no production buffer is exposed. Current macro/critical/suspension,
source-byte-budget, supervisor and NIO admission gates remain checked.

The complete managed verification executable passes, including exact source
emitter/golden comparisons for ordinary and overflow Counter actors. A genuine
macOS arm64 NativeAOT Mach-O probe passes **three consecutive executions**:

- two actual source-generated private actors return A=1,2,3 and B=1,2 through
  the supervisor mailbox;
- a separately source-generated overflow actor fails, its peer continues to
  B=3, and a fresh actor starts at 1;
- runtime negatives prove no partial overflow commit, reuse of the same logical
  context after finally with an exact 16-byte stack budget, and foreign actor
  denial without poisoning the rightful owner;
- actor shutdown joins cleanup and native reserved bytes return to zero;
- the older trusted-host Counter/control/nonce/shape/quota suites also execute,
  but their managed cells are not native actor arena evidence.

Native binary SHA-256:
`9efbe52147aac1719a5c302e8aae566c5d6fc56b411628ab6f000ddde472e613`.
Compiler and new Counter probe pin ILCompiler/runtime packs 10.0.14, following
the existing memory probe, instead of bypassing the 10.0.0 NuGet security audit
failure. Linux/Windows workflow gates are authored; they have not executed on
this candidate and are not certified by the macOS result.

## Still required

The source emitter currently recognizes only the bounded synchronous +1 actor
operation with explicit state effects. It is not Java's receive/ActorMail API,
LLVM's on_message(int) API, general methods/classes, async actor CIL, full
borrow-checking, or the Spin server. The source body must become typed Actor IR
with complete ownership/effect inventory and a canonical Rust checker adapter.

Production ActorRuntime remains serial. Concurrent-default guest execution,
compatible guest read cohorts/exclusive write admission, suspended context/stack
restoration, arbitrary object/string/closure arena lowering, shared application
regions, singleton/symbol borrowing and untrusted tenant VM isolation remain
open. Native private scalar state and compact arena handles do not by themselves
certify every guest allocation or every execution boundary. Spin's .NET backend
must remain unverified until its actual shared source compiles and runs.
