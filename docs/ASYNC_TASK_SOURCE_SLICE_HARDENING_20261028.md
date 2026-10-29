# .NET Task/Future lifecycle hardening — 2026-10-28

## Main integration and CI admission reconciliation

On October 28, 2026, PR #192 was integrated with the newer `main` by a
two-parent Git merge on the **draft feature branch only**, then retargeted
from the already-merged #186 branch to `main`. This preserves current-main
structured CLI commands, module compilation, strict NativeAOT project flags
and every existing managed test registration. PR #194's continuation fence
and PR #186's host observation base are already in `main`; no duplicate
implementation was overlaid. Exactly the ten PR-specific files differ
against the integration base; the branch must remain current with `main`
before review.

Additional native negative checks now cover `check`,
`check-structured`, `compile-structured`, `emit-csharp` and
`emit-fsharp` to prevent new compiler entry points from accepting
unsupported Task/Future guest source. The workflow requires the
`NET_ASYNC_ADDITIONAL_COMMANDS_REJECTED` trace. This expands the
negative checks from 5 source grammar cases + 8 legacy compiler cases
to include 5 additional command-surface rejections. The new checks
still require an actual executed Linux and Windows runner; static
reconciliation is not runtime validation.

**Important:** The exact source commit/root tree change with every
patch. The previous issue #220 candidate (head
`6c358611addb901e041c5f5cd09ea0ec532f511f`,
tree `eb77cb077f1cd6f53a9a50a2dff6698b25cc2e6d`) has been
superseded and must not be used to authorize source transfer. The
current proposal must be independently re-reviewed before any private
cross-organization mirror. The in-source runner remains the preferred
verification route.


