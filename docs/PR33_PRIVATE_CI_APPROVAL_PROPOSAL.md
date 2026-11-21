# PR #33 private-CI authorization proposal — review only

> **Separation of duties — 2026-11-21:** The manual-only,
> exact-SHA source-local CI executor was moved into its own
> [draft PR #197](https://github.com/atto-lang/atto-source.net/pull/197)
> at commit `8548a51d36ce2d4cf429e2074e79b1f3c3c3956e`.
> This #118 PR retains only review documentation and **commented, inactive**
> destination/snapshot candidates. PR #197 touches one workflow file, has
> no automatic triggers and remains inactive pending independent review,
> protected-main governance and actual ephemeral runner registration.
> The two PRs are **not** a combined approval to export private source.
>
> **NON-ACTIVATING DRAFT.** This document and the adjacent commented policy
> candidates are NOT permission to export private source. Do not remove
> comment markers or publish source Git objects on the strength of this PR.
> The private CI policy remains in deny-all mode until the independent reviewer
> and enforceable protected-main requirements are verified and a later,
> separately reviewed activation change is approved.

## 2026-11-20 freshness and runner-capacity addendum (still NON-ACTIVATING)

**The original PR #33 snapshot below is stale and MUST NOT be used for a transfer.**
Current [Atto .NET #33](https://github.com/atto-lang/atto-source.net/pull/33)
is at source head `a99e6c7ad3de618196f5919e1d771408b05c3cf1`,
root Git tree `6da910678f764a1c97a1051762d2121b3f2745ff`.
These are **review inputs**, not an approved private-code snapshot.
The commented `embedded-alerts-test` destination entries below are inert and stale.

A **different, newly qualified source-free runner-capacity candidate** is
`scintilla-run-test/atto-ci` (private repository ID `1458349760`).
Its [no-checkout draft workflow PR #1](https://github.com/scintilla-run-test/atto-ci/pull/1)
on exact head `fb10390543f930ae05e8d8266c6ddc99aa827ec2` was executed in
[Actions run #44860216954](https://github.com/scintilla-run-test/atto-ci/actions/runs/44860216954):
both the Linux and Windows jobs built and ran a **generated, generic** C# program
on managed CoreCLR and published/executed .NET 10 NativeAOT binaries, using
SDK 10.0.404. Actual steps and binary hashes were observed in independently
fetched GitHub job logs:

| Source-free job | Job ID | Result | NativeAOT SHA-256 |
| --- | --- | --- | --- |
| Linux x64 | `136711629673` | PASS | `38B6921F2EC5A1580F9942E94161CB3F85D52A30FF6E0BA2078870E7225585A8` |
| Windows x64 | `136711629421` | PASS | `6F5394C1C0A3C851AA15DC27E284EC1212C36480E35CA4E1996E9A8442C1B4C3` |

This proves **toolchain/runner capacity only**. The no-checkout probe did not
copy or execute Atto source. The candidate carrier still uses a temporary
`ci/...` default ref and has no security-approved immutable carrier base;
its workflow draft is not itself an authorized source destination. In addition,
the source repository has **zero active destination/snapshot allowlist entries**
and the independent-reviewer policy is disabled. Nothing in this document is
authority to change those controls, remove comment markers, mirror a private
Git tree, infer compiler correctness, or merge PR #33.

Independently verify the third reviewer and protected `main`, approve an
immutable trusted carrier workflow/base and a narrowly scoped transfer plan,
then separately approve a **fresh** exact source head/tree in the protected
policy. Require per-blob read-back before any proposed test execution. Prefer
source-local CI if its runners become functional.

---

## 2026-11-21 source-free alternate runner — executed Linux and Windows NativeAOT (NO SOURCE EXPORT)

This is **runner and synthetic algorithm capability evidence only**, not
`atto-source.net` CIL compilation or real actor-runtime verification.

- Private carrier: `scintilla-run-test/atto-ci`, numeric GitHub repo ID
  `1458349760`. Draft [PR #1](https://github.com/scintilla-run-test/atto-ci/pull/1)
  at head `c01d1fa5ed6bbbb8f19a23a5e74bdda4f2776303`, root tree
  `20d08857c377cb86e8a175344acbef9427495e72`.
- New synthetic-only workflow Git blob:
  `b7393f78451ebd7407742d9210a265fbd931c7c6`,
  `.github/workflows/atto-net-sourcefree-actor-fuel.yml`. It has
  **no source checkout**, cross-org request, credentials, or source artifacts.
  Permissions are **`{}` (no GitHub token scopes)**. It triggers on
  **one branch push only**, not twice via both PR and push. Runs generated
  original C# under `RUNNER_TEMP` on Linux and Windows; its budget
  algorithm is an independent synthetic canary, NOT an Atto implementation.
- [Latest hardened run #44920081679](https://github.com/scintilla-run-test/atto-ci/actions/runs/44920081679):
  **2/2 real jobs PASSED**, one Linux x64 and one Windows x64.
  Both compiled and executed the source-free C# model under CoreCLR **and**
  published/executed native binaries using .NET 10 NativeAOT.
  On **each platform**, CoreCLR and NativeAOT printed
  `SOURCE_FREE_ACTOR_ACCOUNTING_PASS checks=531 source_export=NONE`
  with exact equal receipts. The 531 count includes 511 iterations of
  an individual fairness assertion; it is **not 531 independent scenarios**.
  Each log prints `NO_ATTO_SOURCE_COPIED=true`.
- Native binary SHA-256 receipts at this exact run:
  Linux x64 `B73495A8F7B2A298F00E970E96900D9997D66711EA1E8019184BBF11ACEBABE8`;
  Windows x64 `A41EEB38ACB6B46731D7358C32511A2D16DA9729D4C169D826B62EA78C413ADC`.
- Two earlier executions of the same synthetic canary also had 2/2
  stepful Linux/Windows jobs pass at
  [run #44919868070](https://github.com/scintilla-run-test/atto-ci/actions/runs/44919868070)
  and [run #44919872998](https://github.com/scintilla-run-test/atto-ci/actions/runs/44919872998).
  Original source-free .NET10 toolchain run
  [#44919873018](https://github.com/scintilla-run-test/atto-ci/actions/runs/44919873018)
  also passed 2/2.

**Private source export stays DENIED:** `atto-lang/atto-source.net/main`
still reports `protected=false` with zero active destination/snapshot
approvals, the independent-reviewer setting remains disabled, and the
private rulesets API denies enforcement under current plan entitlements.
The user's requested token-free Git-tree mirroring would still transfer
private source to another GitHub security boundary. Neither this carrier
run nor this document activates that transfer. Source-local ephemeral
runners, or a separately reviewed/authorized exact commit+tree export under
the existing gates, are required to execute the *actual* #33/#194 tests.
No source org approvals, branch protections, or export allowlists were modified.

---

## 2026-11-21 source-local runner recovery (NO private source export)

**P0 alternative to cross-organization Git mirroring:** an authorized **ephemeral
repository-scoped self-hosted runner**, registered directly on the PRIVATE
`atto-lang/atto-source.net` repository, can execute exact source heads
**without sending its Git tree to another GitHub organization**. This is a
separate admin decision, not permission to bypass PR governance or trust
unreviewed code.

### Administrator-only runner registration and isolation

1. Verify a supported GitHub Actions plan, source-repository `Settings →
   Actions → Runners` access, and self-hosted runner availability. An operator
   with repository admin authority must register the runner: this assistant's
   current GitHub connector exposes neither registration tokens nor runner
   management endpoints. **Never paste a registration token into a PR, issue,
   workflow, source file, shared artifact, or this chat.**
2. Use ephemeral, disposable, isolated **Linux x64** and **Windows x64**
   machines, registered to **only** `atto-lang/atto-source.net`. Avoid a
   persistent shared workstation and a runner shared with unrelated repos.
   Use distinct, operator-controlled labels such as
   `atto-net-reviewed-linux-x64` / `atto-net-reviewed-win-x64` only after
   registration. The label is a selector, **not** a source authorization.
3. Do not run arbitrary fork PRs or uncontrolled branches on trusted
   self-hosted runners. Restrict dispatch to the separately reviewed exact
   SHA, expected repository identity, expected root Git tree, and approved
   job matrix. Use an admin-controlled manual dispatch/approved branch
   workflow with `permissions: {contents: read}`, 
   `actions/checkout` pinned to the immutable SHA and
   `persist-credentials: false`. No `pull_request_target`, secrets,
   hosted cloud credentials, local SSH agents, or reusable caches. Deprovision
   the VM/runner after one job. Do not silently retarget every PR workflow
   to untrusted persistent self-hosted runners.
4. On Linux provide the repository's root `global.json`-compatible
   **.NET 10 SDK**, **.NET 8 runtime**, Clang, native linker and zlib
   development packages. On Windows provide the same SDK/runtime plus
   supported Visual Studio C++/MSVC NativeAOT toolchain. Verify tool versions
   and actual managed/NativeAOT binary execution; tool installation alone
   never counts as a passing Atto test.
5. Check out only the admin-approved source SHA and verify
   `git rev-parse HEAD`, `HEAD^{tree}`, source Git blobs, working-tree
   cleanliness and runtime receipt counts. Upload no private compiler
   source or generated binaries to public or cross-organization artifacts.
   Collect only source-private logs/receipts needed for review.

### Exact test targets (stale immediately if any head changes)

- [#33](https://github.com/atto-lang/atto-source.net/pull/33):
  source head `3f32ee7ec6e1e1f30520682cada6122b941ed476`,
  root tree `8cab5369d78b185130ef4f51f84f16c7da9b6ece`.
  Compile and run `verification/Atto.Net.ActorRuntime.Tests`
  on both OSes in managed and NativeAOT modes. Require a nonempty
  per-OS test receipt, **at least 40 assertions**, exact case-count parity
  across managed/NativeAOT, actual NativeAOT binary hash and exit status.
  The workflow currently targets GitHub-hosted runners, so an approved
  source-local runner dispatch must select new self-hosted labels explicitly
  without weakening SHA/policy gates.
- [#194](https://github.com/atto-lang/atto-source.net/pull/194):
  source head `850e46f2a02d3177c8b2f4a8b55a0be96893d09b`,
  root tree `1e81446618fa9f5801b62f8efe8532cff866a74b`.
  Run `bash scripts/test-direct-cil-integrity-harness.sh`,
  `bash scripts/verify-direct-cil-integration.sh`, and
  `bash scripts/test-cil-guest.sh`,
  `bash scripts/test-match-cil-guest.sh`,
  `bash scripts/test-stdio-cil-guest.sh` on the isolated approved Linux
  runner; prove negative no-DLL, binary64, guarded-match and precise stdout
  semantics with actual CoreCLR/NativeAOT guest execution. Windows
  equivalents must use a reviewed PowerShell harness/Windows-native runner
  adaptation rather than assuming `bash` or Linux executable paths work
  unchanged.

### Failed hosted reattempts (NOT compiler errors)

- [Actor Actions run #44917038288](https://github.com/atto-lang/atto-source.net/actions/runs/44917038288)
  was explicitly retried. Both Linux and Windows attempts finished
  `conclusion=failure`, `runner_id=null`, `steps=0`. Job log downloads
  were unavailable; no compiler test ran.
- [CIL Actions run #44917257988](https://github.com/atto-lang/atto-source.net/actions/runs/44917257988)
  was explicitly retried; the new job briefly queued before ending with
  `conclusion=failure`, `runner_id=null`, `steps=0`.
  No source-local executable proof ran.
- This readback proves **no runner assignment**; it does not independently
  identify the administrative/billing error. Inspect repository/org Actions
  billing and Actions UI diagnostics as an authorized admin.

**Security outcome:** source-local self-hosting needs runner registration and
admin-approved untrusted-code isolation, but **does not require activating
the separate cross-organization source-transfer allowlists**. It cannot
satisfy an otherwise unmet protected-main/reviewer PR merge rule. If
source-local execution is unavailable, retain the stricter independent
third-reviewer and protected-main source-export policy before considering
the `scintilla-run-test` private carrier.

---

## 2026-11-21 current actor/compiler heads and source-local audit (review only)

**No export authority is granted or activated by this addendum.** The previously
commented candidate in this document and both allowlists are stale/inactive.

- **Actor arena [#33](https://github.com/atto-lang/atto-source.net/pull/33):**
  latest tested-by-static-inspection source head
  `2f3cc4e2b82b09a0e4c41e48e238d97d123a42ab`, root Git tree
  `2249d9e019753e34e3ace49b9494fd515e1e4454`. Added positive/negative
  host assertions for actor-local versus tenant-global logical fuel accounting,
  tenant fuel exhaustion and terminal snapshot fencing. Linux/Windows workflow
  now requires **at least 40 host assertions and identical managed/NativeAOT
  case-count receipts**. Ten source/workflow contract checks inspected
  statically; no managed or NativeAOT actor test actually ran at this head.
- **Combined direct-CIL [#194](https://github.com/atto-lang/atto-source.net/pull/194):**
  latest head `44de47139b77e5b4142bf4324c4bee1b5f320911`, root Git tree
  `581efa8c533c651f29062db391f9d26361e094c0`. Its two source-local
  CI workflows now check a SHA-pinned, regular-mode, non-symlink **38-blob**
  manifest before compiling. All 38 committed Git blob IDs matched exactly
  against this source head. The verifier script blob
  `25a2ffbafa444d2e06d4e8e051c8b8d7f30afd54` was independently
  re-created byte-for-byte in a synthetic local Git repository and passed
  **8/8 harness tests**, including tampering, duplicate, missing-entry,
  traversal, symlink, mode and dirty-file denials. These are **not** Atto
  compiler/CoreCLR/NativeAOT tests. Source Actions returned **0 runner steps**.
- **Carrier capacity (source-free only):**
  `scintilla-run-test/atto-ci` private ID `1458349760` passed actual
  generic .NET 10 managed and NativeAOT smoke execution on Linux and Windows
  in [run #44860216954](https://github.com/scintilla-run-test/atto-ci/actions/runs/44860216954).
  It received **zero Atto source Git objects**, no approved private source
  snapshot, and no permission from this proposal to accept any.

At this review checkpoint `atto-source.net/main` still reports
`protected=false`; the private rulesets readback requires an unavailable
plan entitlement; there is no independently authenticated eligible third
reviewer; the two source-export allowlists have **zero active entries**; and
`private-ci-independent-reviewer.json` has `enabled=false`. The user's
request for token-free mirroring alone does not satisfy the separately
required independent authorization. **Do not mirror** either source tree
until these gates are independently satisfied and the exact new SHA is
approved on a protected, reviewed main branch. The existing commented
candidates below are intentionally retained as historical proposals only.

---

## Context and exact immutable candidate

- Source: `atto-lang/atto-source.net` (**PRIVATE**, numeric repository ID `1448702222`).
- Target [actor arena/fuel PR #33](https://github.com/atto-lang/atto-source.net/pull/33):
  `2d98b781522319020225e7e20dfd442f50fe52df`.
- Full source commit root tree:
  `efe5c294e1d47ef7dc429a0c0bfcb0fc5b45fd4d`.
- Candidate destination: `embedded-alerts-test/atto-ci`
  (**PRIVATE**, numeric repository ID `1458334665`).
- Related admission issue [#34](https://github.com/atto-lang/atto-source.net/issues/34);
  administrative dependencies [#72](https://github.com/atto-lang/atto-source.net/issues/72)
  and [#66](https://github.com/atto-lang/atto-source.net/issues/66).
- A new commit to PR #33 **invalidates the candidate commit/tree**. Refresh,
  independently review, and approve the replacement exact source snapshot.

### Review candidate (must remain commented until separately authorized)

```text
# embedded-alerts-test/atto-ci 1458334665
# 2d98b781522319020225e7e20dfd442f50fe52df efe5c294e1d47ef7dc429a0c0bfcb0fc5b45fd4d 1458334665
```

The first candidate belongs to
`.github/private-ci-approved-repositories.txt`; the second to
`.github/private-ci-approved-source-snapshots.txt`. Both must remain comments
in this draft. In this repository, comment-only policy entries do not authorize
any transfer. The independent-reviewer policy intentionally remains
`enabled: false` with `reviewer: null`.

## Review gates — all mandatory before any export

1. **Independent identity.** Administrator provisions/verifies a third
   GitHub account with `write` or higher permission, recorded by exact login
   and immutable user ID. The existing disallowlist of user IDs
   `11139560` (`ORESoftware`) and `2148448` (`the1mills`) is immutable
   for this proposal. `@ORESoftware` is CODEOWNER for the policy/workflow
   files, but cannot also serve as the separately required independent
   security reviewer. Do not invent an eligible identity or count PR author
   self-approval.
2. **Effective protected main.** Current GitHub branches API reports
   `main.protected=false`. Rulesets returned an entitlement-related
   HTTP 403; classic protection returned an integration-permission HTTP 403.
   An independently authenticated administrator must verify the repository's
   private-protection entitlement and read back actual enforced protection:
   PR + CODEOWNER review, stale-review dismissal, restricted bypass,
   force-push/deletion prohibition, and required check provenance. Do not
   change billing, public visibility, or rules without owner authorization.
3. **One-shot policy activation.** Only in a **later protected, reviewed PR**,
   activate exactly one approved destination and matching immutable source
   snapshot **together**, and set the independent-reviewer JSON to
   `enabled: true` with the independently authenticated eligible identity.
   Preserve the original `.github/CODEOWNERS` final policy entries and
   `.github/private-ci-required-checks.json` context `private-ci-policy`
   / GitHub Actions `appId=15368`. Caller-provided config must never
   self-grant a destination, reviewer, or source SHA.
4. **Carrier trust.** The proposed carrier was PRIVATE/writable when checked,
   but its default branch was a temporary `ci/...` ref and `main` was not
   found. Its generic workflow uses .NET 10 on Linux/macOS; it does **not**
   implement the .NET 8 Linux/Windows proof required here. Independently
   review/establish an immutable trusted carrier base and a separately pinned
   Linux-x64/Windows-x64 .NET 8 managed-and-NativeAOT workflow. No unreviewed
   destination-workflow inheritance, dynamic script injection, secrets or
   untrusted branch-produced code execution.
5. **Runner evidence, not appearance.** Source repository branches still
   show zero registered actor/canary workflow runs. An unrelated source
   workflow run had `steps=[]`. Carrier
   [run #44794775571](https://github.com/embedded-alerts-test/atto-ci/actions/runs/44794775571)
   performed real steps for **different Atto source**; a later
   [run #44796611352](https://github.com/embedded-alerts-test/atto-ci/actions/runs/44796611352)
   failed before executing steps. Capacity for a new job is unverified.
   Prefer restoring source-repo runners, which avoids external export.

## Minimal reviewed source files for a *future* bounded mirror

The candidate full **source root tree identity** is not an instruction to
publish every source blob. The narrower list below covers the .NET host
runtime and its executable test suite:

| Source path | Git blob SHA |
|---|---|
| `runtime/Atto.Net.ActorRuntime/ActorArena.cs` | `5893ca8bd7aa44ce0fc7c5d2159d373fe426d84c` |
| `runtime/Atto.Net.ActorRuntime/GuestCooperationBudget.cs` | `0870fad33e90dc9215664389f4d51c7055b29bea` |
| `runtime/Atto.Net.ActorRuntime/TenantVm.cs` | `2f82bbcc1ebdec53b43b2180ae970f2abc7065d3` |
| `runtime/Atto.Net.ActorRuntime/Atto.Net.ActorRuntime.csproj` | `2c2a10ee51baf3b0afe5eea3432c4e7ffd6123c8` |
| `verification/Atto.Net.ActorRuntime.Tests/Program.cs` | `c9866a4ef724d6fa0df7f5f1f632a6e31755b307` |
| `verification/Atto.Net.ActorRuntime.Tests/Atto.Net.ActorRuntime.Tests.csproj` | `fd948fc2532ca7584dde22415ddcefab2e1ef729` |

The source-owned CI workflow
`.github/workflows/net-actor-arena-cooperation.yml` is separately
identified by blob `13a562a9809dad2c253b0051f2c3b0af3b523389`, but
**do not automatically install/execute this untrusted workflow on the
carrier**. Use only a separately reviewed, pinned carrier workflow.
If source-owned workflow bytes are approved for evidence, treat them as
inert data. Copying a **subset** produces a different carrier Git tree;
independently log the source full-root SHA and every exported Git blob SHA,
plus the destination restricted-subtree/root SHA. Never claim a subset
has the full source root tree.

## Required execution and containment receipt (only once approved)

- Recheck protected-main policy and effective GitHub private-branch
  protection through a separate trusted authority **before fetching any
  private source blobs**. Confirm exact source HEAD/root-tree and destination
  immutable repository ID, private visibility, write permission and trusted
  carrier base. Fail closed if any mismatch.
- Mirror only explicitly approved regular Git blobs to a restricted
  temporary private branch, without cross-org checkout credentials or
  private-source secrets. Validate every blob's Git SHA **before and after**
  publication and verify no inherited unreviewed workflow can run.
  Do not claim a restricted mirror has the same full tree SHA as the source.
- In a real assigned-runner job on **each** Linux and Windows, execute the
  .NET 8 managed tests, publish and **run** the NativeAOT binary, check
  nonzero executed steps, test exit codes, expected adversarial negatives,
  exact source/blob manifest, and the binary SHA-256 receipts.
- Any failure or source HEAD change requires new source review and fresh
  exact-head execution. A zero-step job is `NOT_EXECUTED`, not passing.
  These are host-runtime probes, **not** compiled `.atto` actor or arbitrary
  untrusted native code TenantVM isolation certification.
- Bound carrier source retention and ref lifetime; document artifacts/logs,
  access, revocation and Git-object retention. Removing a ref does not
  securely purge its objects. No public mirrors or unapproved publishing.

## Independent approval evidence register (blank until verified)

The repository's current GitHub App cannot read private-main protection details
or billing/runner-admission annotations. Do not infer them from CODEOWNERS,
policy comments, the PR author's write permission, or another organization's
successful Actions run.

Before a *separate* activation PR is even considered, the authorized
administrator and **distinct** independent security reviewer should attach
these machine-checkable facts to [issue #34](https://github.com/atto-lang/atto-source.net/issues/34).
Blank fields mean **NOT VERIFIED**; textual notes are not substitutes for
GitHub-enforced branch rules:

| Required evidence | Current state / independently supplied value |
|---|---|
| GitHub plan permits private-repo protection | NOT VERIFIED: rulesets returned entitlement HTTP 403 |
| Effective source `main.protected` and ruleset ID | FAIL: `false`; no approved bypass route |
| Admin read-back of required PR, CODEOWNER, stale-review dismissal, force-push/delete restrictions | NOT VERIFIED |
| Independent reviewer exact login and numeric GitHub ID | NOT PROVISIONED |
| Independent reviewer has source `write` or higher | NOT VERIFIED |
| Reviewer ID not `11139560` or `2148448` | NOT VERIFIED |
| CODEOWNER `@ORESoftware` separate from reviewer | REQUIRED; no approval yet |
| Source-main policy approval PR, its merge SHA, and independent review SHA | NOT AUTHORIZED |
| Exact source commit / source root tree | `2d98b781522319020225e7e20dfd442f50fe52df` / `efe5c294e1d47ef7dc429a0c0bfcb0fc5b45fd4d` (candidate only) |
| Destination private repository and immutable numeric ID | `embedded-alerts-test/atto-ci` / `1458334665` (candidate only) |
| Trusted carrier base commit / workflow blob SHA / reviewer | NOT VERIFIED |
| Carrier `.NET 8` Linux/Windows meaningful assigned-runner admission | NOT VERIFIED |
| Approved source-path allowlist + individually reviewed Git blob SHAs | PROPOSED ABOVE; NOT APPROVED |
| Private ref expiration, access log, artifacts/cache policy and revocation plan | NOT VERIFIED |

Admin investigation of the source Actions failure should record the **actual**
job annotation, runner/billing policy or organization access denial, affected
run ID, and source-free canary run with nonzero executed steps. A workflow
with `steps=[]` cannot diagnose a compiler failure. If source-side runners
recover, prefer that route and **do not activate any carrier policy**.

Only after all entries are independently verified may an authorized maintainer
prepare the separately reviewed one-shot activation. No one should fill the
register with invented account IDs, plausible branch settings, or stale
foreign-PR CI evidence. An "APPROVED" label, comment, or static test does not
itself alter the protected-main policy.

## Explicit current disposition

**No activation, no source export, no executed CoreCLR/NativeAOT tests,
no independent authorization, no merge.** Keep
[PR #33](https://github.com/atto-lang/atto-source.net/pull/33) draft.
The next action belongs to the independently authorized administrator
and reviewer: address [#72](https://github.com/atto-lang/atto-source.net/issues/72)
and [#66](https://github.com/atto-lang/atto-source.net/issues/66),
then make a separate governed activation decision in
[#34](https://github.com/atto-lang/atto-source.net/issues/34).
