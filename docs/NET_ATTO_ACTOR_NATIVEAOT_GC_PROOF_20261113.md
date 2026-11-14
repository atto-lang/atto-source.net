# Compiler-verified `.atto` actor mailbox/GC NativeAOT witness

Status: **candidate — NOT_EXECUTED** until an actual managed/native job has steps.

## Precisely supported scope

`examples/actor-message-counter.atto` is parsed and strictly verified by
`OresParser.InspectSynchronousActorIncrement`. The compiler
`emit-actor-csharp` emits a C# implementation for this *single* typed scalar
message and exclusive `critical-write` actor-state mutation profile. CI
compares the emitted bytes against the committed `GeneratedMessageCounterActor.g.cs`
and statically compiles that source into the native executable. It cannot
substitute hand-written actor logic if the emitter changes unexpectedly.

This is genuine **compiled and executed Atto source for a restricted actor
subset**, not general actor-to-CIL lowering, arbitrary `receive`, async actor
continuation, hot loading, the complete mailbox zero-copy path, or Java/LLVM
parity. These remain unimplemented/unproven outside the named subset.

## Execution contract

`verification/Ores.Net.AttoActorGcProbe` creates two host-owned actors
(supervisor and compiler-emitted MessageCounter), warms 256 messages, then
delivers 4,096 real 8-byte i64 mailbox messages, 32 at a time. Each actor turn
performs generated checked native actor-state access and transmits a real reply
through its supervisor mailbox. The witness validates exact cumulative FIFO
values, actor turn accounting, native budget stability, physical refund after
STOP, malformed-message isolation and stale actor handle rejection.

After warmup the probe observes **process-wide**
`GC.GetTotalAllocatedBytes(precise: true)` and three GC collection counters.
It reports managed bytes per delivered guest message, collections and time.
The preliminary 8,192 bytes/message ceiling prevents gross regressions, but
it is deliberately **not** a zero-allocation claim. The current mailbox
allocates managed envelope/byte[] and per-turn context objects. A separate
low-level numeric-handle microbenchmark is in PR #517 and cannot substitute
for this end-to-end measurement.

The same program runs managed and as a real native executable built by
`dotnet publish -p:PublishAot=true` for Linux x64 and Windows x64. The native
phase requires `ATTO_REQUIRE_NATIVE_AOT=1` and
`RuntimeFeature.IsDynamicCodeSupported == false`. The workflow emits exact
commit/tree, fixture/compiler/generated source Git blobs, and native binary
SHA-256 in a successful stepful run.

The negative `tests/fixtures/actor-gc-forbidden-critical-read.atto` attempts
a state write under `critical-read`; source verification must fail *without*
publishing a generated C# file.

## Next implementation reductions

The same compiled `.atto` guest benchmark now tests **both** storage
modes via `ATTO_NATIVE_MAILBOX=0|1`. The opt-in configuration
`ActorRuntimeOptions.NativeMailboxSlotBytes=16` stores queued message bytes in
a fixed actor-owned instance of `NativeFixedBufferPoolV1`, charged against
the same native tenant/process budget as the actor's arena. Queue capacity
remains fixed, oversized messages fail before queue admission, and STOP
scrubs/reclaims undelivered leases. Failed actor Spawn refunds its separate
mailbox slab. Restarting a native mailbox in place is explicitly forbidden.

**Critical ownership boundary:** the existing user callback takes
`ReadOnlyMemory<byte>`, which a handler can retain. It is unsafe to supply
this callback with an array or native-backed memory view that will later be
reused by another tenant, actor or turn. The native queue therefore deliberately
**copies bytes into a fresh managed array at dequeue**. The existing actor
turn-finally scrubs that managed array. A retained-message canary in the
benchmark asserts that old message views remain all-zero after later turns.
No raw native pointer escapes to user code. This staging mode is not zero-copy
delivery and may use **more managed allocations** than the legacy queue
because `Envelope`, `Lease` and `Loan` objects remain managed.

The managed and Linux/Windows native workflows execute identical 4,096-message
compiled guest runs in both `managed_copy_on_send` and
`native_copy_on_dequeue` modes, recording allocation bytes/message, GC
collection deltas and elapsed time. The comparison is an experiment, not a
performance result until the exact-head workflow actually executes. A 50k
zero-CLR-allocation assertion for `WriteBytes`/`ReadBytes` on a
**pre-existing loan** in PR #513 proves only the native-copy primitive, not
the allocation cost of lease acquisition, mailboxes, actors or scheduling.

### Next safe near-zero-GC implementation

Introduce a separate compiler/runtime **borrow-only**
`ReadOnlySpan<byte>` handler, a bounded generation-fenced **value token**
(rather than a newly allocated `Lease`), and a preallocated FIFO ring.
The native loan must never outlive an admitted physical turn or suspension;
pinned await frames require a separate verified continuation contract. Keep
the retainable `ReadOnlyMemory<byte>` API on the legacy safe copy path, not
as a borrowed zero-GC endpoint. Eliminate managed `ActorContext` and
`ActorExecutionContext` per-turn allocations only after profile-based
evidence and correctness tests across STOP, fault, restart and supervisor
CONTROL paths. Do not claim zero CLR GC for the entire .NET process.

Never mirror this private repository to another org without authorized source
snapshot, independently reviewed destination, and protected policy approval.
