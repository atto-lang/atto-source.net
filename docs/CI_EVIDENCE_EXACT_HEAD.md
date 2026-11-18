# Exact-head GitHub Actions evidence gate (.NET)

Status: **validation tooling only, not a CI execution receipt**.

GitHub Actions runs on the private .NET repository have repeatedly returned
`failure` with **zero job steps**. These are *not* build/test failures with
compiler diagnostics and are not successful execution evidence. The current
tools cannot read organization billing/runner allocation settings. Inspect the
GitHub Actions UI and have an authorized administrator check spending policies,
runners and billing before requesting additional expensive runs.

This evidence parser is read-only and uses Node.js built-in modules. It never
connects to GitHub or acquires credentials. The JSON inputs must be fetched
from the originating repo, at the reviewed run ID/attempt, independently of
the source branch being reviewed. Fake fixture data must never be used as
release evidence.

## Artifact provenance metadata is not execution certification

The read-only `verification/artifact-provenance-gate.mjs` inspector accepts
independently fetched, **complete** GitHub Actions run and artifact-list JSON
plus separately reviewed source repository numeric identity, commit SHA,
root-tree SHA, run ID, run attempt, and required artifact names. It rejects
missing/duplicate/truncated artifacts, expired or empty artifacts, malformed
SHA-256 digest **metadata**, foreign repositories, wrong commit/run IDs,
and inconsistent artifact origin metadata.

Run its actual Node.js regression tests on an authorized source-local checkout:

```bash
node --test verification/artifact-provenance-gate.test.mjs
```

The positive result is **`METADATA_CONSISTENT_UNCERTIFIED`**, never `PASS`.
GitHub's artifact metadata cannot identify the specific workflow **attempt**,
verify the **downloaded artifact bytes**, independently attest the **source
tree**, or prove the contained compiler/guest tests ran. Those checks are
always reported as `false`. Releasing artifacts requires a separately
downloaded-and-hashed artifact, an exact-run-attempt source/tree-bound receipt,
and the real stepful required compiler/test job evidence. The inspector never
authorizes copying private source to another organization.

## Capture and validate a real run

```bash
REPO=ores-truffle-oreslang/oreslang-source.net
HEAD=<independently-reviewed-40-character-PR-head>
RUN_ID=<actual-Windows-Native-AOT-run-id>
ATTEMPT=1
gh api "repos/$REPO/actions/runs/$RUN_ID" > run.json
gh api "repos/$REPO/actions/runs/$RUN_ID/attempts/$ATTEMPT/jobs?per_page=100" > jobs.json
node verification/ci-evidence-gate.mjs --repo "$REPO" --head "$HEAD" \
  --run-id "$RUN_ID" --attempt "$ATTEMPT" \
  --workflow "Windows Native AOT" --run run.json --jobs jobs.json
```

Every completed-success job must have a positive numeric runner ID and nonblank
runner name; extra successful jobs must contain at least one completed
non-housekeeping executable step. An extra job containing any failed, skipped,
malformed, or duplicate step is rejected even if another step passed. Runner
names and extra-step names reject invisible, combining and compatibility
characters. Runner setup/teardown and pre/post steps alone are insufficient.
These checks do **not** establish guest correctness.

The gate fails if any of the following is true: different run/repo/SHA/
attempt/workflow, non-successful run, incomplete jobs list, missing required
job, zero executed job steps, missing any explicitly named hosted workflow
step, malformed/duplicate step names or numbers, or failed/skipped required
job steps. Every job must identify the selected run, attempt and source head. The disabled opt-in
`self-hosted-windows-static-facts` job may be skipped; successful mandatory
hosted Windows and Linux jobs remain required; an enabled self-hosted job must
execute all its explicitly named steps successfully. Checkout assertions and the
Windows receipt emission/upload steps must also succeed. It does **not** independently replay
a binary, validate the runner's logs or prove a compiler's memory safety.

Repeat independently for `Integration declaration integrity` using
`--workflow "Integration declaration integrity"` and its own run ID.

Self-test without GitHub/credentials:

```bash
node --test verification/ci-evidence-gate.test.mjs
```

Pass requires actual Windows x64 Native AOT workflow execution at the reviewed
head, Linux diagnostic execution, the declaration-integrity workflow (with
the explicit declaration-only designation), independent review and any required
project-specific cross-backend contract gates. Linux-only results do not
certify Windows. A failed/zero-step run cannot be substituted with synthetic
or another organization's pass. Do not export private source to other
organizations without an approved security review.
