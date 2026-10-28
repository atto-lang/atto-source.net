# .NET Task/Future lifecycle hardening — 2026-10-28

Scope: [source.net PR #192](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/192),
stacked on PR #186 and current-main continuation-fence candidate #194.

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
