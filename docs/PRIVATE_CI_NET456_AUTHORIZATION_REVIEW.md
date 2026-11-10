# PROPOSAL ONLY — independently reviewed private CI proof for .NET #456

> **Status: NOT AUTHORIZED.** This file is a review packet only. It is not an approval, trusted-runner grant, policy enablement, or permission to copy private sources. The existing fail-closed private-CI policy remains authoritative.

## Identifiers to review

| Field | Value |
| --- | --- |
| Source repository | `ores-truffle-oreslang/oreslang-source.net` (private) |
| Source candidate | [PR #456](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/456) |
| Candidate head commit | `cd87860f05037bee11842ee5529eecec508ad371` |
| Candidate root tree | **UNVERIFIED — independently retrieve from authenticated Git commit object before any transfer** |
| Destination (candidate only) | `honeypot-r-us-test/hnpt-e2e` (private) |
| Destination numeric repository ID | `1346492776` |
| Source workflow | `.github/workflows/fixed-array-option-nativeaot.yml` |
| Source workflow blob at candidate head | `45267182f417783c518005e19860099a6ebb77dd` |
| Current source-CI result | [42902557919](https://github.com/ores-truffle-oreslang/oreslang-source.net/actions/runs/42902557919): Linux/Windows jobs failed before step execution |
| Source-CI latest re-run job IDs | Linux `130148094565`, Windows `130148094025`, both `steps=null` |
| Candidate carrier NativeAOT capacity | [41003823626](https://github.com/honeypot-r-us-test/hnpt-e2e/actions/runs/41003823626): source-free synthetic Linux/Windows jobs each executed 5 steps successfully |
| Candidate carrier LLVM capacity | [41003823432](https://github.com/honeypot-r-us-test/hnpt-e2e/actions/runs/41003823432): source-free synthetic toolchain job executed 3 steps successfully |

The source-local failure does **not** establish billing exhaustion: GitHub Actions annotations, organization/private-repository eligibility and billing data require administrator read-back. A successful carrier canary does **not** establish source-export authority or compiler guest parity.

## Review prerequisites / no bypass

1. A separate reviewer with actual write-or-higher permission, whose immutable GitHub ID is **neither** `11139560` nor `2148448`, must be provisioned/verified. Neither candidate identity may self-approve.
2. An authorized administrator must establish enforceable protected-`main` CODEOWNER review, restrictions on force pushes/deletions and effective required-check identity. Verify via the *effective* rules/ruleset UI or API read-back.
3. Independent reviewer and administrator must approve exactly **one** destination repository ID and exactly **one** source commit + independently read source root tree. Write the verified triple to the existing policy files in a separately reviewed, protected change. `private-ci-policy` must pass as the **real** Actions check bound to app ID `15368`.
4. Verify the destination GitHub App/runner access, CODEOWNERS, retention, artifact encryption/access, workflow provenance, and trust boundary; no secrets, credentials, runtime environment or unrelated history may cross the boundary.
5. Obtain an explicit decision on incident inventory and containment from [issue #239](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/239). Historical orphan Git objects or CI artifacts are not cleaned by branch deletion alone. Close out any unresolved policy deviation first.
6. If the authorized approval cannot be demonstrated, **stop**. Continue repairing source-local Actions runner admission instead; do not treat user-requested testing or a successful carrier synthetic test as a replacement for independent authorization.

### Independently inspect root tree

From an authenticated source-repository account with read access:

```sh
# Read-only GitHub API. A reviewer must verify the response is the source
# commit object and record its tree SHA. Do not confuse the commit ID with
# the root tree ID.
gh api repos/ores-truffle-oreslang/oreslang-source.net/git/commits/cd87860f05037bee11842ee5529eecec508ad371 --jq '.tree.sha'
```

Confirm the original PR still points at the candidate head. If it has advanced, create a *new* authorization review with the new commit/tree; no automatic carry-forward.

## Mandatory fixture identity (candidate source head)

- `contracts/collections/v1/array-get-copy-loop-option42.atto`: blob `6cbb6be65250d343b5c1195cc650ea14075001cb`.
- `contracts/collections/v1/array-get-copy-signed-boundaries42.atto`: blob `c7fac378157f5c42a886f2b80bd3b6df0c18b762`.
- `contracts/collections/v1/array-get-copy-immutable42.atto`: blob `d934d5b494406b7e3753173a445af1c0d446a763`.

The approving reviewer must independently compare *all* source blobs used by the workflow, especially compiler/runtime/test inputs. Do not copy an untrusted `pull_request` workflow into the carrier's privileged context without a controlled review; pin the approved carrier workflow on protected destination default branch.

## Conditionally permitted test execution — only after approval

1. Mirror the approved **exact source Git tree**, using reviewed Git object provenance rather than a cross-org private checkout token; no secret material or other refs.
2. Bind destination test commit to source commit, source **root tree**, tree entry/blobs, carrier workflow SHA, run ID and attempt. Checkout via destination-local `GITHUB_TOKEN` with read-only contents; no source-org checkout credentials or workflow-token write permissions.
3. Run source `.atto` → compiler → managed tests/CFG/CIL → Linux and Windows **real NativeAOT** binaries, including the six source-owned guest variants and negative compiler diagnostics. Preserve nonzero executed steps, exact `stdout`, guest exit `0`, binary SHA-256 and exact source fixture blob hashes.
4. Do not count source-only/host-only canaries as compiler execution. Distinguish semantic compiler failures from runner-admission failure (`steps=null`, `[]`, `runner_id=0`).
5. Verify scope and artifact expiry, revoke any temporary carrier branch as approved, and account for orphaned Git blobs, caches, Actions logs, binaries and artifacts. No unverified claim of purge.
6. Keep all source PRs **draft** until exact-head receipt, dependency resolution and independent review are complete; any new source head invalidates this packet.

## Boundaries

This packet **does not** change `.github/private-ci-approved-repositories.txt`, `.github/private-ci-approved-source-snapshots.txt`, `.github/private-ci-independent-reviewer.json`, `.github/CODEOWNERS`, protected-branch settings, organization billing, GitHub Actions permissions or secrets. It explicitly cannot authorize an export.

Tracked governance: [#239](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/239), [#260](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/260), runner-admission [#427](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/427).
