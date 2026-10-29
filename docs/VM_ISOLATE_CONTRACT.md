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
