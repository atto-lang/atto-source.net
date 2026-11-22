# .NET NativeAOT native Atto std/path, std/sync and std/telemetry

This backend owns its own `.atto` modules `stdlib/path.atto`,
`stdlib/sync.atto` and `stdlib/telemetry.atto`. These are deliberately
independent of the Java and LLVM files. The lexical path parser accepts
root-relative input and computes normalized **depth** with traversal denial;
it is NOT a canonical path serializer, nor a safe native filesystem opener.
The Windows mode conservatively rejects DOS device aliases, alternate data
streams, trailing dots, absolute/drive paths, control codes and overly long
components. Symlink resolution and root confinement require trusted host FS
handles and capability checks.

The path source additionally exposes bounded normalized component start,
length, and Unicode scalar views into the original input. These are not a
native normalized path string or filesystem handles.

Synchronization code supplies pure admissions for mutex, writer-priority
RW lock, semaphore, barriers, generation advance and cancellation. It does
NOT provide an atomic/linearizable lock, shared mutable memory access,
scheduler blocking, wakes or actual cancellation. A source function
returning `true` must never grant a lock independently of trusted runtime.

Telemetry adds native Atto W3C-v00 **header grammar validation** to the
existing decoded-field/policy model. It enforces lowercase hex, exact length,
delimiters and nonzero trace/parent IDs; it neither authenticates nor
installs a runtime trace context. NativeAOT only permits toggling already
compiled probe sites. OTLP egress is not part of these files.

`tests/fixtures/path_sync_telemetry_policy_v1.atto` contains 65 sequential
failure codes; it is a candidate, **not a compiled/guest-executed receipt**.
The linked Atto guest test, NativeAOT result, cancellation/lock race test and
OTLP integration are unverified. Unsupported host features must fail closed.

Other independent candidates: Java PR #1756, LLVM PR #883. No claim of
full interoperability or byte-identical source.

## Follow-up audit and exact-source runner status (2026-11-22)
A new source audit verified that every fixture method exists in its backend-owned `.atto` module and no host-language source islands occur in those modules. Path scans reject unpaired Unicode surrogate scalars and values outside Unicode's maximum scalar range. The Java/.NET path views now scan one bounded Unicode snapshot per call and avoid temporary component arrays. Sync release and upgrade policy require an authenticated, unconsumed owner/generation receipt from a trusted runtime, not guest-created boolean authority.

**Verification boundary:** Guest fixtures are authored, not executed. Exact-current-head Actions jobs were queued or pending with no recorded runner steps when checked. Private-source export is not permitted merely by naming a carrier: Java has no active reviewed destination; .NET's candidate remains inactive; LLVM's stale StreemPilot allowlist entry is explicitly under the source-security hold described by LLVM #726/#736/#778. No private-source mirror was performed. Existing draft PRs must remain draft until stepful native guest results and all required reviews are available.

### Native Path value and remaining .NET backend gap
Added an immutable `define pub class Path` and `relative_path()` factory using Atto source. It exposes lexical component views and filename/extension/parent/join admission; it does not create trusted .NET `System.IO.Path` or an opened handle. The source guest fixture now has 65 checks. The current .NET typed module linker is still a scalar int/bool slice and does **not** certify compilation of this String/class-bearing Path module: keep the guest and NativeAOT verdict NOT_EXECUTED. A closed trusted std registry and native heap/ownership lowering remain prerequisites.

### Exact-head source-only admission workflow
`verification/atto-std-path-sync-telemetry-source-audit.mjs` statically checks the full 65 sequential guest failure codes, exported native `.atto` calls, 12 invoked Path members, source size/UTF-8/regular-file constraints, exact Git source blobs, and four nontrivial mutation-negative cases. `.github/workflows/atto-std-path-sync-telemetry-source-audit.yml` runs it source-local and explicitly requires a `--require-executed-guest` request to fail. This does **not** establish .NET guest compilation or NativeAOT executable proof; those remain blocked on typed linker support and stepful CI.


## Source audit mutation hardening (2026-11-22)

The `Path` value now follows the existing `pub define class` syntax and the source-only audit checks this declaration. The Node source audit was exercised against the latest connector-retrieved inputs with **65/65** sequential checked exits, 51 resolved native Atto module calls and 12 invoked instance methods. Five negative mutations were rejected, including an injected early `return 0;` that previously could bypass the 65 assertions without changing their count. This is **source-only validation, not Atto execution**.

A source-free GitHub probe in `honeypot-r-us-test/hnpt-e2e` (run 44859823076) reported zero executed Linux and Windows steps. The source-org tests also lack stepful NativeAOT evidence. Its carrier remains explicitly inactive pending the independent reviewed source-transfer allowlist.

## 134-vector five-backend standard-library ABI convergence (2026-11-22)

The backend-owned `stdlib/path.atto`, `sync.atto` and `telemetry.atto` now expose all **28 public scalar/boolean policy signatures** from [atto-platform-conformance #577](https://github.com/atto-lang/atto-platform-conformance/pull/577), **without removing the backend's existing higher-level path, lock or telemetry APIs**. Existing private path-component array validation was renamed to `checked_component_kind_points` to avoid an ambiguous arity/name collision with the new public scalar `component_kind` API.

The shared *data-only* contract is pinned by exact Git blob `3d9e9d5308a4cd0a99d811570f8c80e77b7219b7` under `contracts/atto-std-pure-policy-parity-v1.json`. The implementation remains locally owned by .NET NativeAOT; other compilers can use distinct `.atto` source files. `verification/atto-std-134-source-model.test.mjs` evaluates a deliberately restricted policy subset: current source matched **134/134** vectors and all 28 typed signatures; five source mutation-negative tests reject altered visibility, separators, permit release, sampling and an injected host escape. The script also checks the separate 134-case `.atto` guest candidate is an exact derivation of the pinned contract. **This is not Atto compiler execution.**

`.github/workflows/atto-std-134-source-model.yml` requires the exact checkout SHA, source blob identities, pinned contract hash, source model tests and a negative check that `ATTO_REQUIRE_NATIVE_GUEST=1` must fail. The previously existing backend-specific guest fixture and tests remain unchanged. .NET NativeAOT native linking/execution (JDK 25/GraalVM Native Image or .NET CIL/NativeAOT, respectively) remains **NOT_EXECUTED** and is not implied by this source-model alignment.

No private source was copied into another GitHub organization. Source-local CI admission and independently approved private carrier remain release blockers. Keep PR draft.


## Real compiler-owned namespace denial and CIL positive control

The focused `verification/Atto.Std.Policy.CompilerAdmission` C# host invokes the actual .NET structured source-module compiler. It requires nine reserved `std/*` or alias imports to fail despite local shadow files, then builds an explicitly relative `./safe_math` source graph and **emits an actual CIL library** using `CilEmitter.EmitLibrary`. The source-only CI workflow requires its `NET_STD_COMPILER_IMPORT_DENIAL=EXECUTED|reserved=9|local_module_graph=CIL_EMITTED` marker on a real runner. This proves neither compiler-owned std linking nor a native `.atto` guest; the complete 134-case guest and NativeAOT execution remain **NOT_EXECUTED**.
