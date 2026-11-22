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

`tests/fixtures/path_sync_telemetry_policy_v1.atto` contains 18 sequential
failure codes; it is a candidate, **not a compiled/guest-executed receipt**.
The linked Atto guest test, NativeAOT result, cancellation/lock race test and
OTLP integration are unverified. Unsupported host features must fail closed.

Other independent candidates: Java PR #1756, LLVM PR #883. No claim of
full interoperability or byte-identical source.
