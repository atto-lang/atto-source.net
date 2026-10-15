# Oreslang .NET — verified CFG-to-CIL lowering (draft)

**Status:** Implemented on a draft stacked branch. Compilation and native execution have **not** been verified. This work depends on [PR #3](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/3), which depends on [PR #2](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/2) and [PR #1](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/1).

## Implemented compiler path

`CilEmitter.EmitLibrary(VerifiedCfgFunction, ...)` delegates to `CfgCilEmitter`.
This entry point accepts *only verified CFGs*. The existing source-level
`oresnet compile` command continues to use verified straight-line arithmetic
OresIR; source-language branches and functions are **not yet supported**.

- `System.Reflection.Metadata` emits a static `OresProgram.Run(): i64`
  method, ready for build-time Native AOT compilation. No LLVM, Reflection.Emit,
  dynamic method loading, or runtime JIT is involved.
- Basic blocks receive labels before emission; a synthetic entry jump
  supports arbitrary physical block order. Every block has a terminating
  jump, conditional branch, or return. All CIL branches use long encodings
  and are resolved through `ControlFlowBuilder`.
- The local table allocates one typed local for each dense SSA result and a
  separate typed scratch local for each destination block parameter.
  `i64` uses an `int64` CIL local; `bool` uses a `bool` CIL local.
- A branch first copies **every** source edge operand into the destination's
  temporary scratch slots, then stores scratch values into the destination
  block parameter locals. This is the required simultaneous-assignment
  semantics for swaps, loop-carried values and shared incoming values.
  A true branch and false branch have separate edge transfer sequences.
- Each instruction immediately stores its CIL evaluation-stack result into
  its SSA local. The maximum stack depth is two for binary operations.
  All block entries and edge transfers have an empty evaluation stack.
- `add.ovf`, `sub.ovf`, `mul.ovf`, `div`, and `ceq` preserve checked-i64
  operations and equality. Operations execute even if their SSA result is
  unused: potentially trapping instructions are not DCE'd.
- CIL module IDs are now hashed from the **finalized method-body stream**,
  including resolved branch offsets and local signature tokens, rather than
  the pre-fixup instruction bytes.
- The verifier caps CFG SSA definitions and scratch-local counts at 16,384
  each; the method has at most 32,768 local slots under this allocation scheme.
  Emission also rejects methods with more than 4 MiB of CIL before metadata
  serialization; all destination-parameter layouts are precomputed, avoiding
  repeated linear block lookups per branch edge.

## Differential verification fixtures

`CfgCilTestInterpreter` decodes the generated CIL using Mono.Cecil in the
**managed developer-test process**, never the Native AOT release process.
It compares results and exception types with the separate
`CfgReferenceEvaluator` on:

- A loop with a backedge, including an intentionally reordered block layout.
- A diamond conditional and typed join argument.
- A conditional that bypasses an exception-throwing branch.
- A backedge that swaps two parameters (correct answer **2211**; naive
  sequential copying would corrupt the swap), plus a separate three-way
  rotation of loop-carried values (correct answer **312**).
- An unused checked division by zero and a checked integer overflow.
- Stable byte-for-byte PE emission across repeated compilations.

The Windows workflow additionally generates a verified CFG CIL fixture in a
build-time-only managed project, publishes the *static* Oreslang host with
`dotnet publish -r win-x64` under the strict Native AOT guard, checks for
managed sidecars, and executes the native binary expecting **2211**. This
test cannot be claimed passing until a runner executes the workflow.

## Verification blockers and explicit non-goals

- GitHub Actions has repeatedly failed before returning job steps, including
  independent private runner-allocation probes across four organizations
  (3FA, gha-indie-worker, messaging-intel and akrion-sim). See
  [CI recovery and no-source probes](ci-recovery.md). **No alternative
  organization has a confirmed working runner**, so private source code
  was not mirrored out of the Oreslang organization.
- The local environment has no .NET SDK; C# compilation, tests and native
  execution on this branch are **unverified**.
- No source-level CFG parser, function calls, parameters or locals yet;
  no LLVM backend; no actor scheduler or ownership/effect verification.
- The reference interpreters are test scaffolding, not proof of arbitrary
  .NET or CIL semantics.
- Do not merge before the .NET 10 managed suite and real Windows Native AOT
  fixture succeed, and prerequisite PRs are merged in dependency order.

## Follow-ups

Resolve Actions runner allocation/billing/policy or use an authorized .NET 10
build host. Fix actual build diagnostics, then extend native tests with
additional CFG shapes and branch-boundary metadata inspection. After that,
add frontend control-flow constructs, source functions and locals, and a
liveness-aware local allocator without deleting trapping operations.
