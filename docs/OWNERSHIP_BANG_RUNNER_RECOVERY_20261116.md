# Ownership-bang rollout — exact-head runner recovery and independent export handoff

> **Status (2026-11-16): BLOCKED / NOT_EXECUTED / NO SOURCE EXPORT AUTHORIZATION.**
> This document and [its read-only immutable-head ledger](evidence/ownership-bang-20261116-source-head-ledger.json) are **not** policy approval, runner allocation, compilation proof, NativeAOT evidence, or merge authorization. PR heads may advance; revalidate independently.

Related: [runner admission #427](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/427), [independent private-CI governance #260](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/260), [historical transfer inventory #239](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/239).

## Reproducible evidence, not CI-green inference

The ledger pins **ten PR numbers, immutable repository IDs, full 40-hex commit SHAs, root Git tree SHAs**, and one per-repository sampled PR-triggered workflow run/job. Each sampled job was completed failed (formatter completed cancelled) with `steps=[]`; inspected run artifact lists were empty. These are **NOT_EXECUTED**, regardless of the workflow YAML's intended tests. The connector exposes first-page/latest-attempt subsets, so this is not exhaustive proof of all runs or artifacts. It is not a certified completed test ledger.

Two additional, isolated, **source-free** workflows were created without checking out Oreslang code or handling secrets:
- [daedalus-fab/atto-ci draft #1](https://github.com/daedalus-fab/atto-ci/pull/1), [run 43978838488](https://github.com/daedalus-fab/atto-ci/actions/runs/43978838488), job `133689717134`: `failure`, zero executed steps, no artifact, job log HTTP 404 BlobNotFound.
- [anticaptrad/atto-ci draft #28](https://github.com/anticaptrad/atto-ci/pull/28), [run 43978986947](https://github.com/anticaptrad/atto-ci/actions/runs/43978986947), job `133690226633`: same outcome.

Earlier successful jobs in those orgs demonstrate **past** runner allocation, not present available minutes or capacity. The current GitHub API integration cannot read org billing/Actions policy or job admission annotations; **do not claim billing is the root cause without administrator confirmation**. The earlier Nov 9 GitHub Actions incident does not establish a root cause for Nov 16 jobs.

## P0 administrator diagnostics — record an evidence-backed decision

An authenticated **organization owner or billing manager** should inspect the source organization *and any contemplated carrier* in this order:

1. **Billing & Licensing / Budgets and alerts** — remaining Actions entitlement, metered spending stop rule, selected cost center and payment failures (capture redacted proof and effective timestamp).
2. **Settings / Actions / General** — GitHub-hosted runner policy, disabled workflows, permitted actions, fork/PR execution approval, runner groups/labels and execution restrictions.
3. **Exact workflow run UI and check annotations** — identify admission refusal details, `startup_failure` if applicable, concurrency saturation, runner allocation and status. The integration returned zero steps and log BlobNotFound but cannot retrieve privileged annotations.
4. **GitHub service status and support** — if billing and policy are healthy, raise a GitHub Support ticket with two zero-step canary run/job IDs and independent read-only API observations. In particular do not confuse an unrelated historical outage with a proved explanation.
5. Record: **confirmed root cause, admin identity/role, exact org/repo, when it was corrected, observed admission proof**. No blind paid rerun while root cause is unknown.

A source-local, trusted, isolated self-hosted runner is an alternative that does not export code. The repository documents its opt-in Windows runner in [CI recovery](ci-recovery.md). It still needs owner-approved runner registration and safe trust isolation; no runner was registered during this audit.

## Separate P0 governance gate — one-snapshot export only

Read-back from source `main`: branch reports `protected=false`; `.github/private-ci-independent-reviewer.json` is `enabled=false` and `reviewer=null`. Explicitly disallowed reviewer IDs: `11139560` and `2148448`. The approved destination and exact-source-snapshot lists contain only comment lines, **zero active approvals**.

The historical candidate `honeypot-r-us-test/hnpt-e2e` (immutable repository ID `1346492776`) is **not** an active approval, and a previous source-free runner pass is not current availability.

Before moving a single private blob to *any* other organization:
1. An admin establishes **effective, enforced** protected-main rules with CODEOWNER review, dismissed stale reviews, no force-push/deletion or bypass, and verifies the `private-ci-policy` required check identity.
2. An independent source-security reviewer with actual write-or-higher access, not an excluded account, reviews the *current* exact source SHA + independently obtained root tree + immutable private destination repository ID + destination access and retention + reviewed test plan.
3. Review and resolve security inventory obligations from #239; do not misrepresent branch deletion as Git-object/artifact purge.
4. Activate **the reviewed** destination-and-snapshot policy entries together through the protected process, not via this documentation PR. A head change voids the request.
5. Only then mirror a temporary private tree using destination-local `GITHUB_TOKEN` to check out the **local** mirror, retaining original blob mode/symlink identity and full exact-tree equality. Ensure no secrets or unrelated refs, and quarantine/clean according to approved retention policy.

**Do not mirror now**. User permission to test is not the independent protected authorization demanded by existing source policy. Do not weaken CODEOWNERS, private CI policy or required checks.

## Required exact-head tests after admission is restored

| Scope | Mandatory evidence before claiming parity |
| --- | --- |
| Spec #67 | Canonical `copy!`, `take!`, `borrow!`, `borrow! mut`, `share!`; accepted, legacy and rejected aliases; separate semantic invalid cases |
| Formatter #144 | Canonical spacing and idempotence, nested/trivia and negative lexical cases, actual Rust test execution |
| Java #3112 | Parser AST equivalence, duplicate mutable qualifiers, borrow escape/use-after-take and JUnit/compiled guest cases |
| LLVM #1831 | Exact lexer/AST and diagnostics, native C++ tests and CTest, emitted LLVM IR → **real executable**, negative ownership checks |
| .NET #531 | Confirm intended **fail-closed** unsupported ownership-bang lowering, managed test suite and real NativeAOT runtime/negative checks where applicable; do not claim unimplemented semantics |
| Wasm #490 | Pinned LLVM-to-wasm32 source identity, real Wasm guest execution, reject unsupported mutable borrow, no synthetic parity |
| Rust #71, Cranelift #117 | Explicit fail-closed tests, actual Rust/native test steps; no accidental acceptance of unsupported typed lowering |
| JS #254, HDL #46 | Strict lexical/parser admission and negative tests, actual Node/test runner; HDL preflight is not hardware synthesis |

Every required run needs **read-only authenticated confirmation** of source repository identity, current PR head, Git tree, correct workflow and workflow blob, run ID and attempt, exact expected jobs, real runner IDs, nonempty completed executable steps and evidence of relevant compiler/guest output. For multiple jobs, verify each mandatory step *within its corresponding job*. A passing source-free canary is only runner-capacity evidence.

This repository already has strong exact-run validators: [EXACT_GITHUB_RUN_EVIDENCE_V1.md](EXACT_GITHUB_RUN_EVIDENCE_V1.md) (`scripts/verify_ci_run.py` and source-free regressions) and [CI_EVIDENCE_EXACT_HEAD.md](CI_EVIDENCE_EXACT_HEAD.md) (`verification/ci-evidence-gate.mjs`). Use those as intended for supported originating workflows. They **do not** verify full release artifacts or grant source export; separate validated guest receipts and SHA-256 artifact bindings are necessary. Expand coverage to other repositories through a **reviewed** verifier update, not by treating unsupported backend keys as verified.

Example administrator-side read-only capture using an **independently selected** run/attempt, without relying on first-page default:
```bash
REPO='ores-truffle-oreslang/oreslang-source.net'
RUN_ID='<real-current-head-run-id>'
ATTEMPT='<independently-verified-run-attempt>'
gh api "repos/$REPO/actions/runs/$RUN_ID" > run.json
gh api --paginate "repos/$REPO/actions/runs/$RUN_ID/attempts/$ATTEMPT/jobs?per_page=100" > jobs-pages.json
gh api --paginate "repos/$REPO/actions/runs/$RUN_ID/artifacts?per_page=100" > artifact-pages.json
```
The paginated responses need reconciliation of every page/total before using the trusted evidence checker. An artifact *listing* alone cannot attest the downloaded artifact's bytes or guest stdout; preserve hashes, timestamps, exact run ID/attempt and reviewed test vector IDs.

## Exit criteria

- An admin documents the **verified** admission cause/resolution, or approved source-local runner capacity.
- A **fresh** source-free attempt has real, successful, completed steps and an obtainable receipt.
- Private transfer, if selected, has one activated independent source SHA/tree/destination/test-plan approval under effective main protection. No prior candidate approval is reused.
- Each of the ten current PR heads receives actual required compiler/test/guest evidence with correct claim scope and artifact integrity, or remains explicitly `NOT_EXECUTED`/`UNSUPPORTED_FAIL_CLOSED`.
- No merges/publication until independent code and security reviews. Monitoring emits alerts only on a substantive unblock or newly executed verified regression.

This documentation-only draft adds **no workflow, token, source mirror, policy grant, runtime implementation, or compiler pass claim**.