Scope: [source.net PR #192](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/192),
retargeted to main, with merged dependency PRs #186 and #194 preserved.

## Follow-up hardening: admission caps and failed allocations

The bounded scheduler now rejects producer or observer capacities outside
`1..4096`, including `int.MaxValue`. This is an *absolute* host-side
resource ceiling, not merely a dynamically selected caller quota.

Observer forwarding registration allocates its next drain witness **before**
incrementing `pendingObserverForwarders`, preventing allocation failure from
stranding a nonzero count or obstructing physical shutdown. `FromTask` also
takes the same admission lock as shutdown, so a conversion cannot succeed
after close has linearized. New managed regressions reject illegal caps,
accept 4096, and reject producer-to-Future conversion after closure.

The static Git-blob/source assertions for these changes succeeded.
**No C# compiler, managed runtime, or NativeAOT job has executed this updated
head yet.** A source-free model or an earlier carrier run cannot be used as
execution proof for these C# changes.

### Alternate runner policy

Private carrier `honeypot-r-us-test/hnpt-e2e`, immutable repository ID
`1346492776`, has real executed source-free jobs (7 and 8 successful
steps) and earlier Linux/Windows NativeAOT executions. That establishes
runner **availability evidence**, not an org budget balance or transfer
authorization. The authoritative Oreslang source policy still has no active
destination/snapshot approval and an independent reviewer disabled.
The current immutable commit/tree/destination must be separately approved
through protected source governance before any cross-org Git-tree mirror.
Any new commit invalidates an older candidate snapshot. Do not repurpose
a prior receipt or transfer unapproved private source to a test repository.

## Audit findings and repairs

| Finding | Risk before patch | Repair | Verification |
|---|---|---|---|
| Concurrent `DisposeAsync` | Second disposer returned immediately while producer remained live | Both callers join a single `TaskCompletionSource` drain witness. Closing the scope and physical producer retirement remain distinct | New blocked-producer two-disposer regression; runtime execution pending exact-head CI |
| Cancellation exception origin | A user-thrown `OperationCanceledException` could be counted as authentic if scope cancellation happened to race | Require both `producerStop.IsCancellationRequested` and the *exact* `producerStop.Token`; foreign/tokenless OCE remains faulted | Foreign-token OCE after cancellation, plus genuine token cancellation regressions |
| Producer/observer admission | Allocation failure after quota reservation could leave a permanently consumed slot | Reserve then roll back on every failed producer identity/allocation/queue publication and observer projection allocation | Source invariant audit; allocation-failure injection not yet executed |
| Producer identity | Unchecked signed `Interlocked.Increment` could wrap and reuse identities | CAS-based bounded monotonic positive identity; reject at `long.MaxValue` | Source invariant audit; forced near-overflow test not yet executed |
| Pump self-join | Producer could await disposal of its *own* serialized lane and deadlock | Task-scope `AsyncLocal` execution guard; fail closed on self-dispose before changing state | Self-join test |
| Continuation quota races | Single-threaded test did not prove concurrent admission bound | 64 concurrent observer attempts for 4 permits; assert exactly 4 admissions and 60 rejects, no replay | New managed regression; execution pending |

| Scope drain vs observer forwarding | Producer pump completion could race with still-running result projections; scope disposal was not a full adapter quiescence witness | Serialize producer/observer admission with scope close; track forwarding independently of quota; join both pump and all admitted forwarding completions | 32-round four-observer drain test, plus 128-way Start/Dispose contention test; execution pending |

**Scope shutdown invariant:** once the admission lock marks the scope closed, no
new producers or observer forwarders can be admitted. Successful disposal
requires the producer pump to drain **and** the last admitted observer
forwarder to publish its result and exit. This does not imply actor scheduler
affinity or generation fencing.

**Export authorization read at this revision:** source
`.github/private-ci-approved-repositories.txt` and
`.github/private-ci-approved-source-snapshots.txt` have **zero active entries**;
`.github/private-ci-independent-reviewer.json` has `enabled: false`.
Therefore the previously used test-carrier workflow **must not** be reused
for the updated private source until an independent, source-governed
authorization is recorded. A user instruction to test does not bypass this
repository security policy.

The old verified run 40536637344 covered source commit
`0c7b1f54e2cf6ca10d33d79c97903bbe3ef86b02` only. It does **not** certify
the newer head of this PR, which also contains the #194 continuation-fence
candidate integration. Do not copy prior pass badges to the new head.
Source-org zero-step Actions attempts are `NOT_EXECUTED`.

## Deliberate limitations

This is a trusted-host, bounded C# scheduling substrate and one literal-only
source interpreter command. The general CIL compiler still rejects
`Future.from_task(task { ... })` because verified guest async HIR/MIR,
`RunAsync` CIL emission, actor lane continuations, borrow/ownership checks,
Send/Sync, supervisor panic delivery and generation-aware runtime retirement
are absent. Do not describe `run-async` as generated guest binary execution.

The select continuation fence is a separate candidate helper. It is **not**
integrated into this OresTaskScope scheduler; producer retirement is not an
actor-generation or arena-reclamation witness. An uncooperative producer may
delay `DisposeAsync` indefinitely. This is safer than falsely declaring
quiescence; higher-level supervisors need timeout/escalation without freeing
referenced memory.

An observer view does not own producer cancellation. Producer shutdown is
authorized by its owning scope. Tokens are not serialized to guest code.

## Required exact-head execution

Run on both Linux x64 and Windows x64 at the same source commit and tree:

```powershell
dotnet run --project verification/Ores.Net.Tests -c Release
dotnet publish compiler/Ores.Net.Compiler -c Release -r linux-x64 -p:PublishAot=true --self-contained true -o artifacts/async-compiler
./verification/assert-async-source-slice.ps1 -Compiler ./artifacts/async-compiler/oresnet
./verification/assert-async-interop-admission.ps1 -Compiler ./artifacts/async-compiler/oresnet
```

Use `win-x64` and `oresnet.exe` on Windows. Require nonzero executed job steps,
post-run exact-tree/raw-blob verification, actual native binaries, binary
SHA-256, negative diagnostic traces, value 42, no unverified generated CIL,
and explicit actor/interop parity=false receipts. Pending checks remain
pending, not PASS.

Private-source cross-organization mirroring additionally requires the
independent, repository-governance approval for *this* updated source head;
prior mirrors do not grant perpetual export authorization. Leave PR #192
**DRAFT**, and do not merge until source-owned and independently reviewed
checks are satisfied.
