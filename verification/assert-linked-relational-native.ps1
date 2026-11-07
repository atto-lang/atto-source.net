param(
    [Parameter(Mandatory = $true)][string]$Compiler,
    [Parameter(Mandatory = $true)][ValidateSet('linux-x64', 'win-x64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$ExpectedHead
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Fail closed: manual runs and missing GitHub run/job identities may produce
# local diagnostic output but MUST NOT create a release evidence receipt.
if ($env:GITHUB_REPOSITORY -cne 'ores-truffle-oreslang/oreslang-source.net' -or
    $env:GITHUB_JOB -cne 'native-source-modules') {
    throw 'Relational release evidence requires the source-owned module workflow'
}
[long]$verifiedRunId = 0
[int]$verifiedAttempt = 0
if (-not [long]::TryParse($env:GITHUB_RUN_ID, [ref]$verifiedRunId) -or
    $verifiedRunId -le 0 -or
    -not [int]::TryParse($env:GITHUB_RUN_ATTEMPT, [ref]$verifiedAttempt) -or
    $verifiedAttempt -le 0) {
    throw 'Relational release receipt requires valid GitHub run and attempt IDs'
}
if (($Rid -eq 'win-x64' -and $env:RUNNER_OS -cne 'Windows') -or
    ($Rid -eq 'linux-x64' -and $env:RUNNER_OS -cne 'Linux')) {
    throw 'NativeAOT target RID does not match actual runner operating system'
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
    -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
    throw 'Relational mutation needs an existing runner-local temporary directory'
}
$artifactsDir = Join-Path (Get-Location).Path 'artifacts'
if (-not (Test-Path -LiteralPath $artifactsDir -PathType Container) -or
    ([IO.File]::GetAttributes($artifactsDir) -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Native AOT artifacts root must be a real checkout-owned directory'
}

$head = (& git rev-parse HEAD).Trim()
$tree = (& git rev-parse 'HEAD^{tree}').Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedHead -or
    $head -cnotmatch '^[0-9a-f]{40}$' -or $tree -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Signed relational gate must use the exact source head and tree'
}
$suffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
$nativeCompiler = (Resolve-Path -LiteralPath $Compiler).Path
$compilerHash = (Get-FileHash -LiteralPath $nativeCompiler -Algorithm SHA256).Hash.ToLowerInvariant()
if ($compilerHash -cne (Get-Content artifacts/module-app/compiler.sha256 -Raw).Trim()) {
    throw 'Signed relational compiler differs from pinned Native AOT executable'
}

$names = @(
    'atto-relational-arithmetic.atto',
    'atto-relational-limits.atto',
    'atto-relational-main.atto',
    'atto-relational-values.atto'
)
foreach ($name in $names) {
    $file = "examples/modules/$name"
    $expectedBlob = (& git rev-parse "HEAD:$file").Trim()
    $observedBlob = (& git hash-object --no-filters -- $file).Trim()
    if ($LASTEXITCODE -ne 0 -or $expectedBlob -cne $observedBlob) {
        throw "Relational guest fixture does not match exact Git blob: $file"
    }
}

# The mutation is performed ONLY under a runner-local temporary directory.
# A generated CIL or native image from the original graph cannot substitute.
$rootVariation = Join-Path $env:RUNNER_TEMP 'atto-linked-relational-mutated'
if (Test-Path -LiteralPath $rootVariation) {
    throw 'Refusing reused or preexisting mutation directory'
}
New-Item -ItemType Directory -Path $rootVariation | Out-Null
foreach ($name in $names) {
    Copy-Item -LiteralPath (Join-Path 'examples/modules' $name) -Destination (Join-Path $rootVariation $name)
}
$arithmetic = Join-Path $rootVariation 'atto-relational-arithmetic.atto'
$source = [IO.File]::ReadAllText($arithmetic)
if ([regex]::Matches($source, [regex]::Escape('n * 2')).Count -ne 1) {
    throw 'Signed relational mutation anchor was absent or ambiguous'
}
[IO.File]::WriteAllText($arithmetic, $source.Replace('n * 2', 'n * 2 + 1'),
    [Text.UTF8Encoding]::new($false))

$receipts = @()
foreach ($case in @(
    @{ label = 'base'; root = 'examples/modules'; expected = '42' },
    @{ label = 'mutated'; root = $rootVariation; expected = '43' }
)) {
    $outputRoot = 'artifacts/atto-linked-relational-' + $case.label
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $cilFile = Join-Path $outputRoot 'OresGenerated.dll'
    Remove-Item -LiteralPath $cilFile -Force -ErrorAction SilentlyContinue
    $entry = Join-Path $case.root 'atto-relational-main.atto'
    $witness = & $nativeCompiler compile-modules $entry --root $case.root --out $cilFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $cilFile -PathType Leaf)) {
        throw "Relational graph did not generate CIL: $($case.label)"
    }
    $graph = ($witness -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
    $units = @($graph.sources)
    $expectedPaths = @($names | ForEach-Object { '0/' + $_ })
    $paths = @($units | ForEach-Object { [string]$_.path })
    if ($units.Count -ne 4 -or
        [string]::Join('|', $paths) -cne [string]::Join('|', $expectedPaths) -or
        $graph.graph_sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "Invalid canonical relational graph inventory: $($case.label)"
    }
    $preimage = [Text.StringBuilder]::new()
    foreach ($unit in $units) {
        $file = Join-Path $case.root ([IO.Path]::GetFileName([string]$unit.path))
        $sourceHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($unit.sha256 -cne $sourceHash -or
            [long]$unit.utf8_bytes -ne (Get-Item -LiteralPath $file).Length) {
            throw "Compiler witness differs from source bytes: $file"
        }
        [void]$preimage.Append([string]$unit.path)
        [void]$preimage.Append([char]0)
        [void]$preimage.Append([string]$unit.sha256)
        [void]$preimage.Append([char]10)
    }
    $graphHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($preimage.ToString())
        )
    ).ToLowerInvariant()
    if ($graphHash -cne $graph.graph_sha256) {
        throw "Relational graph SHA-256 failed independent reconstruction: $($case.label)"
    }

    $generated = (Resolve-Path -LiteralPath $cilFile).Path
    $cilHash = (Get-FileHash -LiteralPath $generated -Algorithm SHA256).Hash.ToLowerInvariant()
    # Prevent a stale executable from satisfying a later output assertion.
    $appDir = Join-Path $outputRoot 'app'
    $binary = Join-Path $appDir ("oreslang-app" + $suffix)
    if (Test-Path -LiteralPath $binary) {
        Remove-Item -LiteralPath $binary -Force
    }
    dotnet clean host/Ores.Net.NativeHost -c Release -r $Rid "-p:GeneratedAssembly=$generated"
    if ($LASTEXITCODE -ne 0) { throw "Native AOT clean failed: $($case.label)" }
    dotnet publish host/Ores.Net.NativeHost -c Release -r $Rid -p:PublishAot=true "-p:GeneratedAssembly=$generated" -o $appDir -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Native AOT publish failed: $($case.label)" }
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "Native AOT publish produced no new executable: $($case.label)"
    }
    if ($Rid -eq 'win-x64') {
        & ./verification/assert-native-release.ps1 -Binary $binary -Rid $Rid
    } else {
        & bash verification/assert-linux-native-release.sh $binary
    }
    if ($LASTEXITCODE -ne 0) { throw "Native AOT release verifier failed: $($case.label)" }
    $output = & $binary
    if ($LASTEXITCODE -ne 0 -or ($output | Out-String).Trim() -cne $case.expected) {
        throw "Linked relational Native AOT guest output mismatch: $($case.label): $output"
    }
    if ((Get-FileHash $nativeCompiler -Algorithm SHA256).Hash.ToLowerInvariant() -cne $compilerHash) {
        throw 'Compiler changed during relational source graph compilation'
    }
    $receipt = [ordered]@{
        schema = 'atto.net.linked_relational_native_aot_v1'
        scope = 'four-file-local-atto-relational-not-full-backend-certification'
        source_head = $head
        source_tree = $tree
        run_id = $verifiedRunId
        run_attempt = $verifiedAttempt
        github_job = $env:GITHUB_JOB
        github_repository = $env:GITHUB_REPOSITORY
        runner_os = $env:RUNNER_OS
        workflow_path = '.github/workflows/source-modules-native-aot.yml'
        rid = $Rid
        variant = $case.label
        graph_sha256 = $graphHash
        compiler_sha256 = $compilerHash
        generated_cil_sha256 = $cilHash
        native_binary_sha256 = (Get-FileHash $binary -Algorithm SHA256).Hash.ToLowerInvariant()
        guest_output = ($output | Out-String).Trim()
        independently_verified = $false
    }
    $receipt | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $outputRoot 'receipt.json') -Encoding utf8
    $receipts += $receipt
}
if ($receipts.Count -ne 2 -or
    $receipts[0].graph_sha256 -ceq $receipts[1].graph_sha256 -or
    $receipts[0].generated_cil_sha256 -ceq $receipts[1].generated_cil_sha256 -or
    $receipts[0].native_binary_sha256 -ceq $receipts[1].native_binary_sha256 -or
    $receipts[0].compiler_sha256 -cne $receipts[1].compiler_sha256) {
    throw 'Signed relational 42-to-43 mutation failed graph/CIL/native differential verification'
}
foreach ($name in $names) {
    $path = "examples/modules/$name"
    $observedBlob = (& git hash-object --no-filters -- $path).Trim()
    $expectedBlob = (& git rev-parse "HEAD:$path").Trim()
    if ($LASTEXITCODE -ne 0 -or $observedBlob -cne $expectedBlob) {
        throw "Tracked relational source was modified by native test: $path"
    }
}
Write-Host 'NET_LINKED_ATTO_RELATIONAL_NATIVE_AOT_42_43_EXECUTED'
