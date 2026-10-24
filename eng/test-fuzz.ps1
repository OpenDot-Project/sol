# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param(
    [ValidateSet('all','managed','native','frontend','semantic','runtime','backend','gc')][string]$Target = 'all',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateRange(-1,39)][int]$Shard = -1,
    [ValidatePattern('\Acase-[0-9]{5}\z')][string]$CaseId,
    [ValidateRange(1,300)][int]$WorkerTimeoutSeconds = 60,
    [ValidateRange(128,2048)][int]$MemoryLimitMB = 512,
    [string]$BackendDirectory = 'out/native/backend'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'FuzzWorker.psm1') -Force
$output = Join-Path $root ('out/fuzz/replay/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$project = Join-Path $root 'tests/OpenDot.Sol.Fuzz/OpenDot.Sol.Fuzz.csproj'
$dotnet = (Get-Command dotnet).Source
& $dotnet restore $project --locked-mode --disable-parallel
if ($LASTEXITCODE) { throw 'Fuzz worker locked restore failed.' }
& $dotnet build $project --no-restore -c $Configuration /m:1 /nodeReuse:false -p:UseSharedCompilation=false
if ($LASTEXITCODE) { throw 'Managed fuzz worker build failed.' }
$worker = Join-Path $root "tests/OpenDot.Sol.Fuzz/bin/$Configuration/net10.0-windows/OpenDot.Sol.Fuzz.dll"
$limits = @{ TimeoutSeconds = $WorkerTimeoutSeconds; MemoryLimitMB = $MemoryLimitMB; OutputDirectory = $output }
Invoke-IsolatedFuzzWorker $dotnet @($worker, 'self-test') 'self-test' @limits
Invoke-IsolatedFuzzWorker $dotnet @($worker, 'verify-all', (Join-Path $root 'fuzz/corpus'), (Join-Path $output 'inventory.json')) 'inventory' @limits
$inventory = Get-Content (Join-Path $output 'inventory.json') -Raw | ConvertFrom-Json
if ($inventory.count -ne 100000 -or $inventory.status -cne 'verified') { throw 'Global corpus inventory is incomplete.' }
$targets = switch ($Target) {
    'all' { @('frontend','semantic','runtime','backend','gc') }
    'managed' { @('frontend','semantic') }
    'native' { @('runtime','backend','gc') }
    default { @($Target) }
}
$native = Join-Path $root 'out/native/runtime/fuzz-harness/Sol-NativeFuzz.exe'
$backend = [IO.Path]::GetFullPath((Join-Path $root "$BackendDirectory/OpenDot.Sol.LlvmNative.dll"))
if ('backend' -in $targets -and -not (Test-Path -LiteralPath $backend)) { throw 'Build the LLVM 23.1.3 backend before native backend fuzz replay.' }
$needsNative = @($targets | Where-Object { $_ -in @('runtime','backend','gc') }).Count -gt 0
if ($needsNative) {
    & (Join-Path $PSScriptRoot 'build-native-fuzz.ps1')
    Invoke-IsolatedFuzzWorker $native @('--asan-probe') 'asan-self-test' -ExpectedError 'AddressSanitizer: use-after-poison' @limits
}
if ($CaseId) {
    $number = [int]$CaseId.Substring(5)
    if ($number -ge 20000) { throw 'Case ID exceeds the per-target corpus inventory.' }
    $selected = [int][Math]::Floor($number / 500)
    if ($Shard -ge 0 -and $Shard -ne $selected) { throw 'Case does not belong to the selected shard.' }
    $Shard = $selected
}
$indices = if ($Shard -ge 0) { @($Shard) } else { @(0..39) }
$totals = [ordered]@{}
foreach ($name in $targets) {
    $directory = Join-Path $output $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $corpus = Join-Path $root "fuzz/corpus/$name"
    $verifiedPath = Join-Path $directory 'verification.json'
    Invoke-IsolatedFuzzWorker $dotnet @($worker, 'verify', $corpus, $verifiedPath) "$name-verify" @limits
    $verified = Get-Content $verifiedPath -Raw | ConvertFrom-Json
    if ($verified.count -ne 20000 -or $verified.unique_sources -ne 20000 -or $verified.status -cne 'verified') {
        throw 'Per-target corpus verification failed.'
    }
    $total = 0
    foreach ($index in $indices) {
        $stem = 'shard-{0:D2}' -f $index
        $report = Join-Path $directory "$stem.json"
        $journal = Join-Path $directory "$stem.journal.log"
        $expected = if ($CaseId) { 1 } else { 500 }
        if ($name -in @('frontend','semantic')) {
            $arguments = @($worker, 'replay', $corpus, "$index", $report, $journal)
            if ($CaseId) { $arguments += $CaseId }
            Invoke-IsolatedFuzzWorker $dotnet $arguments "$name-$stem" @limits
        } else {
            $pack = Join-Path $directory "$stem.pack"
            $packReport = Join-Path $directory "$stem.pack.json"
            $arguments = @($worker, 'pack', $corpus, "$index", $pack, $packReport)
            if ($CaseId) { $arguments += $CaseId }
            Invoke-IsolatedFuzzWorker $dotnet $arguments "$name-$stem-pack" @limits
            $packed = Get-Content $packReport -Raw | ConvertFrom-Json
            if ($packed.count -ne $expected -or $packed.status -cne 'packed' -or $packed.corpus_sha256 -cne $verified.corpus_sha256 -or
                $packed.pack_sha256 -cne (Get-FileHash $pack -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Native trace pack mismatch.' }
            $arguments = @($pack, $report, $journal)
            if ($name -eq 'backend') { $arguments += $backend }
            Invoke-IsolatedFuzzWorker $native $arguments "$name-$stem" @limits
        }
        $result = Get-Content $report -Raw | ConvertFrom-Json
        if ($result.status -cne 'passed' -or $result.count -ne $expected) { throw 'Incomplete fuzz replay report.' }
        if ($name -in @('frontend','semantic')) {
            if ($result.corpus_sha256 -cne $verified.corpus_sha256 -or $result.shard -ne $index) { throw 'Wrong managed replay identity.' }
        } else {
            $code = @{ runtime = 1; backend = 2; gc = 3 }[$name]
            $first = if ($CaseId) { [int]$CaseId.Substring(5) } else { $index * 500 }
            if ($result.target -ne $code -or $result.first -ne $first) { throw 'Wrong native replay identity.' }
        }
        $total += $result.count
        Write-Host "$name/$stem passed: $($result.count) inputs."
    }
    $totals[$name] = $total
}
$count = [int](($totals.Values | Measure-Object -Sum).Sum)
$sourceCommit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE) { throw 'Fuzz source identity unavailable.' }
$sourceDirty = @(& git -C $root status --porcelain).Count -ne 0
if ($LASTEXITCODE) { throw 'Fuzz source tree identity unavailable.' }
$nativeHash = if ($needsNative) { (Get-FileHash $native -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
$backendHash = if ('backend' -in $targets) { (Get-FileHash $backend -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
@{ schema_version = 1; corpus_total = 100000; count = $count; targets = $totals; status = 'passed'
    source_commit = $sourceCommit; source_tree_dirty = $sourceDirty
    managed_worker_sha256 = (Get-FileHash $worker -Algorithm SHA256).Hash.ToLowerInvariant()
    native_worker_sha256 = $nativeHash; llvm_backend_sha256 = $backendHash
    worker_timeout_seconds = $WorkerTimeoutSeconds; memory_limit_mb = $MemoryLimitMB
    native_asan = $needsNative; llvm_dll_asan = $false; coverage_guided = $false } | ConvertTo-Json -Depth 6 |
    Set-Content (Join-Path $output 'summary.json') -Encoding utf8NoBOM
Write-Host "Fuzz replay passed: $count inputs. Reports: $output"
