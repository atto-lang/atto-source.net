# macOS ARM64 NativeAOT guest qualification (candidate, 2026-11-02)

This branch adds an **unverified** native Apple Silicon release lane. The
release target is `osx-arm64`; the new `macos-15` GitHub-hosted job must
execute before any native proof is claimed.

## Scope and positive evidence required

- Verify the checked-out commit is the exact PR head; record the source Git
  tree SHA, fixture SHA-256, output SHA-256, run ID and run attempt.
- Run the native source-compiler CLI `oresnet` built by
  `dotnet publish -r osx-arm64`; produce generated CIL for a verified,
  self-contained `.ores` fixture.
- Statically link `OresGenerated.dll` into a distinct NativeAOT host image.
  Generated CIL is a **build-time input only**, never a runtime DLL.
- Inspect the native compiler and both app outputs for native Mach-O arm64
  headers and no `.dll`, `.deps.json`, or `.runtimeconfig.json` sidecars.
- Execute `hello.ores` to produce 14 and the shared `add.ores` fixture to
  produce 42 with an environment whose PATH contains no dotnet toolchain.
  Re-run the first image after publishing the second to detect stale source
  reuse. Reject unsupported actor input without producing CIL.
- Exclude unexpected managed-runtime linked libraries. Capture the exact-head
  receipt as an uploaded workflow artifact **only after** real native
  execution succeeds.

The macOS header verifier's C fixture and negative-path tests are **not**
guest compilation, not NativeAOT evidence, and not a zero-GC/actor proof.
PowerShell/MSBuild target admission merely validates settings, not that a
particular OS/architecture build works. The source-level compiler still
supports a limited scalar subset; this change does not admit unsupported
classes/actors/async constructs.

## Safety and governance

- The publisher rejects output paths that are outside dedicated `artifacts/`
  paths and symlinked output ancestors. It rejects out-of-tree or symlinked
  source files. It removes stale app/compiler/CIL outputs before publication.
- CI uses immutable checkout/setup-dotnet/upload-artifact action SHAs,
  `persist-credentials: false`, and read-only contents permissions.
- The receipt explicitly sets `independently_verified: false`. It must only
  be promoted after a nonzero-step exact-head GitHub Actions run with working
  logs, reviewed binary identities, source approvals and any required branch
  protection. A `steps: null` admission failure establishes none of these.
- Workflows use the source organization's own runners. Private source must
  not be mirrored to a different organization without the prerequisite
  governance approvals and tree/blob attestation.

## Follow-up

Qualification remains open for `osx-x64`, `linux-arm64` and
`win-arm64` in addition to this `osx-arm64` lane. Adding a RID to the
strict-publish allowlist is only a static admission policy, not a claim of
tested native builds. Other backends (Java/GraalVM, LLVM, Wasm and the
independent Cranelift path) must produce their **own** compiler-backed
guest/OS evidence, not inherit this .NET test result.
