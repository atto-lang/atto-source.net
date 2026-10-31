# OresVM / IsolateVM contract v0 (backend admission)

**Status: experimental host substrate.** Not full actor VM execution,
complete cross-backend parity, per-tenant physical resource enforcement or
a security sandbox. No general guest-code tenant isolation is claimed.

## Common vocabulary

- **OresVM:** root host, code registry, permission broker, global admission
  and tenant supervisor. It must not own tenant-writable guest module globals.
- **IsolateVM:** an OresVM-owned execution domain for one tenant incarnation;
  owns private mutable globals, heap/resource limits, actor registry and
  quiescence accounting. Reusing a tenant ID creates a new generation.
- **Actor:** individually scheduled concurrent unit inside exactly one
  tenant's IsolateVM, with sealed state, owned arena and mailbox.
- **Isolate execution context:** a versioned *borrowed entry capability*;
  never a raw writable pointer to the OresVM root or another tenant.

## Required lifecycle invariants

1. Created -> Running -> Quiescing -> Stopped -> destroyed.
2. Reject duplicate tenant IDs, unbounded admission, invalid quotas,
   stale/foreign/forged handles and actor ID replay after reincarnation.
3. Closing admission during quiescence must be atomic with new guest,
   actor, mailbox, callback and I/O registration accounting.
4. Complete shutdown only after guest calls and all actor,
   continuation, callback, mailbox-loan and I/O retirement have drained.
   Accounting is insufficient without physical OS/worker retirement.
5. Cross-isolate messages must be versioned and serialized or explicitly
   transferred by checked ownership. No borrowed raw pointers cross tenants.
6. A shared immutable AOT module may have different *tenant-owned*
   writable globals. Neither mutable module state nor user symbol identities
   silently become process global.
7. Include a distinct status/result ABI, including legitimate signed
   negative values. Check native artifact entry ABI **before** function
   pointer casts; ABI version mismatch must fail closed.
8. An in-process isolate is **not an OS security sandbox**. Native/FFI
   arbitrary pointers require process-level separation for hostile tenants.

## V8 lessons and crucial differences

V8 separates an isolate's engine/heap from its Context globals and supplies
host platform scheduling and explicit entry/handle scopes. Oreslang should
borrow explicit context ownership, generation-fenced handles, snapshots
for immutable initialization and fast tenant-specific state, **not**
the assumption that a V8 Isolate is equivalent to a renderer-process
security boundary. Oreslang must eventually schedule independently sealed
actors within one tenant concurrently, rather than imposing one
entering thread for the whole tenant. Pointer compression changes memory
representation, not authorization. In-process host admission is insufficient
for arbitrary untrusted native machine code.

Architecture reference: LLVM draft PR #1158,
`docs/ORESVM_VS_ISOLATEVM.md` in `oreslang-source.llvm`.

## .NET Native AOT implementation

`OresVmHost` is an ordinary statically linked C# runtime host whose
`IsolateHandle` and `ActorTicket` identities are checked under a lock.
It uses the repository's real `ActorMailbox` per actor, with
capability-checked byte copies and pending-envelope zeroing on retirement.
Resource pins are explicit host bookkeeping, **not** proof of physical
OS/FFI callback retirement. No user source compiler lowering to a per-tenant
entry ABI is provided. Do not rely on `AssemblyLoadContext`, reflection
emit, runtime CIL generation or process-global mutable statics to isolate
Native AOT tenants. For hostile/native tenant execution, use separate
sandboxed worker **processes**.

Tests: `VmIsolateHostTests.Run()` is wired into the existing
`verification/Ores.Net.Tests/Program.cs` suite; the .NET toolchain must
execute this in CI. No local dotnet SDK execution was available in this pass.

### Actor-local logical turn admission

`ActorTicket` now owns at most one generation-fenced `ActorTurnTicket`.
`BeginActorTurn` admits one user handler; `SuspendActorTurn` keeps that
logical turn active while releasing any physical carrier outside this host
bookkeeping API. A later user turn is rejected until the exact same ticket is
resumed and completed. Cancellation is also a terminal, exact-ticket
transition: it clears actor admission once, fences that generation, and makes
later resume/complete/cancel replay fail closed before a fresh generation may
start. Foreign actor/tenant tickets, stale completed/cancelled tickets,
generation replay and actor retirement with an active turn fail closed.

This is intentionally a host substrate, not source-language actor lowering.
It does not hold a CLR monitor across guest suspension, does not make
`Task`/delegate behavior Oreslang semantics, and does not yet connect
`handle_state`, source `await`, cancellation cleanup or generated
continuation frames. Those future paths must preserve the same rule: suspension
may free a carrier to execute **other actors**, but it cannot admit a different
user continuation into this actor.


## Native AOT compiled CIL tenant execution (follow-up)

