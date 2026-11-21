# Atto .NET source-local one-shot CI: administrator handoff

> **NON-ACTIVATING / PRIVATE SOURCE STAYS IN SOURCE REPOSITORY.**
> This document is an operator checklist, not a CI approval, independent
> code review, runner registration, branch-protection change, or authorization
> to export source Git objects. In particular, do **not** copy the private
> `atto-lang/atto-source.net` tree to another organization as a workaround
> for GitHub Actions minutes.

## Exact reviewed inputs (refresh before operating)

- Source repository: `atto-lang/atto-source.net` (private).
- Manual-only executor: draft PR [#197](https://github.com/atto-lang/atto-source.net/pull/197).
  At 2026-11-21 audit head `88119801198200ba03fb4f05e3a92420279bdc20`,
  workflow blob `ec8a77ce618aa04e73bf74ab648a659fa77eca39`,
  committed offline test blob `baf4c39efbd4cb36949379d7866bec3d9dac2182`.
- Actor runtime: draft PR [#33](https://github.com/atto-lang/atto-source.net/pull/33),
  exact source HEAD `3f32ee7ec6e1e1f30520682cada6122b941ed476`,
  root tree `8cab5369d78b185130ef4f51f84f16c7da9b6ece`.
- CIL compiler: draft PR [#194](https://github.com/atto-lang/atto-source.net/pull/194),
  exact source HEAD `850e46f2a02d3177c8b2f4a8b55a0be96893d09b`,
  root tree `1e81446618fa9f5801b62f8efe8532cff866a74b`.

**Any changed PR HEAD invalidates the pinned test declaration;** rerun independent
review and update expected head/tree values in a new, reviewed workflow change.
Never silently test a moving branch and report it as the former SHA.

## Hard blockers as observed 2026-11-21

1. The repository's `main` reported `protected=false`. The workflow
   deliberately rejects unprotected `main` before checking out private
   source. The connected GitHub App has repository admin visibility but
   its branch-protection read returned **403 Resource not accessible by integration**;
   no branch-protection write action is available through this connection.
2. The repository rulesets endpoint returned **403: Upgrade to GitHub Pro or
   make this repository public to enable this feature**. Confirm plan and
   policy availability in GitHub before promising branch protection. **Do not
   make the private repository public.** If enforcement requires an upgrade,
   obtain it via repository administration.
3. The available app does not expose self-hosted runner registration/actions
   administration. A repository administrator must do this in GitHub itself.
4. There are **zero** active approved private source-export destinations,
   **zero** approved exact source snapshots, and
   `.github/private-ci-independent-reviewer.json` is disabled. This path
   does not transfer source out of the organization; the export policy stays
   deny-all.

## Administrator sequence (requires access outside this GitHub connector)

- [ ] Keep repository **private**. Confirm the required GitHub plan supports
      protected branches/rulesets on this private repository.
- [ ] In GitHub repository **Settings → Branches** (or **Rules → Rulesets**),
      create an enforceable protection for `main`. Require the appropriate
      independent PR reviews and passing status checks; restrict force-push,
      deletion, and bypass permissions. Verify the REST branch response
      returns real JSON `"protected": true`; do not treat the string
      `"true"` as sufficient.
- [ ] Independently review the exact PR #197 diff and underlying source SHA
      pins. Apply the project's existing PR review-age and reviewer governance;
      a requested review or green synthetic canary is not approval.
- [ ] After requirements are enforced and the PR is approved, merge #197
      only through permitted review and merge policy. Reconfirm the merged
      workflow's `github.workflow_sha` and pins.
- [ ] Register **disposable repository-scoped** self-hosted runners on
      `atto-lang/atto-source.net`. They must never be shared with untrusted
      PR workflows or reused for unrelated repositories. Assign exact
      labels `self-hosted,linux,x64,atto-net-approved-one-shot` to Linux
      and `self-hosted,windows,x64,atto-net-approved-one-shot` to Windows.
      A label is **not** evidence of isolation or ephemerality; provision
      one-job-per-VM lifecycle externally and destroy the VM after each job.
- [ ] On Linux, verify `bash`, `git`, `python3`, `clang`, `ld` and
      native .NET AOT linker/development dependencies. On Windows, verify
      `pwsh`, `git`, Visual Studio C++ NativeAOT prerequisites and a
      supported 64-bit build environment. The reviewed workflow installs
      .NET 10 SDK and .NET 8 runtime; both operating systems must have
      network access to GitHub/npm-independent .NET package sources required
      by their projects, but no alternate organization source checkout.
- [ ] Constrain runner network egress and workspace lifetime. Remove runner
      registration and wipe the VM after the job. Do not expose persistent
      organization credentials, long-lived tokens, or secrets to PR source.
- [ ] Dispatch **only the merged manual workflow on main**, after ensuring
      a trusted operator and active independent approval. The workflow gates
      on `workflow_dispatch`, the private source repository and protected
      `main`, serializes dispatches, and checks current #33/#194 heads
      before and after actual tests.

One Linux runner plus one Windows runner is sufficient if GitHub queues
Linux jobs; two isolated Linux runners allow actor and compiler jobs to run
simultaneously. Do not weaken isolation merely to improve throughput.

## Exact-source test evidence and completion criteria

1. The workflow CI job first runs the committed offline preflight harness from
   the reviewed workflow SHA: expect exactly
   `ATTO_NET_SOURCE_LOCAL_PREFLIGHT_MOCK_PASS cases=18 optimized=true trigger_mutants=6 header_mutants=5 network=NONE`.
2. Actor #33 on Linux **and** Windows: require a successful real
   `dotnet run --project verification/Atto.Net.ActorRuntime.Tests`
   followed by the corresponding published NativeAOT binary; exactly one
   `ATTO_NET_ACTOR_ARENA_COOPERATION_PASS cases=N` receipt from each,
   with managed/native case counts equal and the NativeAOT SHA-256 recorded.
3. Compiler #194 on Linux: run
   `scripts/test-direct-cil-integrity-harness.sh`,
   `scripts/verify-direct-cil-integration.sh`, and the actual
   `scripts/test-cil-guest.sh`, `scripts/test-match-cil-guest.sh`,
   `scripts/test-stdio-cil-guest.sh`. Require valid/invalid cases,
   native guest execution, no artifact leaks on negative cases, and
   `artifacts/AttoGuest.dll` SHA-256.
4. Require workflow checkout of **exact Git commit and root tree**, successful
   source and governance preflight **and postflight**, zero failed jobs,
   run URL + attempt + workflow SHA + runner disposal evidence. Recheck
   PR HEAD after the run before counting its results as relevant.
5. Until all of that exists, record **NOT_EXECUTED**, not PASS. A passing
   source-free private-carrier Linux/Windows NativeAOT canary demonstrates
   runner capacity only and cannot replace an Atto source test.

## Alternate-organization path is separately gated

Should source-local runners be impossible, **do not** simply mirror this
private source Git tree into another organization. A cross-organization
mirror requires the source repository's explicit exact-HEAD/tree/snapshot
authorization, eligible independent reviewer, and approved destination
under enforced governance, followed by exact per-blob verification,
temporary isolated carrier branch and post-run deletion. None of those
authorization requirements are supplied by a successful synthetic Actions
run or by avoiding a checkout token.
