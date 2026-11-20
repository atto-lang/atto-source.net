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
- `System.Text.Json` serialize/deserialize paths for issued turn and frame capabilities are explicitly denied via converters and NativeAOT-safe source-generated negative tests; unrelated codecs must enforce their own no-wire rejection at the broker.
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

## 2026-11-20 adversarial hardening audit (still host-only)

**Physical carrier authorization:** the actor turn now captures the issuing
managed thread ID. Even a real issued token cannot read/write frames or
complete a turn from a second carrier while the first turn is live. The old
turn remains valid on its issuing carrier. After its proper completion, a
**new** physical carrier may start a new turn and recover the same sealed
actor-owned logical frames. This prevents accidental cross-thread authority
transfer without blocking legitimate cross-dispatch migration.

**New authored negatives and stress cases:** off-carrier read and EndTurn
rejection; replayed EndTurn rejection; separate-carrier frame recovery;
256 consecutive 127-operation dispatches (32,512 reductions, 254 polls);
zero/oversized charge and invalid-alignment atomicity; tenant disposal
during an active physical turn; and 240 actor alloc/use/reclaim iterations
across four concurrent host threads. All remain **NOT_EXECUTED** pending
actual compiled .NET CoreCLR and Windows/Linux NativeAOT runs.

**Critical limits:** a valid host-issued turn capability is currently the
only caller authority. Logical frames and fuel are not inserted into emitted
guest CIL, cannot interrupt arbitrary native code, do not prove physical
stack-overflow containment, and do not enforce critical-section or borrow
checks until the verified actor scheduler/IR supplies that context. Invalid
compiler charge weights throw atomically, but the trusted compiler MUST
reject them before emitting guest code; this host-only API is not a
guest-callable bypass.

**Private CI hold:** protected-main
`.github/private-ci-approved-repositories.txt` contains **zero active
authorized destinations** on this audit. No source code may be transferred
to another GitHub organization solely because a private carrier exists.
[CI authorization/admission issue #34](https://github.com/atto-lang/atto-source.net/issues/34)
requests source-runner remediation or independent approved-destination
authorization. No cross-organization mirror was created by this audit.

### Sealed scalar ABI raw-access guard (security hardening)

Once a frame has a sealed scalar schema, **typed i64/bool APIs are the only
allowed payload accessors**. Untyped `ReadFrame` or `WriteFrame` with nonempty
buffers causes terminal actor failure, checked again by `EndTurn` even if
the caller swallows the first exception. Pre-seal raw byte initialization is
permitted, but `SealScalarSchema` validates canonical boolean slots
immediately (tag 0 or 1 and seven zero padding bytes), before publishing
immutable scalar-kind metadata. Separate adversarial cases exercise denied
raw writes, denied raw reads, and noncanonical raw pre-seal initialization.
This prevents bypass of typed scalar inspection; it is still **trusted-host
code only** and does not supply a guest sandbox.

Frame alignment is an **arena-relative logical offset** requirement (1–64
power-of-two), not an alignment guarantee for the underlying native pointer,
which is never exposed. Consumers must use the provided byte-wise typed
accessors rather than reinterpret native memory as raw vector/struct
pointers.

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


## 2026-11-20 CI admission and tenant-fuel verification cycle

### Security corrections

1. **Managed frame metadata cap:** the effective number of frame-handle slots
   is now `min(configured_maximum_frames, actor_arena_bytes)`. Since every
   logical frame requires at least one native byte, a 64-byte actor can no
   longer create a 1,024-entry managed handle table while only charging
   64 native bytes. New regression saturates 64 one-byte frames and asserts
   terminal quota/refund on the 65th. Managed metadata is still not a
   separately metered resource; host process-wide quotas remain future work.
2. **Nonrenewable tenant-wide fuel:** the `TenantVm` now owns an additional,
   bounded `maximumTotalLogicalFuel` budget. Each authenticated actor charge
   checks local actor fuel before atomically debiting its parent tenant's
   persistent budget. Neither STOP, actor reincarnation nor a dispatch
   handoff restores spent tenant fuel. Invalid weights and already
   exhausted actors cannot debit the tenant budget. The new negative case
   charges one actor twice across two incarnations, then denies a third
   actor despite its fresh local fuel. Admission can still create actors
   when tenant fuel is exhausted; guest *execution* charges fail terminally.
3. **Source workflow integrity:** the earlier PowerShell source-index
   splitting error is corrected to a single-backslash regex `'\\s+'`.
   Source files must be tracked regular Git blobs (mode `100644`), and
   `git hash-object --path=...` uses Git's clean filters so LF blob identity
   is checked correctly against CRLF Windows working trees. The checkout
   and setup-dotnet actions are pinned to the existing source-main
   reviewed revisions, not moving tags. These checks are only meaningful
   once the workflow genuinely runs.

### Exact-head execution attempts and independent review

- Updated the branch-local `push` trigger to request source-repo CoreCLR
  and Windows/Linux NativeAOT jobs without exporting source.
- Created the repository's **source-free runner admission** branch
  `ci/source-free-runner-admission-20261027` from `main`, targeting the
  preexisting source-free canary workflow. It does not check out code.
- GitHub returned **zero workflow runs** for both branches on the latest
  checks. No assigned-runner execution evidence and no negative compiler
  result exist for this revision.
- Local environment lacks `dotnet`; external SDK retrieval was
  unavailable. Accordingly all actual C#/NativeAOT tests remain
  **NOT_EXECUTED**, not PASSED.
- CODEOWNER `@ORESoftware` was requested as a PR reviewer and assigned
  [CI/security issue #34](https://github.com/atto-lang/atto-source.net/issues/34).
  The reviewer request is not an independent approval; no security review
  has been submitted.
- Source protected-main destination allowlist remains **empty**. Never
  mirror private source to an alternate org until exact destination and
  snapshot approval is independently recorded there. A prior private
  carrier run from a different Atto PR does not constitute authorization.

**Release hold:** no ready-for-review promotion, merge, production
tenant-sandbox certification, untrusted guest admission or compiled `.atto`
actor claim until exact-current-head real managed and Linux/Windows NativeAOT
execution, independently reviewed unsafe/concurrency behavior, and all
GitHub governance checks pass.
