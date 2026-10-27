param([string]$Source = 'examples/hello.ores')
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sourcePath = (Resolve-Path (Join-Path $root $Source)).Path
$out = Join-Path $root 'artifacts/source-smoke'
$cs = Join-Path $out 'OresProgram.cs'
$fs = Join-Path $out 'OresProgram.fs'
$compiler = Join-Path $root 'compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj'
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($file in @($cs, $fs)) {
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
}

dotnet run --project $compiler -c Release -- emit-csharp $sourcePath --out $cs
if ($LASTEXITCODE -ne 0) { throw 'C# source generation failed' }
dotnet run --project $compiler -c Release -- emit-fsharp $sourcePath --out $fs
if ($LASTEXITCODE -ne 0) { throw 'F# source generation failed' }

dotnet build (Join-Path $root 'verification/source-emission-csharp/OresGenerated.csproj') -c Release -o (Join-Path $out 'csharp') "-p:GeneratedSource=$cs" -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Generated C# did not compile' }
dotnet build (Join-Path $root 'verification/source-emission-fsharp/OresGenerated.fsproj') -c Release -o (Join-Path $out 'fsharp') "-p:GeneratedSource=$fs" -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Generated F# did not compile' }

foreach ($assembly in @(
    (Join-Path $out 'csharp/OresGenerated.dll'),
    (Join-Path $out 'fsharp/OresGenerated.dll')
)) {
    if (-not (Test-Path -LiteralPath $assembly)) {
        throw "Generated managed assembly is missing: $assembly"
    }
}
Write-Host 'Both managed source backends compiled their generated source to CIL.'
Write-Host 'This is not a Native AOT release certification.'
