param(
    [Parameter(Mandatory = $true)][string]$Source,
    [ValidateSet('win-x64', 'win-arm64')][string]$Rid = 'win-x64',
    [string]$Output = 'artifacts/apps'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourcePath = (Resolve-Path $Source).Path
$generated = Join-Path $root 'artifacts/generated/OresGenerated.dll'
$compilerOut = Join-Path $root "artifacts/compiler/$Rid"
$checkNative = Join-Path $root 'verification/assert-native-pe.ps1'
$publishOut = Join-Path $root "$Output/$Rid"
New-Item -ItemType Directory -Force (Split-Path -Parent $generated) | Out-Null

# Only the native compiler executable is run in the distributed build path.
dotnet publish (Join-Path $root 'compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj') -c Release -r $Rid -o $compilerOut -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Native compiler publish failed' }

$exe = Join-Path $compilerOut 'oresnet.exe'
& $checkNative -Binary $exe -Rid $Rid
& $exe compile $sourcePath --out $generated
if ($LASTEXITCODE -ne 0) { throw 'Oreslang-to-CIL compilation failed' }

dotnet publish (Join-Path $root 'host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj') -c Release -r $Rid -o $publishOut "-p:GeneratedAssembly=$generated" -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Native AOT publish failed' }

$binary = Join-Path $publishOut 'oreslang-app.exe'
if (-not (Test-Path $binary)) { throw "Expected native executable is missing: $binary" }
& $checkNative -Binary $binary -Rid $Rid
if (Test-Path (Join-Path $publishOut 'OresGenerated.dll')) {
    throw 'Managed generated assembly was copied to native publish output'
}
if (Test-Path (Join-Path $publishOut 'oreslang-app.runtimeconfig.json')) {
    throw 'Framework-dependent runtime configuration unexpectedly found'
}
Write-Host "Native AOT executable: $binary"
