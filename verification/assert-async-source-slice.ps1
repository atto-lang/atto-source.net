param([Parameter(Mandatory = $true)][string]$Compiler)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Compiler = (Resolve-Path $Compiler).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('oresnet-async-source-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
try {
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $file = Join-Path $root 'guest.ores'
    $guest = 'fnc main(): Future<int> { return Future.from_task(task { return 42; }); }'
    [IO.File]::WriteAllText($file, $guest, $encoding)
    $output = & $Compiler run-async $file 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $output.Trim() -cne '42') {
        throw "NativeAOT guest slice execution failed: exit=$LASTEXITCODE output=$output"
    }
    Write-Output 'NET_ASYNC_SOURCE_SLICE_EXECUTED; value=42; producer_count=1; runtime_proof=managed_tests; guest_cil_verified=false'

    # No false positive from the legacy CIL compiler: Task lowering there
    # remains unsupported until generated RunAsync() and NativeAOT host proof.
    $assembly = Join-Path $root 'OresGenerated.dll'
    $rejection = & $Compiler compile $file --out $assembly 2>&1 | Out-String
    if ($LASTEXITCODE -ne 1 -or
        $rejection -cnotmatch 'execution task requires scheduler-aware Future/continuation lowering' -or
        (Test-Path $assembly)) {
        throw "Unverified CIL path was admitted: exit=$LASTEXITCODE output=$rejection"
    }
    Write-Output 'NET_ASYNC_CIL_REJECTED; exit=1; assembly_exists=false'

    # Negative grammar/effect checks must reject instead of executing an
    # unsupported alias, type mismatch, expression, or actor callback.
    foreach ($invalid in @(
        $guest.Replace('return 42;', 'return 41+1;'),
        $guest.Replace('Future.from_task', 'Future.new'),
        $guest.Replace('Future.from_task', 'Task.from_future'),
        $guest.Replace('Future<int>', 'int'),
        $guest + ' fnc hidden():int{return 1;}'
    )) {
        [IO.File]::WriteAllText($file, $invalid, $encoding)
        $rejection = & $Compiler run-async $file 2>&1 | Out-String
        if ($LASTEXITCODE -ne 1 -or $rejection -cnotmatch 'oresnet:') {
            throw "Unsupported source was admitted: $invalid exit=$LASTEXITCODE output=$rejection"
        }
    }
    Write-Output 'NET_ASYNC_SLICE_NEGATIVE_DIAGNOSTICS; cases=5; executable_guest_cil=false'
    $global:LASTEXITCODE = 0
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
