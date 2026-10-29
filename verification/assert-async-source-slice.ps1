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
        $rejection -cnotmatch 'oresnet:' -or
        (Test-Path $assembly)) {
        throw "Unverified CIL path was admitted: exit=$LASTEXITCODE output=$rejection"
    }
    Write-Output 'NET_ASYNC_CIL_REJECTED; exit=1; assembly_exists=false'

    # The updated main branch also exposes experimental structured and source
    # emission commands. None may bypass the Task/Future fail-closed gate.
    $modes = @('check', 'check-structured', 'compile-structured', 'emit-csharp', 'emit-fsharp')
    # A broken/disabled CLI command must not be mistaken for a fail-closed
    # async admission result. First prove every command accepts the known
    # supported scalar fixture, and emits a real artifact when applicable.
    $supported = Join-Path $root 'scalar.ores'
    [IO.File]::WriteAllText($supported, 'fnc main():int{return 42;}', $encoding)
    $supportedOut = Join-Path $root 'supported'
    $null = New-Item -ItemType Directory -Path $supportedOut
    foreach ($mode in $modes) {
        $expectedArtifact = switch ($mode) {
            'compile-structured' { Join-Path $supportedOut 'OresGenerated.dll' }
            'emit-csharp' { Join-Path $supportedOut 'OresProgram.cs' }
            'emit-fsharp' { Join-Path $supportedOut 'OresProgram.fs' }
            default { $null }
        }
        $supportedArgs = if ($null -eq $expectedArtifact) {
            @($mode, $supported)
        } else {
            @($mode, $supported, '--out', $expectedArtifact)
        }
        $supportedOutput = & $Compiler @supportedArgs 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or
            ($null -ne $expectedArtifact -and
             -not (Test-Path -LiteralPath $expectedArtifact -PathType Leaf))) {
            throw ('Supported scalar source was rejected by ' + $mode +
                ': exit=' + $LASTEXITCODE + ' output=' + $supportedOutput)
        }
    }
    Write-Output 'NET_ASYNC_ADDITIONAL_COMMANDS_POSITIVE_CONTROLS; cases=5; artifacts=3'

    foreach ($mode in $modes) {
        $outFile = switch ($mode) {
            'compile-structured' { Join-Path $root 'OresGenerated.dll' }
            'emit-csharp' { Join-Path $root 'OresProgram.cs' }
            'emit-fsharp' { Join-Path $root 'OresProgram.fs' }
            default { $null }
        }
        $cmdArgs = if ($null -eq $outFile) {
            @($mode, $file)
        } else {
            @($mode, $file, '--out', $outFile)
        }
        $rejection = & $Compiler @cmdArgs 2>&1 | Out-String
        if ($LASTEXITCODE -ne 1 -or
            $rejection -cnotmatch 'oresnet:' -or
            ($null -ne $outFile -and (Test-Path -LiteralPath $outFile))) {
            throw "Unverified async source admitted through $mode: exit=$LASTEXITCODE output=$rejection"
        }
    }
    Write-Output 'NET_ASYNC_ADDITIONAL_COMMANDS_REJECTED; cases=5; artifacts=false'


    # Negative grammar/effect checks must reject instead of executing an
    # unsupported alias, type mismatch, expression, or actor callback.
    foreach ($invalid in @(
        $guest.Replace('return 42;', 'return 41+1;'),
        $guest.Replace('Future.from_task', 'Future.new'),
        $guest.Replace('Future.from_task', 'Task.from_future'),
        $guest.Replace('Future<int>', 'int'),
        ($guest + ' fnc hidden():int{return 1;}')
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
