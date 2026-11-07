# .NET verified CFG scalar activation → actor-owned native frame ABI v1

**Status: candidate only — source-authored, not executed.** Stacked on .NET
actor logical stack PR #384. This does not certify native machine stack
isolation or compiler-backed AttoLang actor execution.

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
