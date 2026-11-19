# Ownership-bang: exact-head Java / LLVM qualification and independent export review packet

> PREPARATION ONLY / NOT_EXECUTED / PRIVATE EXPORT DENIED. This packet is not an authorized mirror request, compiler receipt, code-review approval or release certificate.

Prepared on 2026-11-19 UTC. Source-local documentation only. Existing [runner recovery runbook](OWNERSHIP_BANG_RUNNER_RECOVERY_20261116.md) and [historical head ledger](evidence/ownership-bang-20261116-source-head-ledger.json) remain authoritative background, not current execution proof. See [runner admission #427](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/427), [governance #260](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/260), and [historical transfer inventory #239](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/239).

## Current implementation and source-local runner recovery (2026-11-19 UTC)

**Important change:** [Java ownership-bang #3186](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/3186), PR head `176694acbbc09c365f434e906e7ed3c035a95193`, was **merged by another actor** into Java `main`. Its merge commit `1f6d48e2cb7f76dfe4fec3ad357319f17277439a` contains the canonical `borrow! mut` parser and SHA-pinned guest fixtures (verified by source Git-blob readback). **No actual executed JUnit or Truffle guest receipt has been observed; the merge is NOT a parity certificate.** The exact merge commit, not the older PR head, is the next Java qualification target.

[LLVM #1903](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/pull/1903) remains a **draft, open, unmerged** compiler fix, source-locally rebased onto current `main` with mergeability rechecked at `5eba17e441faacc85d446c2f6b12c1537709c48c`, with the `rt borrow mut` CTest guest registered at default/O0/O2.

| Target | Exact immutable source under test | Git root tree | Compiler execution |
| --- | --- | --- | --- |
| Java merged #3186 | `1f6d48e2cb7f76dfe4fec3ad357319f17277439a` | `a4402e1967e0ed63fe39d26931ece935c341e820` (direct Git commit API readback) | NOT_EXECUTED |
| LLVM draft #1903 | `5eba17e441faacc85d446c2f6b12c1537709c48c` | `74aed6c4fa17b372b181886147b4594325458ed4` (direct Git commit API readback) | NOT_EXECUTED |

### New private-source-preserving, source-local Linux fallback proposals

- [Java draft CI #3191](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/3191), workflow-PR head `43f799b5d439ade92012be493cf1196365acaf6c`. Its reviewed workflow checks out only the immutable Java **merged** source commit above from the same Java repository. Requires an isolated `oreslang-bang-java-isolated` self-hosted Linux runner, an explicit owner-approved CI PR head and source SHA in repository variables, and a separately applied `ownership-bang-ci-approved` label.
- [LLVM draft CI #1904](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/pull/1904), workflow-PR head `d904cb587d8e77a898f80ced1a29e2317da211a2`. Its reviewed workflow checks out only LLVM draft #1903's exact source head from the same LLVM repo. Requires dedicated `oreslang-bang-llvm-isolated` ephemeral runner and same dual-SHA approval.
- **Both CI proposals are OFF BY DEFAULT and NOT_EXECUTED.** No runner was registered or configured, no variable or approval label was set, and no private source was exported. See the per-repository `docs/OWNERSHIP_BANG_SOURCE_LOCAL_LINUX_RUNNER.md` for administrator-only setup and teardown. Source-local runners still depend on GitHub Actions organization policy allowing scheduling. The GitHub app cannot access billing/runner registration.

### Independent alternate runner and synthetic evidence hardening

A **new real alternate GitHub Actions run** [quaestor-ledger-test/atto-ci #44444480481](https://github.com/quaestor-ledger-test/atto-ci/actions/runs/44444480481), job `135293163894`, executed **22/22 assistant-authored source-free synthetic evidence-verification tests** successfully (21 negative mutations correctly denied and one valid synthetic record accepted). All 8 recorded job steps completed successfully. Its artifact [#14492467767](https://github.com/quaestor-ledger-test/atto-ci/actions/runs/44444480481/artifacts/14492467767) was downloaded; ZIP SHA-256 `3b7f3af32c725ece8255458ef1aea1fab3454dec69b86afdebbe4ad3845c4f8f` matches GitHub metadata, and both internal receipt/test-output hashes match. The run receipt identifies the exact synthetic carrier head, run ID/attempt, runner ID/name and explicitly says `source_contents_transferred=false`, `compiler_guest_executed=false`. This tests the verifier mechanism, **not Oreslang compiler correctness**.

The carrier's [original source-free canary #44212748860, attempt 3](https://github.com/quaestor-ledger-test/atto-ci/actions/runs/44212748860) also passed six executed steps and uploaded [artifact #14491444377](https://github.com/quaestor-ledger-test/atto-ci/actions/runs/44212748860/artifacts/14491444377). It demonstrates live alternate capacity, not remaining paid minutes, a secure export grant, or originating-source CI.

Further **source-local** hardening in CI draft #3191 and #1904 now pins actual SHA-1 Git blobs of test fixtures, requires clean builds (Java Maven `clean test`, LLVM fresh CMake build), validates the full 24-case LLVM ownership-bang CTest inventory and preserves its detailed `LastTest.log`, and recalculates receipt/report SHA256 before upload. Both CI-only branches were independently rebased onto current `main`, preserving exact workflow and runbook blob identities. Both remain draft/unmerged/off-by-default and will not run without administrator-approved exact workflow/source SHAs and isolated runner registration.

**LLVM correction #1903** is now source-locally rebased at `5eba17e441faacc85d446c2f6b12c1537709c48c` with root tree `74aed6c4fa17b372b181886147b4594325458ed4`; 24 required CTest names reconcile with the frozen CMake registrations, but neither the LLVM compiler nor native guest tests have run. Earlier #1903 job receipts on a superseded head are invalid for the new head.

**Latest source admission attempt:** At the reconciled LLVM head `5eba17e441faacc85d446c2f6b12c1537709c48c`, source Actions [real-LLVM guest #44455440518](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/actions/runs/44455440518), [native-admission #44455440406](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/actions/runs/44455440406), and [ownership grammar #44455440517](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/actions/runs/44455440517) have sampled jobs with **zero executed steps and zero artifacts**. CI workflow #1904 must use the updated exact-source SHA/tree and **new** CI-only workflow PR SHA. The connected GitHub API explicitly rejects Actions runner-registration and Actions-permission endpoints; the authorized repository/org administrator must complete those controls outside this chat. Source-private export remains DENY under issue #260.

### Executed-step evidence remains absent

The current Java merged commit has new first-page source-free runs: [physical-source #44435049581](https://github.com/ores-truffle-oreslang/oreslang-source.java/actions/runs/44435049581) job `135261692611` and [receipt-checker #44435049521](https://github.com/ores-truffle-oreslang/oreslang-source.java/actions/runs/44435049521) job `135261692072`, both failed with `steps=[]` and zero run artifacts. The LLVM [rerun #44429270289](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/actions/runs/44429270289) job `135247173762` likewise had `steps=[]`, zero artifacts, and a `404 BlobNotFound` job log. These outcomes show admission trouble, not compiler regressions. The literal root cause remains UNCONFIRMED.

The adjacent JSON now records the merged Java source identity, LLVM draft head, two independently reviewed candidate **workflow** SHAs and the absence of actual runner registration/authorization, preserving the separate original merged-benchmark entries for historical provenance.

## The candidate runner and the hard security boundary

- Candidate destination: private `quaestor-ledger-test/atto-ci`, numeric repository ID `1458333874`. This is an **unapproved candidate**, NOT an allowlisted private source destination.
- The latest *source-free* GitHub Actions canary run [44212748860, attempt 2](https://github.com/quaestor-ledger-test/atto-ci/actions/runs/44212748860), job `135231411544`, succeeded with four substantive completed steps, two Node tests and zero failed tests. Artifact [14483897425](https://github.com/quaestor-ledger-test/atto-ci/actions/runs/44212748860/artifacts/14483897425) is 611 bytes, verified downloaded ZIP SHA-256 `436e090c4a1865a034debb13c9141a398e8c06ffb222c35977713e17e6938186`. No Oreslang private source was checked out or transferred.
- At source `.NET main`, independent reviewer policy remains `enabled=false` with `reviewer=null`; two immutable reviewer IDs are excluded. Destination and exact-snapshot allowlists contain only inactive comments. The reported source `main` is not proven protected. **Do not transfer any private blob**.
- Source-local runner admission root cause remains unknown. An org/billing administrator must capture the literal Actions admission denial and repair it, or the source owner must authorize an isolated, source-local trusted runner.

## Pin exact source identities before test execution

| Component | PR / exact head | Git root tree | Review status |
| --- | --- | --- | --- |
| LLVM | #1831 `94c682798e6272c94174478a805deb1a5362c568` | `5d4b50dd759bf5c5552feaec1e2272cc5ae8e8fb` from 2026-11-16 source ledger for identical commit; independent recheck required | Merged; compiler tests NOT_EXECUTED |
| Java | #3112 `e52172a7acd489d70ae10a62bcf47816b3e7cf32` | **UNVERIFIED** in available connector response; do not substitute an older commit's tree | Merged; compiler tests NOT_EXECUTED |
| Spec | #67 `676b35a07f68436960c07281aee4f86fae8b3abc` | Must independently recheck if used as export source | Open; target contract only |

Before running any approved source mirror, independently query the source Git commit/tree API on the CURRENT SHA, verify repository numeric identity and all blob modes/symlinks, and compare the full destination root tree exactly. A commit ID is **not** interchangeable with a tree ID. A head change invalidates prior source approvals.

## LLVM exact-head test plan, after runner admission or secure authorization

Recorded executable contracts in `CMakeLists.txt` at the pinned LLVM head include `native_ownership_bang_aliases_parser_and_checker`, `native_ownership_bang_*_guest`, optimization variants `*_O0` / `*_O2`, negative diagnostic cases and sanitizer runtime smoke. Real guest contracts are registered with `--run 42`, which requires checking the harness result semantics rather than assuming stdout. Fixture blob SHA pins are enforced in the native checker. Preconditions include a compatible LLVM toolchain, CMake, OpenSSL Crypto and Clang.

```bash
# From an independently authorized source-local checkout, or an APPROVED exact-tree private mirror
test "$(git rev-parse HEAD)" = 94c682798e6272c94174478a805deb1a5362c568
git rev-parse HEAD^{tree}  # compare with independently reviewed expected root tree
cmake -S . -B build -DBUILD_TESTING=ON
cmake --build build --target oreslang-llvmc oreslang-native-ownership-bang-aliases-test --parallel 2
ctest --test-dir build -N -R '^native_ownership_bang_'
ctest --test-dir build --output-on-failure -R '^native_ownership_bang_'
```

Require an independently enumerated expected test set, nonzero executed count, zero unexpected disabled/skipped tests and the actual compiled artifacts. CTest pass alone does not prove ASan/UBSan instrumented the guest, and host AST checks alone do not prove native execution. Inspect generated executable/IR evidence and negative no-IR outputs separately.

## Java exact-head test plan, after runner admission or secure authorization

The pinned `pom.xml` uses Java release 21, Maven Surefire/JUnit and GraalVM dependencies. Two actual source tests are `OwnershipBangAliasesTest` and `OwnershipBangGuestTest`. The guest test verifies three immutable `.atto` fixture blob hashes and evaluates actual Truffle guests, requiring stdout exactly `42` for each. It is **not** Graal Native Image AOT.

```bash
# From an independently authorized source-local checkout, or an APPROVED exact-tree private mirror
test "$(git rev-parse HEAD)" = e52172a7acd489d70ae10a62bcf47816b3e7cf32
git rev-parse HEAD^{tree}  # approval blocked until the true tree SHA is independently pinned
mvn -B -ntp -Dtest=OwnershipBangAliasesTest,OwnershipBangGuestTest test
```

Require nonzero Surefire test counts, executed Truffle guest stdout 42 for each mode, and separate GraalVM Native Image receipts before any AOT claim. Missing dependencies or disabled tests are failures, not proof.

## Semantic disagreement requiring a source change and new tests

The [spec #67](https://github.com/ores-truffle-oreslang/attolang-language-spec.md/pull/67) explicitly makes `borrow! mut value` canonical and `mut borrow! value` an accepted compatibility form. At the current [Java #3112](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/3112) head, `OwnershipBangAliasesTest.trailingMutWordOrderIsRejectedLikeMainsLegacyRtBorrowMut` expects canonical `borrow! mut foo` to fail and instructs use of `mut borrow! value`. **This is a verified source-level disagreement, not an executed compiler regression result.** Repair parser / AST / ownership handling and update the negative/positive tests under a separate reviewed source PR; do not weaken the spec or silently change borrow semantics. Verify LLVM and formatter behavior against the same canonical matrix.

The current spec also includes checked-handle aliases `ref!`, `deref!`, and `unref!`. Neither Java/LLVM ownership-bang PR may be called fully conformant until these preserve the existing typed checked-handle contracts or fail closed. The new `block {}` / checked-view loan scope and exceptional-call gate are separate implementation targets, not verified runtime behavior.

## Independent private-source export review packet (NOT APPROVED)

An independent authorized reviewer, distinct from excluded IDs `11139560` and `2148448`, with verified source-repository write authority, must review each CURRENT exact source commit + independently verified complete Git tree + immutable destination repo ID `1458333874` + approved test commands + retention/deletion policy. An organization owner must establish enforceable protected source `main` and CODEOWNER review, reject bypass/force pushes and validate the source-private-CI policy gate. Source and destination owners must review destination access, workflow permissions, secret isolation, ephemeral branches and Git object/artifact retention. Resolve relevant #239 historical transfer-inventory obligations.

After these conditions are met (not before), update source governance through its protected independent review path to enable the exact destination and single SHA/tree snapshot; then verify per-blob identities and full destination tree before any runner checkout. No token-free Git-connector trick waives security policy. Do not make the private repo public, copy secrets, grant a self-review exception, or claim that deleting a Git branch fully erases previously exported objects.

## Required execution receipt / stop conditions

Preserve repository numeric ID, current source head/tree, workflow blob, run ID and attempt, runner ID/name, every required completed executable step, real test counts, logs/exit behavior, full artifact listing and independently downloaded ZIP digest, fixture provenance and explicit admission classification. Reject queued, zero-step, skipped, ambiguous, missing-step, missing-artifact, different-head and first-page-only evidence. `METADATA_CONSISTENT_UNCERTIFIED` is not PASS. Do not merge or publish from this documentation packet.

See the adjacent [machine-readable manifest](evidence/ownership-bang-java-llvm-qualification-20261118.json) for pinned IDs, executable commands, and explicit unknown or unapproved states.
