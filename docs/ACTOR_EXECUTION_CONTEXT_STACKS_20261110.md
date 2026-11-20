# Execution-context stacks and actor-private native memory

Each logical execution context owns exactly one logical stack, independently of
which function or method started it. A receive invocation is one current entry
point for a new context, not the definition of stack ownership. Nested
synchronous calls push frames on their caller's execution stack. Independently
scheduled work needs a distinct context/stack. A future authenticated await/resume
must retain the original context and stack across carrier migration; unrelated
DATA must not acquire them. Concurrent live contexts will need disjoint live
stack regions under the actor's aggregate quota.

This branch integrates #395 → #453 → #454 onto current main
`3f3606cc3ef81dad117047b4cb57609ad1a92672`, preserving those histories and
the existing native ActorArena as the sole actor storage allocator.

## Implemented synchronous receive boundary

`ActorExecutionContext.Stack` owns a distinct `ActorExecutionStack` object.
The actor owns `ActorFrameStorage`, which supplies bounded native storage and
actor-terminal quota state. Logical stack authority and completion are private
to the execution context. A completed context cannot reopen its stack or acquire
a subsequent context's frames; physical arena bytes may be reused only after
completion and scrubbing. The implementation currently creates these contexts
at synchronous receive entry; the relationship itself is execution-based.

- `ActorRuntime.RunCell` mints an exact-issued `ActorExecutionContext` for each
  admitted receive, binding its ActorRef incarnation and non-wrapping local
  invocation sequence. Carrier identity is not invocation identity.
- Every native frame capability records that exact context. Access still
  requires the currently admitted physical actor turn and the original frame
  object; metadata-equivalent, foreign, stale and later-receive capabilities
  cannot acquire its bytes. Contexts and frame authority reject JSON transport.
- The runtime scrubs all remaining invocation frame bytes and revokes its frame
  objects in `finally`, including handler exceptions and swallowed quota faults.
  The bounded stack slab remains within its actor's existing native arena and
  can be reused by a later completed receive. Ordinary persistent heap data
  remains valid across messages and independent of receive stack lifetime.
- Quota poison remains actor-terminal. Closing the invocation does not clear it
  or permit another DATA turn to recover the same failed incarnation.

The old host test that treated a later DATA message as a continuation has been
replaced with stale-receive denial, fresh zeroed stack bytes, distinct execution
identities and persistent-heap survival. Typed-frame anti-forgery, failure-atomic
edge transfer and the three swallowed-quota negative controls remain exercised.

## Executed evidence

Local .NET SDK 10.0.100, macOS arm64: the complete managed verification executable
passes, including compiler/CFG/CIL tests and the distinct host actor-memory,
frame, execution-context and scalar-schema suites. These host runtime tests are
not compiled `.atto` actor execution.

The actual `Ores.Net.ActorMemoryProbe` **NativeAOT arm64 Mach-O executable**
passes three consecutive runs. Each checks four private native heaps (including
two actors sharing a carrier pool), foreign and stale heap/frame authority,
fresh per-receive contexts/stacks, peer progress during pinned STOP and after
physical release, sealed typed schemas, parallel scalar transfers, all three
terminal stack-quota branches and complete native-budget return.

Binary SHA-256:
`c7225b1f38087b744cefdfb6b8bd5fe52f58255f5ef463e15b6364330461dff4`.
The probe pins runtime and ILCompiler **10.0.14**. The initial SDK-default 10.0.0
restore failed vulnerability admission; it was not bypassed. Version 10.0.14 is
the patched release for [GHSA-p3jp-9v83-76w9](https://github.com/advisories/GHSA-p3jp-9v83-76w9).

```sh
dotnet run --project verification/Ores.Net.Tests -c Release
dotnet publish verification/Ores.Net.ActorMemoryProbe -c Release -r osx-arm64 -o /tmp/atto-actor-probe
/tmp/atto-actor-probe/Ores.Net.ActorMemoryProbe
```

Linux and Windows exact-head NativeAOT execution are not claimed by these local
macOS runs. The existing source workflow retains those target gates and now
requires the updated per-receive-stack receipts. No hosted run is certified.

## Three-backend integration boundary

| Backend | Executed current work | Still incomplete |
| --- | --- | --- |
| Java #2781 | 69 frame tests, 39 boundary tests, seven guest tests with `-Xss512k`; Spin routing/builder and 110 HTTP checks | General native guest heap lowering, all invocation/continuation frames, concurrent-default execution, Native Image proof |
| LLVM #1649 | Generated scalar actors: nonoverlapping 64-KiB frame arenas, 64-frame terminal quota, sibling/replacement survival, O0/O2 low-stack and ASan/UBSan | General objects/methods/await, disjoint concurrent invocation stacks, complete Spin execution |
| .NET, this branch | Native actor arenas, per-receive contexts and frame revocation; managed verifier and real macOS NativeAOT host probe | Compiler-generated general `.atto` actor dispatch and frames, authenticated suspend/resume, concurrent receive stacks, Linux/Windows execution |

The synchronous .NET runtime still admits one physical callback per actor.
No await/resume API is supplied here: frame authority may not be carried into
later DATA as a substitute. Full per-actor source allocation coverage, checked
32-bit references, borrow/drop/unwind inventories and concurrent sealed-state
access remain requirements across all three backends. Native/CLR machine stack
overflow and tenant sandboxing are not proven. Tenant-VM work follows proof of
the actor-private heap and logical-stack boundary.

The C# additions implement privileged allocator/execution-context authority and
compiler/runtime frame primitives, which guest `.atto` cannot safely mint.
They do not move guest library algorithms into C# or imply compiler support for
currently rejected actor source. New surrounding infrastructure remains Rust;
the existing native probe remains a host runtime acceptance executable.
