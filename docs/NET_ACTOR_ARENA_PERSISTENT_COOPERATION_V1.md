# Atto .NET: one actor arena and persistent cooperation budget (v1 candidate)

**Status:** host runtime substrate and adversarial test source only. It does **not**
enable compiled `.atto` actors, guest `await`, actor isolation against arbitrary
CLR/native guest code, physical machine-stack overflow recovery, or a production
multi-tenant security boundary.

## Selected resolution

The owner explicitly selected these two behaviors on 2026-11-20:

1. **Single actor-owned arena-backed logical stack.** A `TenantVm` accounts
   actor arenas and admits bounded capacity. Each `ActorArena` is the **sole
   native allocation** for its incarnation; every logical frame is an offset
   and exact-issued object capability into that arena. There is no second
   `ActorFrameStack` slab allocator and no pointer escape.
2. **Persistent metering.** The 128-unit poll phase and hard fuel survive
   every physical dispatcher turn. Only per-turn dispatch fairness is reset.
   Compiler charges have verified positive weight <=128. Poll eligibility
   never, by itself, grants suspension under critical/borrow/lock guards.

## Explicit legacy reconciliation

| Legacy Oreslang PR | Disposition |
| --- | --- |
| [#384](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/384) | **Incorporated architecturally**: existing actor-private arena for frames; PR was closed without merge |
| [#385](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/385) | **Superseded allocator**: independent extra native frame slab is NOT ported; exact-issued turn/frame and terminal fault principles retained |
| [#389](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/389), [#393](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/393) | **Partially incorporated security semantics**: physical-turn, typed and continuation principles only; actual continuations/spill CIL are unimplemented |
| [#395](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/395) | **Incorporated**: immutable sealed scalar-kind/block authority |
| [#453](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/453) | **Incorporated**: terminal, unswallowable quota failure checked on dispatcher exit |
| [#401](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/401) | **Partially incorporated**: inexpensive 128-unit checkpoint concept; resetting phase at BeginDispatch is explicitly rejected; opt-in CIL metering remains future work |
| [#404](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/404) | **Selected**: 128-unit maximum weighted charge, persistent phase/fuel and per-dispatch fairness reset |

The legacy projects have other branches; this document does not close or
rewrite any legacy PR. The Atto implementation is new, intentionally small
and does not copy the legacy C# runtime into the current direct-CIL compiler.

## Trusted-host API and resource lifecycle

- `TenantVm.CreateActor` checks tenant maximum actors, total native bytes,
  per-actor bounds and initial fuel before publishing any authority. The
  allocation is zero-filled. Native memory is never aliased across actors.
- `BeginTurn` issues an exact actor-owned token. Each native access and
  metering charge validates that token and actor liveness. A second active
  admission poisons that actor. Old turn and other-tenant tokens are invalid.
- `PushFrame` aligns to 1..64 power-of-two and allocates within the **same
  actor arena**. `PopFrame` enforces LIFO and clears bytes. Old or
  cross-actor frames fail exact-reference authorization.
- `SealScalarSchema` snapshots the verified scalar types (i64/bool) and CFG
  block count into immutable host-authoritative metadata. Mutable raw bytes
  cannot retype the schema. Booleans occupy 8 bytes and must be exactly 0 or
  1 with seven zero padding bytes.
- Terminal stack quota, noncanonical bool, reentrant dispatch or depleted
  fuel mark only the offending actor terminal. `EndTurn` is mandatory in the
  dispatcher's `finally` path and rethrows terminal failure even if a
  handler caught the original exception.
- `Stop` revokes access immediately but physical memory is released only
  after its active physical turn quiesces. Release zeroes all bytes and
  refunds exactly once. A stuck native call can delay teardown indefinitely:
  host-level liveness and OS isolation are separate responsibilities.
- Remaining fuel and 128-unit phase are actor-incarnation data. BeginTurn
  resets **only** BackedgesThisDispatch. A 127-iteration dispatch loop
  cannot avoid later polls by continually yielding before step 128.

## Isolation boundaries and no-claims

This is **not** an arbitrary-code sandbox. The trusted .NET runtime and
untrusted native/CIL guest code share process address space if run together.
The host must not supply an `ActorArena`, `ActorTurn`, `ActorFrameHandle`,
CLR delegate, native pointer or a mutable managed reference to untrusted
code. Cross-tenant messages still require a separately verified broker,
capability protocol, source-compiled guest ABI and execution provider.
Future interpreter/compiler changes must not silently invoke this class
from unverified guest input. The current scalar direct-CIL compiler keeps
actor/suspension syntax rejected.

## Verification and release hold

Standalone .NET 8 executable:
```sh
dotnet run -c Release --project verification/Atto.Net.ActorRuntime.Tests
dotnet publish verification/Atto.Net.ActorRuntime.Tests -c Release -r linux-x64 \
  -p:PublishAot=true --self-contained true -o /tmp/atto-net-actor-native
/tmp/atto-net-actor-native/Atto.Net.ActorRuntime.Tests
```

The same test source is meant to execute on Windows x64 published NativeAOT
under a real Windows runner. Required receipt:
`ATTO_NET_ACTOR_ARENA_COOPERATION_PASS cases=...`.

The new workflow must verify the exact Git SHA and clean checkout, require
nonzero executed runner steps, compile with warnings as errors, run managed
tests and execute the **published** native program on both OSes. The authoring
environment has no `dotnet` SDK; absent executed receipts are
**NOT_EXECUTED**, not PASSED. Before enabling any production guest or closing
existing ready PRs: independently review C# unsafe memory accesses, compile
real guest spills/calls from verified CFG, preserve critical/borrow rules,
prove physical teardown, and obtain genuine multi-tenant isolation evidence.
