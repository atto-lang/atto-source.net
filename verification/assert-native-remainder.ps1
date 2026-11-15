param([Parameter(Mandatory = $true)][ValidateSet('linux-x64', 'win-x64')][string]$Rid)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$suffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
$work = Join-Path $root ('artifacts/remainder-native-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$compilerDir = Join-Path $work 'compiler'
dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r $Rid -o $compilerDir -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Native compiler publish failed' }
$compiler = Join-Path $compilerDir "oresnet$suffix"
# Historical script filename; this is now the canonical NO-% NativeAOT gate.
# Every positive mode must build and execute real generated native code.
$cases = @(
    @{ Name = 'canonical-multiply'; Expression = '7*6'; Output = '42'; Exit = 0 },
    @{ Name = 'canonical-add'; Expression = '7+3'; Output = '10'; Exit = 0 },
    @{ Name = 'canonical-precedence'; Expression = '8-3*2'; Output = '2'; Exit = 0 },
    @{ Name = 'checked-overflow'; Expression = '9223372036854775807+1'; Output = ''; Exit = 70 }
)
foreach ($mode in @('compile', 'emit-csharp', 'emit-fsharp')) {
    foreach ($case in $cases) {
        $caseDir = Join-Path $work "$mode/$($case.Name)"
        New-Item -ItemType Directory -Path $caseDir -Force | Out-Null
        $inputFile = Join-Path $caseDir 'input.ores'
        [IO.File]::WriteAllText($inputFile, "fnc main():int{return $($case.Expression);}", [Text.UTF8Encoding]::new($false))
        $assembly = Join-Path $caseDir 'OresGenerated.dll'
        if ($mode -eq 'compile') {
            & $compiler compile $inputFile --out $assembly
            if ($LASTEXITCODE -ne 0) { throw "$mode source compilation failed" }
        } else {
            $language = $mode.Substring(5)
            $extension = if ($language -eq 'csharp') { 'cs' } else { 'fs' }
            $generatedSource = Join-Path $caseDir "OresProgram.$extension"
            & $compiler $mode $inputFile --out $generatedSource
            if ($LASTEXITCODE -ne 0) { throw "$mode source emission failed" }
            $cilDir = Join-Path $caseDir 'cil'
            dotnet build "verification/source-emission-$language/OresGenerated.${extension}proj" -c Release -o $cilDir "-p:GeneratedSource=$generatedSource" -warnaserror
            if ($LASTEXITCODE -ne 0) { throw "$mode emitted source did not compile" }
            $assembly = Join-Path $cilDir 'OresGenerated.dll'
        }
        $hostProject = 'host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj'
        dotnet clean $hostProject -c Release -r $Rid "-p:GeneratedAssembly=$assembly"
        if ($LASTEXITCODE -ne 0) { throw 'Host clean failed' }
        $native = Join-Path $caseDir 'native'
        dotnet publish $hostProject -c Release -r $Rid -o $native "-p:GeneratedAssembly=$assembly" -warnaserror
        if ($LASTEXITCODE -ne 0) { throw "$mode Native AOT host publish failed" }
        $app = Join-Path $native "oreslang-app$suffix"
        if ($Rid -eq 'win-x64') { & "$root/verification/assert-native-release.ps1" -Binary $app -Rid $Rid }
        else {
            bash verification/assert-linux-native-release.sh $app
            if ($LASTEXITCODE -ne 0) { throw 'Native release layout failed' }
        }
        $stdout = Join-Path $caseDir 'stdout.txt'
        $stderr = Join-Path $caseDir 'stderr.txt'
        & $app > $stdout 2> $stderr
        $exitCode = $LASTEXITCODE
        $actual = ([IO.File]::ReadAllText($stdout)).Trim()
        $errorText = [IO.File]::ReadAllText($stderr)
        if ($exitCode -ne $case.Exit -or $actual -cne $case.Output) {
            throw "$mode/$($case.Name): expected exit $($case.Exit), output '$($case.Output)'; observed $exitCode '$actual' $errorText"
        }
        if ($case.Exit -eq 70 -and $errorText -notmatch 'arithmetic overflow') { throw 'Missing overflow diagnostic' }
        $hash = (Get-FileHash $app -Algorithm SHA256).Hash
        Write-Output "CANONICAL_SCALAR_NATIVE_PASS mode=$mode case=$($case.Name) exit=$exitCode output=$actual SHA256=$hash"
    }
    $rejectDir = Join-Path $work "rejected/$mode"
    New-Item -ItemType Directory -Path $rejectDir -Force | Out-Null
    $negativeIndex = 0
    # Fourteen modulo and ten caret cases per mode; unique output paths
    # make it impossible to confuse a stale file with a current rejection.
    foreach ($expression in @(
        '7%3', '-7%-3', '(8%3)*2', '-9223372036854775808%-1',
        '-7%3', '7%-3', '8%3*2', '20%6%3', '7%0', '7%(1+1)',
        '7%-(1+1)', '7%-9223372036854775809', '7%%2', '7%2%0',
        '7^3', '7^^3', '-7^3', '7^(-3)', '(7^3)',
        '7^3^1', '7^^(1+2)', '7^^3^^1', '7^ ^3',
        '-9223372036854775808^^-1'
    )) {
        $inputFile = Join-Path $rejectDir "input-$negativeIndex.ores"
        [IO.File]::WriteAllText($inputFile, "fnc main():int{return $expression;}", [Text.UTF8Encoding]::new($false))
        $extension = switch ($mode) { 'compile' { 'dll' } 'emit-csharp' { 'cs' } 'emit-fsharp' { 'fs' } }
        $outputFile = Join-Path $rejectDir "OresGenerated-$negativeIndex.$extension"
        $errorFile = Join-Path $rejectDir "rejection-$negativeIndex.stderr"
        & $compiler $mode $inputFile --out $outputFile 2> $errorFile
        $code = $LASTEXITCODE
        if ($code -ne 1 -or (Test-Path $outputFile)) {
            throw "$mode incorrectly admitted $expression or left an emitted artifact (exit=$code)"
        }
        $diagnostic = [IO.File]::ReadAllText($errorFile)
        $expectedDiagnostic = if ($expression.Contains('^')) {
            "Reserved AttoLang operator"
        } else {
            "noncanonical '%' source operator"
        }
        if ($diagnostic -notmatch [regex]::Escape($expectedDiagnostic)) {
            throw "$mode rejected $expression for the wrong reason: $diagnostic"
        }
        Write-Output "CANONICAL_RESERVED_OPERATOR_REJECTED mode=$mode index=$negativeIndex source=$expression"
        $negativeIndex++
    }
}
Write-Output 'CANONICAL_OPERATOR_NATIVE_SUITE_PASS: 12 native executions, 72 source denials'
# The last compiler invocation is an expected rejection. Publish success only
# after every assertion, instead of leaking that command's nonzero exit status.
exit 0
