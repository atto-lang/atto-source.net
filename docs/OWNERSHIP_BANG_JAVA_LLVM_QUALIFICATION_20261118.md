# Ownership-bang: exact-head Java / LLVM qualification and independent export review packet

> PREPARATION ONLY / NOT_EXECUTED / PRIVATE EXPORT DENIED. This packet is not an authorized mirror request, compiler receipt, code-review approval or release certificate.

Prepared on 2026-11-19 UTC. Source-local documentation only. Existing [runner recovery runbook](OWNERSHIP_BANG_RUNNER_RECOVERY_20261116.md) and [historical head ledger](evidence/ownership-bang-20261116-source-head-ledger.json) remain authoritative background, not current execution proof. See [runner admission #427](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/427), [governance #260](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/260), and [historical transfer inventory #239](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/239).

## Current correction PR heads (updated 2026-11-19 UTC)

The earlier #3112 / #1831 entries below are **merged baseline heads**, not the current correction PR heads. Test / approve only the **current exact heads** for these open changes:

| Source correction | Current exact PR head | Root tree | Executed compiler/guest evidence |
| --- | --- | --- | --- |
| [Java draft #3186](https://github.com/ores-truffle-oreslang/oreslang-source.java/pull/3186) | `176694acbbc09c365f434e906e7ed3c035a95193` | UNVERIFIED | NOT_EXECUTED |
| [LLVM draft #1903](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/pull/1903) | `d8291593d94d7cf5f73eebe3a755d4ede3c4604a` | UNVERIFIED | NOT_EXECUTED |

Both PRs were source-locally reconciled and last reported mergeable, but they are deliberately **draft and unmerged**. Java includes three SHA-pinned mutable-borrow `.atto` guest fixtures; LLVM includes the SHA-pinned legacy `rt borrow mut` native guest under default, O0 and O2 CTest registrations. The source-only readback checks are **not compiler execution**. The requested Java test command is `mvn -B -ntp -Dtest=OwnershipBangAliasesTest,OwnershipBangGuestTest test`; LLVM's command is listed below. After any head change, re-pin this packet before qualification.

**Source runner admission evidence:** A selected job re-run was accepted in each source repo, but GitHub created a new failed job with `steps=[]`, zero artifacts, and a job-log download returning `404 BlobNotFound`. Java [run 44430114082](https://github.com/ores-truffle-oreslang/oreslang-source.java/actions/runs/44430114082) job `135247159045` is on the **superseded** Java head `bf020ce7c90e0cce21c25b8c3700780eaf6d41f0`; it **cannot** certify Java #3186's new head. LLVM [run 44429270289](https://github.com/ores-truffle-oreslang/oreslang-source.llvm/actions/runs/44429270289), job `135247173762`, is on LLVM's current head, but has no executable steps. The admission-denial root cause remains **UNCONFIRMED**, and private-source export remains **DENIED**.

The current machine-readable targets are in `current_followup_pr_targets` in the adjacent JSON. The historic `targets` field remains for provenance of the merged baseline heads; do not substitute an earlier commit's Git tree for either new feature head.

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