`OresVmHost.InvokeGeneratedScalar(handle, Func<long> entry)` now admits
a trusted **statically linked generated-CIL entry**. It pins the
in-flight guest call (including queued turns), serializes within one tenant,
rechecks admission before execution, calls the generated CIL method without
holding the root VM lock, and uses checked accumulation into tenant-local
i64 state. Exceptions and arithmetic overflow do not commit a result.
Quiescence blocks new entry but permits an already-running guest call to
finish, and shutdown waits until all entry calls retire.

The NativeHost has an opt-in `--isolate-smoke` mode using
`Oreslang.Generated.OresProgram.Run` from **examples/hello.ores**, which
returns 14. It executes the same generated CIL function for two independently
mutable tenants, verifies restart fresh state, and ensures the neighbor can
continue after tenant A shuts down. It runs as a **Native AOT binary**, not
a framework-dependent dynamic assembly. Invoke either:

- Windows: `./scripts/publish-windows.ps1 -Source examples/hello.ores -Rid win-x64 -TenantIsolateSmoke`
- Linux: `ORES_NET_ISOLATE_SMOKE=1 bash scripts/publish-linux.sh examples/hello.ores`

The ordinary host invocation remains unaffected. The existing C# test driver
adds nested-entry denial, rollback on exceptions/overflow and a physical
blocked-guest shutdown pin using a concurrent Task.

**Limit:** current compiler-generated CIL has no tenant-bound parameters;
the statically linked function returns a scalar constant which the host
accumulates in tenant state. This proves an actual compiled source entry can
execute under tenant isolation accounting, **not** full mutable .ores
module globals, actor scheduling, hard realtime, or a CLR/OS security
sandbox. Exact-head Linux and Windows Native AOT publishes have NOT
executed locally; no .NET SDK is installed here.

## Scoped host-owned actor-state loans (2026-10-30)

`ActorTurnTicket` now supports bounded (at most 32 nested) `ActorStateLoan`
scopes. Only the active, unsuspended logical turn can mint a loan, and nested
critical scopes temporarily fence the parent loan. The innermost loan must
be returned in LIFO order; a released loan can never validate again. Writes
require a writable ticket, and `ValidateActorStateLoan` checks exact actor,
isolate, logical turn and loan identity. Future generated state operations
must enforce this check on each access; holding a token alone is insufficient.

Both `SuspendActorTurn` and `CompleteActorTurn` reject outstanding loans.
After physical carrier suspension, a resumed continuation must request a
fresh state loan. Terminal cancellation revokes the entire bounded nested
loan chain before making the actor lane available for its next user turn.
Cancellation and restart never revive old loan objects or turn tickets.

This is **host admission only**, NOT executable `.ores` actor state lowering,
a native per-actor heap, or a verified scheduler/continuation implementation.
A source compiler must still generate and type-check actual compiler-owned
state storage, reject escaped state capabilities, enforce effect rows,
and connect resumption to generation-fenced scheduler continuations.
Exact-head Windows/Linux NativeAOT builds and adversarial tests remain required.

## Typed actor-local scalar cells (2026-10-30)

The host now supports a **bounded per-actor scalar state layout**, installed
once via `ConfigureActorState` before the first actor turn. The permitted
field kinds are `Int64` and `Boolean`; each instance receives separate
typed defaults and at most 16 fields. Empty/oversized schemas, unknown kinds,
mismatched literal kinds, duplicate installations and cross-isolate actor
tickets are rejected. This host API takes compiler-provided field indices,
**not** guest-controlled mutable references or dynamic key lookup.

`ReadActorStateInt64`, `WriteActorStateInt64`,
`AddActorStateInt64`, `ReadActorStateBoolean` and
`WriteActorStateBoolean` validate the exact current actor-turn state loan
and access the underlying cell **inside one host lock**. The checked
`AddActorStateInt64` commits only after overflow checking succeeds.
Read-only or retired loans cannot write; nested loans fence outer tokens;
suspended/cancelled/completed turns cannot access cells. Actor retirement
releases its private cells and a new isolate incarnation starts fresh.

`VmActorStateCellsTests.Run()` exercises type/index rejection, defaults,
per-actor/per-tenant isolation, checked increments and overflow rollback,
nested scopes, suspension, cancellation, and restart. It is wired through
`VmIsolateHostTests.Run()` in the existing verification program.

**Scope and release gate:** this is a trusted **host storage primitive**
for future generated code, not yet an executable source-level
`self.handle_state(critical |s| -> ...)` lowering. An actual compiler must
bind immutable source field names and types to layout indices, prove effect
rows and nonescaping state borrows, and emit checked accesses through this
API while maintaining scheduler-owned logical single-flight execution.
The primitive's lock does not establish guest scheduler non-reentrancy by
itself. No NativeAOT success claim is valid until the exact PR head compiles
and runs on real Linux and Windows runners with nonzero executed steps.
