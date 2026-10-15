# CI recovery and Native AOT evidence

## Observed blocker (2026-10-14)

The Windows x64 Native AOT GitHub Actions jobs have reported `failure` with **zero returned job steps and no usable job logs**. A subsequent `ubuntu-latest` Native AOT diagnostic job also failed with no step data. No C# compiler diagnostic or native binary execution can be inferred from those run results.

Before changing application code in response to a failing runner, examine the individual Actions run in GitHub and the repository/organization's **Actions usage budget, billing status, workflow permissions, and runner availability**. Native AOT on Windows requires a supported Windows host with the .NET 10 SDK and VS C++ build tools.

Never mirror this *private* source repository into a public test repository to obtain Actions minutes. If the source must be mirrored to another GitHub organization, use a private destination controlled by the repository owners, explicit access controls, no secrets, and purge temporary branches after validation.

## Independent no-source runner probe

A temporary private diagnostic PR in `3FA-app/3fa-app-e2e` ran a workflow that only printed `dotnet --info` on `windows-2022` and `ubuntu-latest`. [Run 38658939713](https://github.com/3FA-app/3fa-app-e2e/actions/runs/38658939713) also concluded **failure** for both jobs with **zero returned steps**. No Oreslang source or secrets were copied. The probe PR was closed without merge and its temporary workflow file deleted.

This is consistent with a runner allocation, Actions billing, or organization/platform policy problem affecting more than one repository, **not proof of a particular cause**. Do not repeatedly consume workflow attempts without investigating Actions settings. Check spending and available minutes in each organization and GitHub-hosted runner allocation/status; review the Actions UI for scheduling notices, then retry a simple no-source `dotnet --info` job before full Native AOT compilation.

## Additional private-org probes (2026-10-14)

Three further private GitHub repositories were probed with a dedicated
`pull_request` workflow that runs only `dotnet --info` on Windows 2022
and Ubuntu. Neither code nor credentials from Oreslang were copied.

| Probe repository | PR (closed unmerged) | Probe Actions run | Outcome |
|---|---|---|---|
| `gha-indie-worker/gha-indie-worker-docs` | [#34](https://github.com/gha-indie-worker/gha-indie-worker-docs/pull/34) | [38677489247](https://github.com/gha-indie-worker/gha-indie-worker-docs/actions/runs/38677489247) | Windows and Linux jobs failed, zero reported steps |
| `messaging-intel/msgint-e2e` | [#60](https://github.com/messaging-intel/msgint-e2e/pull/60) | [38677493653](https://github.com/messaging-intel/msgint-e2e/actions/runs/38677493653) | Windows and Linux jobs failed, zero reported steps |
| `akrion-sim/akrion-monorepo` | [#49](https://github.com/akrion-sim/akrion-monorepo/pull/49) | [38677497705](https://github.com/akrion-sim/akrion-monorepo/actions/runs/38677497705) | Windows and Linux jobs failed, zero reported steps |

The temporary workflows were deleted from their probe branches, and the
three draft PRs were closed **without merge**. Temporary branches may
still exist; do not reuse them for production work.

**Important:** The connector's commit-based workflow-run listing returns
PR-triggered runs only. Push-only probe runs are not visible through that
lookup. The outcomes above are based on the actual PR-triggered runs and job
step inspections, not on missing push-run results.

The available GitHub tools do **not** expose organization Actions spending,
minutes remaining, or billing settings. Therefore no org can be declared out
of budget from these results, and no alternative org is known to have available
working runners. The job-log redirects returned `BlobNotFound` for the source
run, consistent with the absence of executed steps but not diagnostic of cause.

### Safe cross-organization retry gate

1. An owner or billing admin first checks Actions spending, billing,
   organization policy and runner allocation for the candidate org.
2. The candidate **private** repository must pass a minimal Windows
   and Linux no-source, no-secret PR workflow on its own runners.
3. Only after a successful probe may a short-lived **private** source
   mirror be considered; use an isolated repo, minimal GitHub workflow
   permissions, no secrets or credentials, and an exact source-commit
   manifest. Mirror the complete Git tree, not text files only (binary
   assets, executable modes and symlinks must be retained).
4. Never mirror to a public destination, leak repo tokens to workflows,
   or run a source branch from a different org through a normal
   `GITHUB_TOKEN` checkout. Revalidate the staged tree's commit/content
   hashes before executing, and delete temporary branches afterward.

No source mirror was created during this audit because **none** of the
candidate runner probes passed.

## Exact-commit Windows validation without a cross-org mirror

A source-repository maintainer with a clean, isolated Windows x64 host
(.NET 10 SDK, native C++ toolchain and Native AOT prerequisites) can run
the single-command strict release gate on a **reviewed immutable PR #4 SHA**:

```powershell
# Supply the independently reviewed exact 40-character commit SHA.
# Do not derive this expected value from a mutable branch in the script.
$reviewedSha = '<reviewed-40-character-PR4-SHA>'
git fetch origin refs/pull/4/head
git checkout --detach $reviewedSha
./verification/verify-exact-sha-windows-aot.ps1 -ExpectedCommit $reviewedSha
```

The script rejects a mismatched SHA, modified tracked source, unsupported
operating system, or non-.NET-10 SDK. It compiles/executes the managed suite,
publishes and checks the strict native compiler and arithmetic application,
requires native arithmetic result `14`, verifies the native CLI and checked
arithmetic negative fixtures, and separately publishes/executes the actual
Windows Native AOT CFG swap application expecting `2211`. It uses a fresh
temporary directory for the CFG DLL/native executable and deletes it after
the run. It does **not** grant a CI bypass or substitute for independent
review/required originating-repository checks. This is a committed
verification command, **not evidence that the command has succeeded yet**.

Track administration, runner allocation and exact-head release proof in
[.NET CI blocker #5](https://github.com/ores-truffle-oreslang/oreslang-source.net/issues/5).

## Local Windows release verification

From a Windows x64 host with Native AOT prerequisites installed:

```powershell
dotnet --info
dotnet run --project verification/Ores.Net.Tests/Ores.Net.Tests.csproj -c Release
./scripts/publish-windows.ps1 -Source examples/hello.ores -Rid win-x64
./verification/assert-native-pe.ps1 -Binary ./artifacts/compiler/win-x64/oresnet.exe -Rid win-x64
./verification/assert-native-pe.ps1 -Binary ./artifacts/apps/win-x64/oreslang-app.exe -Rid win-x64
$output = & ./artifacts/apps/win-x64/oreslang-app.exe
if ($LASTEXITCODE -ne 0 -or $output.Trim() -ne '14') { throw "Invalid native binary result: $output" }
```

**Release only if the final compiler and app are Native AOT**, the PE inspection finds no CLR header or managed-runtime sidecars, and the Windows executable actually runs. Managed `dotnet run` during test development is permitted; managed runtime binaries are **not** released.

## Independent diagnostics

The workflow includes `linux-aot-diagnostic` to test the CIL metadata generator and statically linked Native AOT host on Linux when Windows runners are unavailable. Linux results **do not authorize Windows release**. The Windows x64 job remains a required release gate and ARM64 must be tested separately.

## Triage order

1. Distinguish job scheduling/organization billing failure from a job that actually reaches `actions/checkout`.
2. Once steps run, capture `dotnet --info`, restore/build diagnostics, and ILCompiler native-link errors.
3. Run parser, CIL and mailbox tests; demand a passing 2,048-operand expression regression.
4. Verify real Native AOT PE images; reject both managed apphosts and accidentally copied `OresGenerated.dll`.
5. Add separate Native AOT negative tests for overflow and division-by-zero exit status before closing bootstrap.
6. Keep [PR #1](https://github.com/ores-truffle-oreslang/oreslang-source.net/pull/1) draft and unmerged until evidence is collected.
