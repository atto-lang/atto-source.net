# Source-local .NET Native AOT certification handoff

Status: **operator checklist only; no native execution certified**. This document is not
an approval, release authorization, or substitute for private CI security review.
See [#221](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/221),
[bootstrap #233](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/233)
and [reconciliation #214](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/214).

## P0 — independent security administration, before any trusted dispatch

- [ ] Enable enforceable private \`main\` branch protection/rulesets, required
  checks and CODEOWNER review; independently read back the effective controls.
  A GitHub PR's \`mergeable=true\` is not authorization.
- [ ] Appoint an independent reviewer with verified write-or-higher permission
  who is neither the PR author nor \`ORESoftware\`; independently approve
  workflow, source SHA/tree, runner restrictions and artifact retention.
  \`ORESoftware\` must separately provide CODEOWNER approval.
- [ ] Independently review/bootstrap the three manual-only workflows from #233
  onto protected \`main\` through the authorized merge process. Do not
  change \`private-ci-independent-reviewer.json\` to a nominal approval.
- [ ] Configure isolated ephemeral runner group \`oreslang-net-aot-trusted\`
  restricted to this source repo and precisely these protected-main paths:
  \`.github/workflows/trusted-source-local-linux-aot.yml@refs/heads/main\`,
  \`.github/workflows/trusted-source-local-windows-aot.yml@refs/heads/main\`,
  \`.github/workflows/trusted-source-local-fence-windows.yml@refs/heads/main\`.
  Independently read back repository and workflow restrictions; labels alone
  are insufficient. Never dispatch any PR-authored YAML on those runners.
- [ ] Independently record approved #214 **commit SHA and Git root tree**.
  Record the installed \`main\` workflow SHA separately for each trusted
  definition. A commit SHA can change after review, rebase or new code writes.
  Authorize repository variables only for those exact identities and only
  after runner-group and branch-protection gates have passed.
- [ ] Keep private source in its origin organization; no cross-org mirror,
  export or funded-carrier shortcut without separate snapshot, destination and
  retention approvals.

## P1 — execute actual immutable-source certification

- [ ] Dispatch trusted **Linux Native AOT** from protected \`main\`, not from
  \`pull_request\`. Require real allocated self-hosted runner and all eight
  named job steps successful. Require .NET 10, a native compiler plus four
  independent Linux native application executions: hello **14**, add **42**,
  precedence **40**, structured CFG **45**. Verify rejected unsupported source
  creates no CIL sidecar. Collect
  \`artifacts/evidence/source-local-linux-sha256.txt\`.
- [ ] Dispatch trusted **Windows Native AOT**, with all twelve steps actually
  executed and successful. Require released native PE compiler/app, app output
  **14**, managed/native static-facts agreement and emitted
  \`artifacts/evidence/source-local-windows.json\`.
- [ ] Dispatch trusted **Windows continuation fence**, with all eight steps
  executed and successful, including **16** independent stress rounds and
  \`ISOLATED_FENCE_TESTS_PASSED\`; collect
  \`artifacts/evidence/source-local-fence-windows.json\`.
- [ ] Run the source-owned hosted \`source-modules-native-aot.yml\` and
  \`spin-http-native-aot.yml\` regression matrices against the same combined
  source candidate on Linux and Windows. These require actual GitHub Actions
  jobs and native application execution, not merely an apparently green
  matrix definition.
- [ ] Independently fetch each trusted workflow run and attempt's **run JSON**
  and complete **jobs JSON**, including pagination; preserve the real
  \`run_id\`, \`run_attempt\`, \`runner_id\`, step names/statuses and recorded
  workflow identity. Reject zero-step and unallocated jobs.
- [ ] Download each corresponding original GitHub artifact and independently
  verify presence, origin run/attempt, binary file types (ELF/PE), binary
  SHA-256 digests and application outputs. A digest *printed in a receipt*
  alone does not establish that the binary or its source was correct.
- [ ] Validate the actual compiled release against its exact **tested source
  commit/tree**, never a newer head or a different GitHub organization.
  Re-run/reapprove upon any change to tested source or the installed trusted
  workflow SHA.

## Source-free metadata consistency preflight

The following **read-only** tool checks that three independently acquired
runs/jobs and their three source-local receipts agree with a separately
prepared approved source/workflow identity record. It makes no network calls
and requires no GitHub token. A successful result is **not release
certification**, and the boolean approval flags in the input are operator
assertions, not independent verification of admin settings or review.

Create a local \`bundle.json\` of this shape, using real values fetched
independently from the GitHub API (do not put secrets in the bundle):

\`\`\`json
{
  "approval": {
    "repository": "ores-truffle-oreslang/oreslang-source.net",
    "source_head": "<40-character independently reviewed tested source commit>",
    "root_tree": "<40-character independently reviewed tested root tree>",
    "independent_review_verified": false,
    "protected_main_verified": false,
    "restricted_runner_group_verified": false,
    "workflows": {
      "linux": {"workflow_sha": "<40-character protected-main workflow revision>", "run_id": 0, "attempt": 1},
      "windows": {"workflow_sha": "<40-character protected-main workflow revision>", "run_id": 0, "attempt": 1},
      "fence": {"workflow_sha": "<40-character protected-main workflow revision>", "run_id": 0, "attempt": 1}
    }
  },
  "receipts": {
    "linux": "<full original UTF-8 source-local-linux-sha256.txt content>",
    "windows": {"schema": "oreslang.net.source_local_windows_aot_v1"},
    "fence": {"schema": "oreslang.net.windows_fence_source_local_v1"}
  },
  "execution": {
    "linux": {"run": {}, "jobs": {}},
    "windows": {"run": {}, "jobs": {}},
    "fence": {"run": {}, "jobs": {}}
  }
}
\`\`\`

This is an **intentionally invalid skeleton**. Replace every placeholder
with independently reviewed evidence before invoking:

\`\`\`bash
node --test verification/source-local-receipt-gate.test.mjs
node verification/source-local-receipt-gate.mjs bundle.json
\`\`\`

For each run ID and attempt, independently retrieve the source-org metadata:

\`\`\`bash
REPO=ores-truffle-oreslang/oreslang-source.net
RUN_ID=<independently-selected-run-id>
ATTEMPT=<actual-run-attempt>
gh api "repos/$REPO/actions/runs/$RUN_ID" > run.json
gh api "repos/$REPO/actions/runs/$RUN_ID/attempts/$ATTEMPT/jobs?per_page=100" > jobs.json
\`\`\`

Check that \`total_count\` and pagination are complete. Include the unmodified
JSON objects in \`execution.<linux|windows|fence>.run\` and \`.jobs\`; put the
original Linux text receipt and unmodified Windows/fence JSON receipts in
\`receipts\`. Review and archive these independently fetched inputs and the
GitHub artifact URLs with the exact run/attempt. Do not use the test suite's
synthetic fixtures as real execution evidence.

**Final release gate:** independently reviewed branch/runner configuration,
stepful CI run receipts, independently hashed actual binaries, negative
compiler tests, source-module and Spin HTTP regressions, and final approvals.
Only then can #214 be moved to ready and merged. Maintain #233 as a separate
security bootstrap review and do not merge either while #221 is unresolved.
