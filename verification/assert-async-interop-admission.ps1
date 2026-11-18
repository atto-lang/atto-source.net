param([Parameter(Mandatory = $true)][string]$Compiler)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Compiler = (Resolve-Path $Compiler).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('oresnet-interop-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
try {
    $source = Join-Path $root 'input.ores'
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $positive = Join-Path $root 'positive/OresGenerated.dll'
    $null = New-Item -ItemType Directory -Path (Split-Path $positive)
    [IO.File]::WriteAllText($source, 'fnc main():int{return 42;}', $encoding)
    & $Compiler compile $source --out $positive
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $positive -PathType Leaf)) {
        throw 'Native compiler positive control did not emit CIL'
    }
    $case = 0
    foreach ($trivia in @(' ', '/* outer /* inner */ end */', "// gap`n", "// gap`r",
        "// gap`r`n", "// gap$([char]0x85)", "// gap$([char]0x2028)", "// gap$([char]0x2029)")) {
        $target = Join-Path $root "$case/OresGenerated.dll"
        $null = New-Item -ItemType Directory -Path (Split-Path $target)
        [IO.File]::WriteAllText($source,
            ('fnc main():int{return Future.from_task(task' + $trivia + '{return 42;});}'), $encoding)
        $output = & $Compiler compile $source --out $target 2>&1 | Out-String
        $exit = $LASTEXITCODE
        if ($exit -ne 1 -or $output -cnotmatch 'execution task requires scheduler-aware Future/continuation lowering') {
            throw "Native compiler missed targeted Task-boundary rejection in case $case (exit $exit): $output"
        }
        if (Test-Path $target) { throw "Unsupported interop emitted assembly in case $case" }
        Write-Output "NET_INTEROP_ADMISSION_CASE=$case; exit=1; assembly_exists=false"
        $case++
    }
    if ($case -ne 8) { throw 'Incomplete native compiler rejection corpus' }
    Write-Output 'NET_INTEROP_NATIVE_COMPILER_CONTAINMENT_EXECUTED; cases=8; guest_interop_verified=false'
    $global:LASTEXITCODE = 0 # Expected negative compiler exits are successful assertions.
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
