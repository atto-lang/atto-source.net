# Verification-only: generate checked CFG from typed Oreslang structured
# source, statically link it into Native AOT, and run the real win-x64 EXE.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('ores-net-structured-native-' + [guid]::NewGuid().ToString('N'))
$generated = Join-Path $work 'OresGenerated.dll'
$outputDir = Join-Path $work 'publish'
$hostProject = 'host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj'
$previous = $env:ORESNET_CFG_SOURCE_FIXTURE
New-Item -ItemType Directory -Path $work -Force | Out-Null
Push-Location $root
try {
    $env:ORESNET_CFG_SOURCE_FIXTURE = 'sum45'
    dotnet run --project verification/Ores.Net.CfgFixtures/Ores.Net.CfgFixtures.csproj -c Release -- $generated
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $generated)) {
        throw 'Structured source CFG generator failed'
    }
    Remove-Item Env:ORESNET_CFG_SOURCE_FIXTURE -ErrorAction SilentlyContinue
    dotnet clean $hostProject -c Release -r win-x64 "-p:GeneratedAssembly=$generated"
    if ($LASTEXITCODE -ne 0) { throw 'Structured CFG Native AOT clean failed' }
    dotnet publish $hostProject -c Release -r win-x64 -o $outputDir "-p:GeneratedAssembly=$generated" -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Structured CFG Native AOT publish failed' }
    $exe = Join-Path $outputDir 'oreslang-app.exe'
    & ./verification/assert-native-release.ps1 -Binary $exe -Rid win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Structured CFG native release gate failed' }
    $value = & $exe
    if ($LASTEXITCODE -ne 0 -or ($value | Out-String).Trim() -cne '45') {
        throw "Structured source Native AOT expected 45, got '$value'"
    }
    Write-Host 'Verified Windows Native AOT structured-source sum45: 45'
}
finally {
    if ($null -eq $previous) {
        Remove-Item Env:ORESNET_CFG_SOURCE_FIXTURE -ErrorAction SilentlyContinue
    } else {
        $env:ORESNET_CFG_SOURCE_FIXTURE = $previous
    }
    Pop-Location
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
