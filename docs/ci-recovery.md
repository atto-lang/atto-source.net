# CI recovery and Native AOT evidence

## Observed blocker (2026-10-14)

The Windows x64 Native AOT GitHub Actions jobs have reported `failure` with **zero returned job steps and no usable job logs**. A subsequent `ubuntu-latest` Native AOT diagnostic job also failed with no step data. No C# compiler diagnostic or native binary execution can be inferred from those run results.

Before changing application code in response to a failing runner, examine the individual Actions run in GitHub and the repository/organization's **Actions usage budget, billing status, workflow permissions, and runner availability**. Native AOT on Windows requires a supported Windows host with the .NET 10 SDK and VS C++ build tools.

Never mirror this *private* source repository into a public test repository to obtain Actions minutes. If the source must be mirrored to another GitHub organization, use a private destination controlled by the repository owners, explicit access controls, no secrets, and purge temporary branches after validation.

## Independent no-source runner probe

A temporary private diagnostic PR in `3FA-app/3fa-app-e2e` ran a workflow that only printed `dotnet --info` on `windows-2022` and `ubuntu-latest`. [Run 38658939713](https://github.com/3FA-app/3fa-app-e2e/actions/runs/38658939713) also concluded **failure** for both jobs with **zero returned steps**. No Oreslang source or secrets were copied. The probe PR was closed without merge and its temporary workflow file deleted.

This is consistent with a runner allocation, Actions billing, or organization/platform policy problem affecting more than one repository, **not proof of a particular cause**. Do not repeatedly consume workflow attempts without investigating Actions settings. Check spending and available minutes in each organization and GitHub-hosted runner allocation/status; review the Actions UI for scheduling notices, then retry a simple no-source `dotnet --info` job before full Native AOT compilation.

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
