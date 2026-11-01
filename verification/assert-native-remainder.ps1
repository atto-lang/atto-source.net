param([Parameter(Mandatory = $true)][ValidateSet('linux-x64', 'win-x64')][string]$Rid)
# Migration note: %, ^ and ^^ were removed from Oreslang source. We now
# require NativeAOT compiler rejection in every shipping emitter mode, plus
# one REAL NativeAOT scalar guest; no source remainder executions are claimed.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$suffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
$work = Join-Path $root ('artifacts/removed-remainder-native-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$compilerDir = Join-Path $work 'compiler'
dotnet publish compiler/Ores.Net.Compiler/Ores.Net.Compiler.csproj -c Release -r $Rid -o $compilerDir -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'Native compiler publish failed' }
$compiler = Join-Path $compilerDir "oresnet$suffix"
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Native compiler binary absent' }

# All operator forms must reject before any generated DLL, C# or F# source
# can be emitted. Each output gets a fresh directory and unique filename.
$expressions = @(
    '-7%3', '7%-3', '-7%-3', '8%3*2', '20%6%3',
    '-9223372036854775808%-1', '7%0', '7%(1+1)',
    '(9223372036854775807+1)%-1', 'Integer.bitXor(7%3,1)',
    '7^3', '7^^3'
)
$rejections = 0
foreach ($mode in @('check', 'compile', 'emit-csharp', 'emit-fsharp')) {
    $index = 0
    foreach ($expression in $expressions) {
        $caseDir = Join-Path $work "$mode/$index"
        New-Item -ItemType Directory -Path $caseDir -Force | Out-Null
        $inputFile = Join-Path $caseDir 'input.ores'
        [IO.File]::WriteAllText($inputFile, "fnc main():int{return $expression;}", [Text.UTF8Encoding]::new($false))
        $extension = switch ($mode) {
            'compile' { 'dll' }
            'emit-csharp' { 'cs' }
            'emit-fsharp' { 'fs' }
            default { 'txt' }
        }
        $outputFile = Join-Path $caseDir "OresGenerated.$extension"
        $caseStdout = Join-Path $caseDir 'compiler-stdout.txt'
        $caseStderr = Join-Path $caseDir 'compiler-stderr.txt'
        if ($mode -eq 'check') {
            & $compiler check $inputFile > $caseStdout 2> $caseStderr
        } else {
            & $compiler $mode $inputFile --out $outputFile > $caseStdout 2> $caseStderr
        }
        $compilerExit = $LASTEXITCODE
        $diagnostic = [IO.File]::ReadAllText($caseStderr)
        if ($compilerExit -ne 1) { throw "$mode must exit 1 on removed operator: $expression; exit=$compilerExit" }
        if ($diagnostic -notmatch 'Removed Oreslang operator') {
            throw "$mode rejection did not come from removed-operator admission: $expression; $diagnostic"
        }
        if ([IO.File]::ReadAllText($caseStdout) -ne '') { throw "$mode wrote unexpected stdout for denied source: $expression" }
        if (Test-Path -LiteralPath $outputFile) { throw "$mode emitted artifact for forbidden source: $expression" }
        $rejections++
        $index++
    }
}
if ($rejections -ne 48) { throw "Expected 48 executed native compiler denials, observed $rejections" }

# Ensure the same compiler still produces real scalar machine code; an
# all-negative parser-only suite must not be misrepresented as AOT execution.
$positiveDir = Join-Path $work 'positive'
New-Item -ItemType Directory -Path $positiveDir -Force | Out-Null
$positive = Join-Path $positiveDir 'main.ores'
[IO.File]::WriteAllText($positive, 'fnc main():int{return 6*7;}', [Text.UTF8Encoding]::new($false))
$assembly = Join-Path $positiveDir 'OresGenerated.dll'
& $compiler compile $positive --out $assembly
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $assembly)) { throw 'NativeAOT compiler failed positive scalar build' }
$native = Join-Path $positiveDir 'native'
$host = 'host/Ores.Net.NativeHost/Ores.Net.NativeHost.csproj'
dotnet publish $host -c Release -r $Rid -o $native "-p:GeneratedAssembly=$assembly" -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'NativeAOT guest publish failed' }
$app = Join-Path $native "oreslang-app$suffix"
if ($Rid -eq 'win-x64') { & "$root/verification/assert-native-release.ps1" -Binary $app -Rid $Rid }
else {
    bash verification/assert-linux-native-release.sh $app
    if ($LASTEXITCODE -ne 0) { throw 'Native release layout failed' }
}
$stdout = Join-Path $positiveDir 'stdout.txt'
$stderr = Join-Path $positiveDir 'stderr.txt'
& $app > $stdout 2> $stderr
$exitCode = $LASTEXITCODE
$actual = ([IO.File]::ReadAllText($stdout)).Trim()
$errorText = [IO.File]::ReadAllText($stderr)
if ($exitCode -ne 0 -or $actual -cne '42' -or $errorText -ne '') {
    throw "NativeAOT scalar guest mismatch: exit=$exitCode output='$actual' stderr='$errorText'"
}
$hash = (Get-FileHash $app -Algorithm SHA256).Hash
Write-Output "REMOVED_REMAINDER_NATIVE_PARITY_PASS denials=$rejections native_aot_guest=42 RID=$Rid SHA256=$hash"
exit 0
