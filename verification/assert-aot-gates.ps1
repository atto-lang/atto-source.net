param(
    [ValidateSet('win-x64', 'linux-x64')][string]$Rid = 'win-x64'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
$projects = @(
    (Join-Path $root 'compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj'),
    (Join-Path $root 'host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj')
)

function Assert-Gate {
    param([string]$Project, [string]$Label, [bool]$Success, [string[]]$Overrides)
    $arguments = @('msbuild', $Project, '-target:VerifyStrictNativeAotPublish',
        '-property:PublishAot=true', '-property:SelfContained=true',
        ('-property:RuntimeIdentifier=' + $Rid), '-property:TargetFramework=net10.0')
    $arguments += $Overrides
    $null = & dotnet @arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if (($exitCode -eq 0) -ne $Success) {
        throw "$([IO.Path]::GetFileName($Project)) $Label: unexpected exit $exitCode"
    }
}

foreach ($project in $projects) {
    Assert-Gate -Project $project -Label 'valid Native AOT' -Success $true -Overrides @()
    Assert-Gate -Project $project -Label 'managed/JIT publish' -Success $false -Overrides @('-property:PublishAot=false')
    Assert-Gate -Project $project -Label 'framework-dependent publish' -Success $false -Overrides @('-property:SelfContained=false')
    Assert-Gate -Project $project -Label 'unsupported target RID' -Success $false -Overrides @('-property:RuntimeIdentifier=osx-arm64')
    Assert-Gate -Project $project -Label 'wrong framework' -Success $false -Overrides @('-property:TargetFramework=net9.0')
}
Write-Host 'Strict Native AOT MSBuild release-gate checks passed'
