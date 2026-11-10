# .NET verified CFG scalar activation → actor-owned native frame ABI v1

**Evidence status: source-authored; exact-head execution not yet proven.**
The arena-backed logical frame stack and `ActorScalarFrameAbiV1` already exist
on current .NET main. Draft PR #395 seals scalar type/block metadata; this
follow-up strengthens actor-local quota failure handling. The older #384 was
closed without merging and the separate native-slab stack proposed in
#385/#389/#393 remains a competing draft (tracked in issue #396). This does
not certify physical CLR stack isolation or compiler-backed AttoLang actors.

## Contract

- The *compiler* produces a bounded spill layout only from
  `VerifiedCfgFunction`. Scalar SSA IDs must be dense, immutable and have
  exact `i64`/`bool` types. V1 accepts at most 256 values / 128 CFG blocks
  and rejects helper calls or argument effects until ownership, call ABI,
  cancellation and exception unwind are proved.
- `ActorScalarFrameAbiV1` is the runtime's only proposed typed scalar
  frame encoding. It reserves **16 + 16 × slotCount** bytes within the
  actor's existing native frame stack, not a separate heap.
- Little-endian header: 32-bit magic `ATF1`, slot count, block count,
  and checked block-index PC. Each 16-byte scalar slot contains an initialization tag, an immutable
  compiler-declared type tag, six zero padding bytes, and an eight-byte
  scalar payload. An unwritten slot has initialization tag 0 and cannot be
  read or retyped; booleans must be exactly 0 or 1.
- Every access goes through a **current** actor physical-turn context and
  the exact-issued stack handle. No raw native pointer, CLR call-stack
  capture, cross-actor authority, or wire serialization. On Pop, the
  underlying actor frame stack zeroes bytes and invalidates old handles.
- A frame can hold values across physical turns, but the compiler must
  explicitly arrange checkpointing, safepoint/cancellation validation,
  suspension, resumption and LIFO cleanup. This PR does **not** do so.

## Evidence ladder

1. **Authored only:** compiler plan, typed native spill/read API, and host
   integration tests using a verified CFG layout and two actor turns.
2. **Required:** real `dotnet run --project verification/Ores.Net.Tests -c Release`
   on the exact PR head, nonzero Actions steps and logs.
3. **Required:** Linux/Windows `PublishAot=true`, executable hash, bounded
   stack probe, stdout, stderr and exit code from exact-head artifacts.
4. **Not yet supported:** compile and execute a .atto actor source whose
   nested function calls and awaits actually use this layout, with actor
   cancellation/restart/overflow isolation and shared Java/LLVM fixtures.

No compiler success, source-guest parity, or process-safe actor stack
overflow claim may be inferred from stage 1. Zero-step CI is
`RUNNER_ADMISSION_FAILURE`, never a test result.

## Terminal actor-local logical stack quota (hardening draft)

- `ActorStackBytes` and `ActorMaxStackFrames` limit frames inside the
  existing **actor-owned arena**. A valid request that exceeds the frame
  count, total stack bytes (including alignment), or single-frame stack
  capacity throws `ActorStackQuotaExceededException` and permanently
  poisons that actor's logical stack instance. **No second allocator** is
  introduced.
- A host handler is permitted to catch an exception for its own cleanup,
  but cannot resume frame reads/writes, admit more frames, or count the
  enclosing physical turn as successful. Synchronous dispatch checks the
  poison state **after the handler returns** and retires the affected
  actor. Native arena bytes are reclaimed through existing STOP and
  physical-quiescence logic. Malformed zero-size and invalid-alignment
  requests continue to fail argument validation without consuming quota.
- Managed regression covers three separate negative paths: depth limit,
  cumulative byte limit, and one oversized frame. In each case a handler
  deliberately catches the terminal exception, then verifies revoked
  frame authority. A fourth actor on the **same shared-carrier pool**
  must complete normally, with native reservation eventually refunded.
- The published Linux and Windows NativeAOT **host** probe repeats those
  three fault paths and sibling survival, requiring the exact output
  receipt `PASS actor-stack-nativeaot-terminal-quota`. This is NOT
  generated guest `.atto` CIL and cannot establish protection against
  uncontrolled physical CLR call-stack overflow.
- Release gate: source-identity and tree receipt, nonzero executed job
  steps, managed host tests, published native probe binary + SHA-256,
  independent review, and post-merge smoke verification. A GitHub
  `steps=null` or `steps=[]` job is **RUNNER_ADMISSION_FAILURE**, not
  passing or failing runtime evidence.
