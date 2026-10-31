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
$cases = @(
    @{ Name = 'precedence'; Expression = '2+3*4'; Output = '14'; Exit = 0 },
    @{ Name = 'signed-add'; Expression = '-7+3'; Output = '-4'; Exit = 0 },
    @{ Name = 'minimum-plus-one'; Expression = '-9223372036854775808+1'; Output = '-9223372036854775807'; Exit = 0 },
    @{ Name = 'checked-overflow'; Expression = '(9223372036854775807+1)'; Output = ''; Exit = 70 },
    @{ Name = 'math-abs-negative'; Expression = 'Math.abs(-42)'; Output = '42'; Exit = 0 },
    @{ Name = 'math-neg-negative'; Expression = 'Math.neg(-42)'; Output = '42'; Exit = 0 },
    @{ Name = 'math-abs-minimum'; Expression = 'Math.abs(-9223372036854775808)'; Output = ''; Exit = 70 },
    @{ Name = 'math-neg-minimum'; Expression = 'Math.neg(-9223372036854775808)'; Output = ''; Exit = 70 }
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
        Write-Output "NUMERIC_NATIVE_PASS mode=$mode case=$($case.Name) exit=$exitCode output=$actual SHA256=$hash"
    }
    foreach ($expression in @('7%0', '-7%3', '7%-3', '7%(1+1)', '7%-9223372036854775809')) {
        $inputFile = Join-Path $work 'rejected.ores'
        [IO.File]::WriteAllText($inputFile, "fnc main():int{return $expression;}", [Text.UTF8Encoding]::new($false))
        $extension = switch ($mode) { 'compile' { 'dll' } 'emit-csharp' { 'cs' } 'emit-fsharp' { 'fs' } }
        $outputFile = Join-Path $work "rejected/OresGenerated.$extension"
        & $compiler $mode $inputFile --out $outputFile
        if ($LASTEXITCODE -eq 0 -or (Test-Path $outputFile)) { throw "$mode admitted removed percent operator $expression" }
    }
}
Write-Output 'NUMERIC_NATIVE_SUITE_PASS: 24 checked native executions, 15 percent-source denials'
# The last compiler invocation is an expected rejection. Publish success only
# after every assertion, instead of leaking that command's nonzero exit status.
exit 0
