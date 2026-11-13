# Source-compiled Counter V2 main — .NET NativeAOT candidate

Status: **DRAFT / NOT EXECUTED ON THIS HEAD**. The opt-in compiler now checks the entire canonical six-unit Counter V2 package, including the actual async `main.atto`. It is still a **closed-source subset**, not a general AttoLang task/await compiler or full Java/GraalVM actor parity.

## Source-lowered operations

- `spawn Counter()` becomes the checked generated native actor spawn, not an in-process host counter.
- `await worker.get_ready()` is immediate only because this closed native actor registration returns with its mailbox ready to accept DATA. It is not a general asynchronous actor-start protocol.
- Each `worker.send(N)` is parsed as a signed i64 source operand and compiled into an eight-byte little-endian mailbox `Send`.
- Each `await worker.outputs.next()` is compiled as `await ChannelReader.ReadAsync` on a **bounded four-value** internal output channel, fed exclusively by the supervisor mail actor. The actor's physical carrier never parks on a blocking user read.
- Source `stdio.stdout.write` operations drive runtime i64 formatting and delimiter placement, preserving order. Stdout is **not precomputed** by the compiler.
- `await worker.get_done()` uses the exact actor-identity/nonce-checked runtime task captured while the actor is alive. The task becomes completed on physical STOP/finalize. This adds only a read-only public observation API; it does not expose scheduler dispatch.
- The compiler rejects source shapes it cannot safely lower, including early/missing STOP, premature completion, wrong output streams, duplicate bindings, bad formatting, forbidden calls, and provable signed-i64 overflow.

## Executable verification

A separate `Ores.Net.CounterMainV2Guest` publishes **full NativeAOT** on Linux x64 and Windows x64. The CI workflow verifies all seven canonical source blobs, materializes a fresh package, runs both real source emitters, byte-compares generated C# with golden sources, runs the full managed verification, publishes both native binaries, and executes each **three times**. The guest main binary must produce exactly `1|3|6|10` on stdout, with no other bytes and no .dll sidecars. The existing native actor probe continues to test two-actor isolation, native state reclamation, STOP/stale send, and malformed ActorMail.

The original `main.atto`, not a synthetic host test, is the authority for source messages and output formatting. The compiler still emits reviewed C# at build time, not general verified actor CIL/MIR. A genuine exact-head runner with nonzero executed steps, native AOT publish, hash/identity, output and independent review is required before reporting parity.

## Remaining gaps

General `async routine` and `await` semantics, nontrivial coroutine captures, async suspension inside actors, general `ActorMail<T>`, typed ActorIR/CIL, verifier proof for arbitrary actor logic, multi-package linker, cross-backend Java/GraalVM and LLVM guest execution. None of these are claimed by this bounded candidate.

Never transfer private source to an external carrier without exact-tree validation and the required independent authorization. A failed Actions job with `runner_id=0`/`steps=[]` proves only runner admission failure, not a program failure or success.

## Source contract reconciliation

The parent #498 Counter fixture changed to detached immutable Copy semantics: `val int snapshot = self.handle_state(critical-read |s| -> s.total);` followed by `self.send(snapshot);`. This six-source compiler now admits only the updated receive syntax through the parent verifier; direct state loan transport, mutation of the copy alias, and suspension before loan release are rejected. The native handler uses actor-owned `ActorNativeScalarState.ReadInt64` and a disposable arena read loan to materialize the snapshot **before** the mailbox send. Source and generated C# are verified together on the same exact head/tree; no new native execution is claimed until full CI is runner-allocated.
