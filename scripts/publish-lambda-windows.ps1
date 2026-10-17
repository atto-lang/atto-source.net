param(
    [Parameter(Mandatory = $true)][string]$Source,
    [ValidateSet('win-x64', 'win-arm64')][string]$Rid = 'win-x64',
    [string]$Output = 'artifacts/lambda-apps'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourcePath = (Resolve-Path $Source).Path
$generated = Join-Path $root 'artifacts/generated/OresGenerated.dll'
$compilerOut = Join-Path $root "artifacts/compiler/$Rid"
$checkNative = Join-Path $root 'verification/assert-native-release.ps1'
$publishOut = Join-Path $root "$Output/$Rid"
New-Item -ItemType Directory -Force (Split-Path -Parent $generated) | Out-Null

$compiler = Join-Path $compilerOut 'oresnet.exe'
$binary = Join-Path $publishOut 'oreslang-lambda-app.exe'
Remove-Item -LiteralPath $compiler -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $binary -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $generated -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj') -c Release -r $Rid -o $compilerOut -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Native compiler publish failed' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Native compiler executable was not published' }
& $checkNative -Binary $compiler -Rid $Rid

& $compiler compile-lambda $sourcePath --out $generated
if ($LASTEXITCODE -ne 0) { throw 'Oreslang Lambda-to-CIL compilation failed' }

dotnet publish (Join-Path $root 'host/Ores.Net.LambdaNativeHost/Ores.Net.LambdaNativeHost.csproj') -c Release -r $Rid -o $publishOut "-p:GeneratedAssembly=$generated" -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Lambda Native AOT publish failed' }

if (-not (Test-Path -LiteralPath $binary)) { throw "Expected Lambda native executable is missing: $binary" }
& $checkNative -Binary $binary -Rid $Rid
if (Test-Path (Join-Path $publishOut 'OresGenerated.dll')) { throw 'Managed generated assembly was copied to Lambda native publish output' }
if (Test-Path (Join-Path $publishOut 'oreslang-lambda-app.runtimeconfig.json')) { throw 'Framework-dependent Lambda runtime configuration unexpectedly found' }

if ((Split-Path -Leaf $sourcePath) -eq 'lambda-ping.ores') {
    $smoke = & $binary --invoke ping
    if ($LASTEXITCODE -ne 0 -or $smoke -ne 'pong') { throw "Lambda NativeAOT smoke failed: expected pong, got '$smoke'" }
}

Write-Host "Lambda Native AOT executable: $binary"
