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

`tests/fixtures/path_sync_telemetry_policy_v1.atto` contains 49 sequential
failure codes; it is a candidate, **not a compiled/guest-executed receipt**.
The linked Atto guest test, NativeAOT result, cancellation/lock race test and
OTLP integration are unverified. Unsupported host features must fail closed.

Other independent candidates: Java PR #1756, LLVM PR #883. No claim of
full interoperability or byte-identical source.

## Follow-up audit and exact-source runner status (2026-11-22)
A new source audit verified that every fixture method exists in its backend-owned `.atto` module and no host-language source islands occur in those modules. Path scans reject unpaired Unicode surrogate scalars and values outside Unicode's maximum scalar range. The Java/.NET path views now scan one bounded Unicode snapshot per call and avoid temporary component arrays. Sync release and upgrade policy require an authenticated, unconsumed owner/generation receipt from a trusted runtime, not guest-created boolean authority.

**Verification boundary:** Guest fixtures are authored, not executed. Exact-current-head Actions jobs were queued or pending with no recorded runner steps when checked. Private-source export is not permitted merely by naming a carrier: Java has no active reviewed destination; .NET's candidate remains inactive; LLVM's stale StreemPilot allowlist entry is explicitly under the source-security hold described by LLVM #726/#736/#778. No private-source mirror was performed. Existing draft PRs must remain draft until stepful native guest results and all required reviews are available.
