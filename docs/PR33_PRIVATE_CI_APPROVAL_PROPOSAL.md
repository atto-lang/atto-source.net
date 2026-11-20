# PR #33 private-CI authorization proposal — review only

> **NON-ACTIVATING DRAFT.** This document and the adjacent commented policy
> candidates are NOT permission to export private source. Do not remove
> comment markers or publish source Git objects on the strength of this PR.
> The private CI policy remains in deny-all mode until the independent reviewer
> and enforceable protected-main requirements are verified and a later,
> separately reviewed activation change is approved.

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
