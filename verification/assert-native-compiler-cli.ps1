param(
    [Parameter(Mandatory = $true)][string]$Compiler,
    [Parameter(Mandatory = $true)][string]$ValidSource
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$binary = (Resolve-Path -LiteralPath $Compiler).Path
$source = (Resolve-Path -LiteralPath $ValidSource).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("oresnet-cli-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

function Assert-Exit {
    param([int]$Expected, [string[]]$Arguments, [string]$Case)
    # Prevent intentional nonzero native process codes from terminating PowerShell.
    $output = (& $binary @Arguments 2>&1 | Out-String)
    $code = $LASTEXITCODE
    if ($code -ne $Expected) {
        throw "$Case: expected exit $Expected, got $code. Output: $output"
    }
}

try {
    Assert-Exit -Expected 2 -Arguments @() -Case 'no args'
    Assert-Exit -Expected 2 -Arguments @('compile', $source) -Case 'missing --out'
    Assert-Exit -Expected 2 -Arguments @('check', $source, '--out', 'ignored') -Case 'check extra args'
    Assert-Exit -Expected 2 -Arguments @('compile', $source, '--other', 'ignored') -Case 'unknown flag'
    Assert-Exit -Expected 0 -Arguments @('check', $source) -Case 'valid check'

    $oversize = Join-Path $work 'oversize.ores'
    [IO.File]::WriteAllBytes($oversize, [byte[]]::new(1048577))
    Assert-Exit -Expected 1 -Arguments @('check', $oversize) -Case 'oversize'

    $badUtf8 = Join-Path $work 'invalid-utf8.ores'
    [IO.File]::WriteAllBytes($badUtf8, [byte[]]@(0xFF, 0xFE, 0xFF))
    Assert-Exit -Expected 1 -Arguments @('check', $badUtf8) -Case 'invalid UTF8'

    $invalidSource = Join-Path $work 'invalid.ores'
    [IO.File]::WriteAllText($invalidSource, 'fnc main():int {return 1 @ 3;}')
    $out = Join-Path $work 'OresGenerated.dll'
    Assert-Exit -Expected 1 -Arguments @('compile', $invalidSource, '--out', $out) -Case 'invalid syntax'
    if (Test-Path -LiteralPath $out) {
        throw 'Invalid source unexpectedly generated CIL'
    }

    Assert-Exit -Expected 0 -Arguments @('compile', $source, '--out', $out) -Case 'valid compile'
    if (-not (Test-Path -LiteralPath $out)) {
        throw 'Valid source failed to produce assembly'
    }
    Write-Host 'Native compiler CLI negative and positive tests passed'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
