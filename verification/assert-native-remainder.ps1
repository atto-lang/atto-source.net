# Historical filename retained for existing workflow callers. This now checks
# source rejection of the retired modulo operator and supported NativeAOT arithmetic.
param([Parameter(Mandatory = $true)][ValidateSet('linux-x64', 'win-x64')][string]$Rid)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$suffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
$work = Join-Path $root ('artifacts/removed-modulo-native-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$compilerDir = Join-Path $work 'compiler'
dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r $Rid -o $compilerDir -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Native compiler publish failed' }
$compiler = Join-Path $compilerDir "oresnet$suffix"
# Supported arithmetic remains executable with NativeAOT; retired '%' does not.
$cases = @(
    @{ Name = 'addition'; Expression = '7+3'; Output = '10'; Exit = 0 },
    @{ Name = 'signed-multiplication'; Expression = '-7*3'; Output = '-21'; Exit = 0 },
    @{ Name = 'bounded-multiplication'; Expression = '999999*100'; Output = '99999900'; Exit = 0 },
    @{ Name = 'checked-overflow'; Expression = '9223372036854775807+1'; Output = ''; Exit = 70 }
)
# Every former positive '%' case must now reject; no emitted file is accepted.
$rejected = @(
    '-7%3', '7%-3', '-7%-3', '8%3*2', '20%6%3',
    '-9223372036854775808%-1',
    '-9223372036854775808%-9223372036854775808',
    '9223372036854775807%-9223372036854775808',
    '7%0', '7%(1+1)', '7%-(1+1)', '7%-9223372036854775809',
    '7%%2', '7%2%0'
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
        Write-Output "OPERATOR_NATIVE_PASS mode=$mode case=$($case.Name) exit=$exitCode output=$actual SHA256=$hash"
    }
    $caseIndex = 0
    foreach ($expression in $rejected) {
        $caseIndex++
        $caseDir = Join-Path $work "$mode/rejected-$caseIndex"
        New-Item -ItemType Directory -Path $caseDir -Force | Out-Null
        $inputFile = Join-Path $caseDir 'rejected.ores'
        [IO.File]::WriteAllText($inputFile, "fnc main():int{return $expression;}", [Text.UTF8Encoding]::new($false))
        $extension = switch ($mode) { 'compile' { 'dll' } 'emit-csharp' { 'cs' } 'emit-fsharp' { 'fs' } }
        $outputFile = Join-Path $caseDir "OresGenerated.$extension"
        $diagnostic = & $compiler $mode $inputFile --out $outputFile 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 0 -or (Test-Path $outputFile)) {
            throw "$mode admitted retired modulo source: $expression"
        }
        if ($diagnostic -notmatch "Removed Oreslang operator") {
            throw "$mode rejected source without expected operator diagnostic: $expression $diagnostic"
        }
        Write-Output "MODULO_SOURCE_REJECTED mode=$mode case=$caseIndex"
    }
}
Write-Output 'OPERATOR_NATIVE_SUITE_PASS: 12 NativeAOT executions, 42 source denials'
# The last compiler invocation is an expected rejection. Publish success only
# after every assertion, instead of leaking that command's nonzero exit status.
exit 0
