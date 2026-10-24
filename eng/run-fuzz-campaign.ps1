# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'nightly')]
    [string]$Campaign = 'smoke',
    [string[]]$Target = @('all'),
    [int]$SecondsOverride = 0,
    [string]$RunId = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if ($env:OS -ne 'Windows_NT') { throw 'Coverage-guided campaigns require the supported Windows host.' }
if ($SecondsOverride -lt 0 -or $SecondsOverride -gt 300) { throw 'SecondsOverride must be between 0 and 300.' }
if (-not $RunId) { $RunId = 'local-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
if ($RunId -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$') { throw 'RunId contains unsupported characters.' }

$configPath = Join-Path $root 'fuzz/campaigns.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.schema_version -ne 1) { throw 'Unsupported fuzz campaign schema.' }
$campaignConfig = $config.limits.campaigns.$Campaign
$seconds = if ($SecondsOverride -gt 0) { $SecondsOverride } else { [int]$campaignConfig.seconds_per_target }
if ($seconds -gt $campaignConfig.seconds_per_target) { throw 'SecondsOverride cannot exceed the campaign limit.' }
$targets = @($config.targets | ForEach-Object { $_.name })
if ($Target.Count -eq 1 -and $Target[0] -eq 'all') { $selectedTargets = $targets }
else {
    $selectedTargets = @($Target | Select-Object -Unique)
    if (@($selectedTargets | Where-Object { $_ -notin $targets }).Count -gt 0) { throw 'Unknown fuzz target requested.' }
}
if ($selectedTargets.Count -eq 0) { throw 'At least one fuzz target is required.' }

$runRoot = Join-Path $root "out/fuzz/campaigns/$RunId"
if (Test-Path -LiteralPath $runRoot) { throw "Campaign output already exists: $runRoot" }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$toolsRoot = Join-Path $root 'out/fuzz/tools'
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
$driver = Join-Path $toolsRoot 'libfuzzer-dotnet-windows.exe'
$driverPin = $config.engine.managed
if (-not (Test-Path -LiteralPath $driver -PathType Leaf) -or
    (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant() -ne $driverPin.driver_sha256) {
    Invoke-WebRequest -Uri $driverPin.driver_url -OutFile $driver
}
$driverInfo = Get-Item -LiteralPath $driver
if ($driverInfo.Length -ne $driverPin.driver_bytes -or
    (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant() -ne $driverPin.driver_sha256 -or
    (Get-FileHash -LiteralPath $driver -Algorithm SHA512).Hash.ToLowerInvariant() -ne $driverPin.driver_sha512) {
    throw 'The libfuzzer-dotnet executable does not match the pinned size, SHA-256, and SHA-512.'
}

dotnet restore tests/OpenDot.Sol.Fuzz/OpenDot.Sol.Fuzz.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Fuzz harness restore failed.' }
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Fuzz tool restore failed.' }
$managedTargets = @($config.targets | Where-Object { $_.mode -eq 'managed' -and $_.name -in $selectedTargets })
$managedPublish = Join-Path $runRoot 'managed/publish'
if ($managedTargets.Count -gt 0) {
    dotnet publish tests/OpenDot.Sol.Fuzz/OpenDot.Sol.Fuzz.csproj --no-restore --configuration Release --output $managedPublish
    if ($LASTEXITCODE -ne 0) { throw 'Managed fuzz target publish failed.' }
    $assemblies = @($managedTargets | ForEach-Object { $_.instrumented_assemblies } | Select-Object -Unique)
    foreach ($assembly in $assemblies) {
        if ([IO.Path]::GetFileName($assembly) -ne $assembly -or [IO.Path]::GetExtension($assembly) -ne '.dll') {
            throw "Invalid instrumentation assembly name: $assembly"
        }
        $assemblyPath = Join-Path $managedPublish $assembly
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "Instrumentation assembly is missing: $assembly" }
        dotnet tool run sharpfuzz -- $assemblyPath
        if ($LASTEXITCODE -ne 0) { throw "SharpFuzz instrumentation failed for $assembly" }
    }
}
else {
    dotnet build tests/OpenDot.Sol.Fuzz/OpenDot.Sol.Fuzz.csproj --no-restore --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Managed seed utility build failed.' }
}

$nativeTargets = @($config.targets | Where-Object { $_.mode -eq 'native' -and $_.name -in $selectedTargets })
if ($nativeTargets.Count -gt 0) {
    if (-not $env:LLVM_ROOT) { throw 'LLVM_ROOT is required to build native coverage-guided targets.' }
    if (@($nativeTargets | Where-Object { $_.native_target -eq 'backend' }).Count -gt 0 -and
        -not (Test-Path -LiteralPath (Join-Path $root 'out/native/backend/OpenDot.Sol.LlvmNative.dll') -PathType Leaf)) {
        throw 'The LLVM 23.1.3 backend must be built before the backend fuzz target.'
    }
    $nativeBuildTargets = @($nativeTargets | ForEach-Object { $_.native_target } | Select-Object -Unique)
    ./eng/build-coverage-fuzz.ps1 -Target $nativeBuildTargets
    if ($LASTEXITCODE -ne 0) { throw 'Native coverage-guided target build failed.' }
}

function Get-TreeWorkingSet([int]$ProcessId, [datetime]$RootStartTime) {
    $processes = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($ProcessId)
    $creationTimes = @{ $ProcessId = $RootStartTime }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($item in $processes) {
            $parentId = [int]$item.ParentProcessId
            if (-not $creationTimes.ContainsKey($parentId)) { continue }
            $processStartTime = [datetime]$item.CreationDate
            if ($processStartTime -lt $creationTimes[$parentId]) { continue }
            $childId = [int]$item.ProcessId
            if ($ids.Add($childId)) {
                $creationTimes[$childId] = $processStartTime
                $changed = $true
            }
        }
    }
    [UInt64]$total = 0
    foreach ($item in $processes) { if ($ids.Contains([int]$item.ProcessId)) { $total += [UInt64]$item.WorkingSetSize } }
    if (@($processes | Where-Object { [int]$_.ProcessId -eq $ProcessId }).Count -eq 0) {
        throw 'Fuzzer process was absent from the process snapshot.'
    }
    return [pscustomobject]@{ WorkingSetBytes = $total; ProcessCount = $ids.Count }
}

function Invoke-Fuzzer([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [string]$StdoutPath,
    [string]$StderrPath, [int]$MaximumSeconds, [int]$MemoryLimitMb) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $outFile = [IO.File]::Create($StdoutPath)
    $errFile = [IO.File]::Create($StderrPath)
    $processStarted = $false
    [UInt64]$peakWorkingSetBytes = 0
    $peakProcessCount = 0
    try {
        if (-not $process.Start()) { throw 'Could not start the libFuzzer process.' }
        $processStarted = $true
        $rootStartTime = $process.StartTime
        $outCopy = $process.StandardOutput.BaseStream.CopyToAsync($outFile)
        $errCopy = $process.StandardError.BaseStream.CopyToAsync($errFile)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $failure = $null
        $failureDetail = $null
        try {
            $sample = Get-TreeWorkingSet $process.Id $rootStartTime
            $peakWorkingSetBytes = $sample.WorkingSetBytes
            $peakProcessCount = $sample.ProcessCount
            if ($sample.WorkingSetBytes -gt [UInt64]$MemoryLimitMb * 1MB) { $failure = 'working-set-limit' }
        }
        catch {
            if (-not $process.HasExited) { $failure = 'process-tree-monitor-failed'; $failureDetail = $_.Exception.Message }
        }
        while (-not $failure -and -not $process.WaitForExit(1000)) {
            if ($watch.Elapsed.TotalSeconds -gt $MaximumSeconds) { $failure = 'hard-timeout'; break }
            try { $sample = Get-TreeWorkingSet $process.Id $rootStartTime }
            catch {
                if (-not $process.HasExited) { $failure = 'process-tree-monitor-failed'; $failureDetail = $_.Exception.Message; break }
            }
            if ($sample.WorkingSetBytes -gt $peakWorkingSetBytes) { $peakWorkingSetBytes = $sample.WorkingSetBytes }
            if ($sample.ProcessCount -gt $peakProcessCount) { $peakProcessCount = $sample.ProcessCount }
            if ($sample.WorkingSetBytes -gt [UInt64]$MemoryLimitMb * 1MB) { $failure = 'working-set-limit'; break }
        }
        if ($failure) {
            try { $process.Kill($true) } catch [InvalidOperationException] { }
            $process.WaitForExit()
        }
        [void]$outCopy.GetAwaiter().GetResult()
        [void]$errCopy.GetAwaiter().GetResult()
        $outFile.Flush()
        $errFile.Flush()
        $exitCode = if ($failure) { 124 } else { $process.ExitCode }
        return [pscustomobject]@{
            ExitCode = $exitCode; Failure = $failure; ElapsedMilliseconds = $watch.ElapsedMilliseconds
            FailureDetail = $failureDetail; PeakWorkingSetBytes = $peakWorkingSetBytes; PeakProcessCount = $peakProcessCount
        }
    }
    finally {
        if ($processStarted -and -not $process.HasExited) {
            try { $process.Kill($true); $process.WaitForExit() } catch [InvalidOperationException] { }
        }
        $outFile.Dispose()
        $errFile.Dispose()
        $process.Dispose()
    }
}

$results = [Collections.Generic.List[object]]::new()
$failed = $false
foreach ($name in $selectedTargets) {
    $definition = $config.targets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    $targetRoot = Join-Path $runRoot $name
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    $corpus = Join-Path $targetRoot 'corpus'
    $crashes = Join-Path $targetRoot 'artifacts'
    New-Item -ItemType Directory -Path $crashes -Force | Out-Null
    $sourceCorpus = Join-Path $root $definition.corpus
    $fuzzDll = if ($managedTargets.Count -gt 0) { Join-Path $managedPublish 'OpenDot.Sol.Fuzz.dll' } else {
        Join-Path $root 'tests/OpenDot.Sol.Fuzz/bin/Release/net10.0-windows/OpenDot.Sol.Fuzz.dll'
    }
    dotnet $fuzzDll seed $sourceCorpus $corpus ([int]$campaignConfig.seed_limit_per_target)
    if ($LASTEXITCODE -ne 0) { throw "Could not prepare seed corpus for $name." }
    $seedCount = @(Get-ChildItem -LiteralPath $corpus -File).Count
    if ($seedCount -eq 0 -or $seedCount -gt [int]$campaignConfig.seed_limit_per_target) {
        throw "Invalid seed count for ${name}: $seedCount."
    }
    $executable = if ($definition.mode -eq 'managed') { Join-Path $managedPublish 'OpenDot.Sol.Fuzz.exe' } else {
        Join-Path $root "out/native/coverage-fuzz/$($definition.native_target)/Sol-Fuzz-$($definition.native_target).exe"
    }
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Fuzzer executable is missing: $executable" }

    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add("-max_total_time=$seconds")
    $arguments.Add("-max_len=$([int]$config.limits.maximum_input_bytes)")
    $arguments.Add("-timeout=$([int]$config.limits.input_timeout_seconds)")
    $arguments.Add("-rss_limit_mb=$([int]$config.limits.rss_limit_mb)")
    $arguments.Add('-print_final_stats=1')
    $arguments.Add('-use_value_profile=1')
    $arguments.Add("-artifact_prefix=$(Join-Path $crashes 'artifact-')")
    if ($definition.mode -eq 'managed') {
        $arguments.Add("-dict=$(Join-Path $root 'fuzz/dictionaries/sol.dict')")
        $arguments.Add("--target_path=$executable")
        $arguments.Add("--target_arg=$($definition.entry_argument)")
    }
    $arguments.Add($corpus)

    $stdoutPath = Join-Path $targetRoot 'stdout.log'
    $stderrPath = Join-Path $targetRoot 'stderr.log'
    if ($env:LLVM_ROOT) {
        $runtimePaths = @((Join-Path $env:LLVM_ROOT 'lib/clang/23/lib/windows'), (Join-Path $env:LLVM_ROOT 'bin'))
        $env:PATH = ($runtimePaths -join ';') + ";$env:PATH"
    }
    $fuzzer = if ($definition.mode -eq 'managed') { $driver } else { $executable }
    $monitorOutputs = @(Invoke-Fuzzer -Executable $fuzzer -Arguments $arguments.ToArray() -WorkingDirectory $root `
        -StdoutPath $stdoutPath -StderrPath $stderrPath `
        -MaximumSeconds ($seconds + [int]$config.limits.input_timeout_seconds + 45) `
        -MemoryLimitMb ([int]$config.limits.rss_limit_mb))
    if ($monitorOutputs.Count -eq 0) { throw 'Fuzzer process monitor returned no result.' }
    $processResult = $monitorOutputs[-1]
    if ($processResult -isnot [pscustomobject] -or 'ExitCode' -notin $processResult.PSObject.Properties.Name) {
        throw "Fuzzer process monitor returned an invalid result: $($processResult | ConvertTo-Json -Compress -Depth 4)"
    }
    $log = Get-Content -LiteralPath $stdoutPath -Raw
    $stderr = Get-Content -LiteralPath $stderrPath -Raw
    $units = 0L
    if (($log + "`n" + $stderr) -match 'stat::number_of_executed_units:\s+(\d+)') { $units = [long]$Matches[1] }
    $instrumentedCounters = 0
    if (($log + $stderr) -match 'Loaded\s+\d+\s+modules\s+\((\d+)\s+inline 8-bit counters\)') {
        $instrumentedCounters = [int]$Matches[1]
    }
    $coverageFeatures = 0
    if (($log + $stderr) -match '#\d+\s+DONE\s+cov:\s+\d+\s+ft:\s+(\d+)') { $coverageFeatures = [int]$Matches[1] }
    $status = if ($processResult.ExitCode -eq 0 -and -not $processResult.Failure -and
        $units -gt 0 -and $instrumentedCounters -gt 0 -and $coverageFeatures -gt 0) { 'passed' } else { 'failed' }
    $result = [pscustomobject]@{
        schema_version = 1; campaign = $Campaign; target = $name; engine = $config.engine.$($definition.mode).name
        seed_count = $seedCount; seed_limit = [int]$campaignConfig.seed_limit_per_target; seconds_budget = $seconds
        elapsed_ms = $processResult.ElapsedMilliseconds; executed_units = $units
        coverage_features = $coverageFeatures; instrumented_counters = $instrumentedCounters
        process_tree_peak_bytes = $processResult.PeakWorkingSetBytes; process_tree_peak_count = $processResult.PeakProcessCount
        exit_code = $processResult.ExitCode; failure = $processResult.Failure; failure_detail = $processResult.FailureDetail
        status = $status
        stdout = 'stdout.log'; stderr = 'stderr.log'; corpus = 'corpus/'; artifacts = 'artifacts/'
        source_commit = (git rev-parse HEAD).Trim(); dirty_worktree = [bool](git status --porcelain)
        toolchain = if ($definition.mode -eq 'native') { "LLVM $($config.engine.native.llvm_version), clang-cl, ASan" } else { ".NET 10, SharpFuzz $($config.engine.managed.version)" }
        flags = @("-max_total_time=$seconds", "-max_len=$($config.limits.maximum_input_bytes)", "-timeout=$($config.limits.input_timeout_seconds)", "-rss_limit_mb=$($config.limits.rss_limit_mb)", '-use_value_profile=1')
        last_phase = ((@((Get-Content -LiteralPath $stdoutPath -Tail 5) + (Get-Content -LiteralPath $stderrPath -Tail 5)) | Where-Object { $_ }) -join "`n")
    }
    $summaryPath = Join-Path $targetRoot 'summary.json'
    [IO.File]::WriteAllText($summaryPath, ($result | ConvertTo-Json -Depth 10 -Compress) + "`n", [Text.UTF8Encoding]::new($false))
    $results.Add($result)
    if ($status -ne 'passed') { $failed = $true }
    Write-Host "$name`: $status; $units executions, $coverageFeatures coverage features, $instrumentedCounters instrumented counters, $($processResult.ElapsedMilliseconds) ms."
}

$campaignResult = [pscustomobject]@{
    schema_version = 1; campaign = $Campaign; run_id = $RunId; source_commit = (git rev-parse HEAD).Trim()
    targets = $results.ToArray(); status = if ($failed) { 'failed' } else { 'passed' }
}
[IO.File]::WriteAllText((Join-Path $runRoot 'summary.json'), ($campaignResult | ConvertTo-Json -Depth 12 -Compress) + "`n", [Text.UTF8Encoding]::new($false))
if ($failed) { throw "Coverage-guided campaign failed; minimized reproducers and logs are under $runRoot." }
Write-Host "Coverage-guided $Campaign campaign passed. Reports: $runRoot"
