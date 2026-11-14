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

First eliminate the `ActorMailbox.Envelope` and `byte[].ToArray()` managed
allocations via a bounded native lease/transfer protocol (PR #513), with an
ownership, shutdown, failure-atomicity and budget-refund proof. Then remove
per-turn managed context allocations, without letting untrusted actor code
bypass sealed state, scheduler admission, supervisor CONTROL, or pinned await
frames. Establish lower measured allocation targets from completed exact-SHA
benchmarks; do not claim zero CLR GC for the .NET process.

Never mirror this private repository to another org without authorized source
snapshot, independently reviewed destination, and protected policy approval.
