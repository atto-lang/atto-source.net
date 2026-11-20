param(
    [Parameter(Mandatory = $true)][string]$Compiler,
    [Parameter(Mandatory = $true)][ValidateSet('linux-x64', 'win-x64')][string]$Rid
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$Compiler = (Resolve-Path $Compiler).Path
$repoRoot = Split-Path $PSScriptRoot
$root = Join-Path $repoRoot 'artifacts/task-i64-source'
$null = New-Item -ItemType Directory -Force -Path $root
$records = @()
foreach ($case in @(
    @{Name = 'value'; File = 'task-i64-42.ores'; Exit = 0; Value = '42'},
    @{Name = 'overflow'; File = 'task-i64-overflow.ores'; Exit = 70; Value = ''}
)) {
    $caseRoot = Join-Path $root $case.Name
    $null = New-Item -ItemType Directory -Force -Path $caseRoot
    $source = Join-Path $repoRoot ('examples/' + $case.File)
    $cil = Join-Path $caseRoot 'OresGenerated.dll'
    & $Compiler compile-task-i64 $source --out $cil
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $cil)) { throw 'Native compiler did not emit Task-i64 producer CIL' }
    $native = Join-Path $caseRoot 'native'
    dotnet publish (Join-Path $repoRoot 'host/Ores.Net.TaskI64NativeHost') -c Release -r $Rid -t:Rebuild -p:PublishAot=true --self-contained true "-p:GeneratedAssembly=$cil" -o $native
    if ($LASTEXITCODE -ne 0) { throw 'Task-i64 source NativeAOT link failed' }
    $name = if ($Rid -eq 'win-x64') { 'oreslang-task-i64.exe' } else { 'oreslang-task-i64' }
    $binary = Join-Path $native $name
    $stderr = Join-Path $caseRoot 'stderr.txt'
    $stdout = @(& $binary 2> $stderr)
    $exit = $LASTEXITCODE
    $errorTrace = Get-Content $stderr -Raw
    if ($exit -ne $case.Exit) { throw "Wrong Task-i64 native exit: expected $($case.Exit), got $exit" }
    if ($case.Name -eq 'value') {
        if ($stdout.Count -ne 1 -or $stdout[0] -cne $case.Value -or
            $errorTrace -cnotmatch 'TASK_I64_RECEIPT; producers=1; observer_distinct=True; producer_body_retired=True') {
            throw 'Missing Task-i64 value or actual one-producer/observer/body-retirement witness'
        }
    } elseif ($stdout.Count -ne 0 -or $errorTrace -cnotmatch 'Task-i64 producer FAILED: OverflowException') {
        throw 'Checked producer overflow was lost or reclassified'
    }
    $records += [ordered]@{
        case = $case.Name
        source_sha256 = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
        producer_cil_sha256 = (Get-FileHash $cil -Algorithm SHA256).Hash.ToLowerInvariant()
        native_sha256 = (Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant()
        exit_code = $exit
        stdout = $stdout
        stderr = $errorTrace
    }
}
$positive = Get-Content (Join-Path $repoRoot 'examples/task-i64-42.ores') -Raw
$negatives = @(
    $positive.Replace('Future<int>', 'Future<bool>'),
    $positive.Replace('6 * 7', 'captured'),
    $positive.Replace('6 * 7', 'await other'),
    $positive.Replace('6 * 7', 'task {return 42;}'),
    $positive.Replace('from_task', 'new'),
    $positive.Replace('return await f', 'return await other'),
    $positive.Replace('6 * 7', 'self.value'),
    $positive.Replace('6 * 7', 'rt borrow value')
)
for ($i = 0; $i -lt $negatives.Count; $i++) {
    $dir = Join-Path $root "reject-$i"
    $null = New-Item -ItemType Directory -Force -Path $dir
    $source = Join-Path $dir 'invalid.ores'
    [IO.File]::WriteAllText($source, $negatives[$i], [Text.UTF8Encoding]::new($false, $true))
    $cil = Join-Path $dir 'OresGenerated.dll'
    $diagnostic = & $Compiler compile-task-i64 $source --out $cil 2>&1 | Out-String
    if ($LASTEXITCODE -ne 1 -or $diagnostic -cnotmatch 'Task-i64' -or (Test-Path $cil)) {
        throw "Unsupported Task-i64 profile case $i was not denied before emission: $diagnostic"
    }
}
[ordered]@{
    schema = 'root-task-i64-source-v1'
    profile = $Rid
    producer_language = 'capture-free-int-arithmetic'
    runtime_root_only = $true
    positive_and_failure = $records
    compile_negative_cases = $negatives.Count
    full_interop_verified = $false
} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $root 'receipt.json')
Write-Output 'NET_ROOT_TASK_I64_SOURCE_EXECUTED; value=42; producers=1; typed_failure=overflow; negative_cases=8; full_interop_verified=false'
$global:LASTEXITCODE = 0
