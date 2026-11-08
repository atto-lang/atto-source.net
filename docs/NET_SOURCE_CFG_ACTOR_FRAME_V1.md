# Verified .atto CFG on actor-owned native logical frames — integration v1

Status: **source-authored, NOT_EXECUTED on a .NET SDK or NativeAOT runner**.
This is the first direct bridge in verification between the actual .atto
structured compiler's verified CFG and the existing actor runtime's
generation/turn-scoped guest-frame API.

## Actual integration path

1. The canonical `.atto` fixtures in
   `contracts/actor-scalar-execution-v1/` are read as real source bytes.
2. `OresStructuredCfgLowering.Lower` invokes the compiler frontend and CFG
   verifier. `CfgActorScalarFramePlan.Create` validates scalar-only, dense
   SSA layouts and bounded frame size, type tags, block count and entry PC.
3. The test-only executor `ActorCompiledCfgFrameSelfTest` interprets **the
   verified compiler CFG**, never raw source text, against memory admitted by
   `ActorContext.GuestPushStackFrame`.
4. Header, typed i64/bool SSA slots, program counter, checked scalar ops and
   simultaneous CFG edge transfers live in the actor-owned bounded native
   frame. `ActorScalarFrameAbiV1.ReadI64` independently validates the return
   value using the existing typed frame ABI.
5. Returning guest handlers pop all guest frames. Failed operations, leaked
   guest frames and quota breaches remain terminal under the dispatcher
   `ValidateGuestFrameExit` contract.

The host test distinguishes a compiler-verified scalar workload from a
compiled **actor declaration**. It is not dynamically executable .NET IL
guest code, NativeAOT guest code, a Cranelift backend, coroutine suspension,
or an unsafe source-level function capability crossing an actor mailbox.

## Acceptance fixtures and negative controls

| Fixture | Source behavior | Evidence sought |
| --- | --- | --- |
| `scalar42.atto` | `19 + 23` | Actor B computes 42 via verified CFG and typed native slots |
| `loop42.atto` | Loop and return 42 | Replacement actor returns 42 with CFG edge/PC handling |
| `fuel-denial.atto` | Nonterminating source loop | Finite interpreter fuel terminates only this actor |

A separate actor A exceeds a three-frame guest stack limit; catching the
quota exception must not clear its terminal fault. A sibling actor B still
completes with `42`. A retained old `ActorContext` is rejected after
physical-turn expiry, and a new actor attempting to read the old
`ActorStackFrameHandle` must itself be faulted. The replacement actor then
executes the compiled loop source and returns `42`. Final STOP must
release the runtime's entire private native reservation.

The test uses a **new** `ActorRuntime.Spawn` reference for the replacement.
It does not claim stable-ID supervisor restart. Proper actor generation
reissuance with stable IDs remains a separate required runtime change.

The source-local managed CI run must include exactly:

`PASS net-source-cfg-actor-frames-v1|source=3|faults=3|peer=42|loop=42|fuel=bounded|native_refund=1|guest_machine_code=0`

This is a **managed verification harness receipt**, not a NativeAOT compiled
guest result. The existing NativeAOT probe remains separately gated in the
same workflow. A missing output marker, a `steps=null` job, a failed
compilation, or an independent-review gap must keep the PR in draft.

## Next engineering step

Replace the test interpreter with compiler-emitted actor-frame access and
return/branch/continuation lowering that does **not** consume unbounded
CLR/native carrier recursion. Build a real .atto `define actor` fixture,
then reproduce the same quota-overflow/peer-survival/stale-generation result
with compiled guest code under Linux and Windows NativeAOT. Only then
extend the canonical fixture to Forge/Cranelift, LLVM, Java/GraalVM and Wasm.
