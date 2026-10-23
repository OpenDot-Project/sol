# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param(
    [ValidateSet('dense', 'sparse', 'wide', 'churn', 'roots', 'concurrent')]
    [string[]]$Scenario = @('dense', 'sparse', 'wide', 'churn', 'roots', 'concurrent'),
    [ValidateRange(16, 200000)][int]$Nodes = 10000,
    [ValidateRange(1, 10)][int]$Cycles = 5,
    [ValidateRange(1, 4096)][int]$Roots = 32,
    [ValidateRange(2, 8)][int]$Threads = 4,
    [uint]$Seed = 1,
    [ValidateRange(1, 20)][int]$Repeat = 1,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 120,
    [ValidateNotNullOrEmpty()][string]$HeapLimit = '128MiB',
    [string]$OutputDirectory,
    [switch]$Events,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'RuntimeProfile.psm1') -Force
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'out/native/runtime/profiles' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if ([long]$Nodes * $Cycles * $Threads -gt 2000000 -and 'concurrent' -in $Scenario) {
    throw 'Concurrent workload exceeds the two-million allocation sample budget.'
}
if ([long]$Nodes * $Cycles -gt 2000000) { throw 'Workload exceeds the two-million allocation sample budget.' }
if (-not $SkipBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'MSVC is required for runtime profiling.' }
    $toolset = $null
    $platforms = Get-ChildItem (Join-Path $vs 'MSBuild/Microsoft/VC') -Directory | Sort-Object Name -Descending
    foreach ($platform in $platforms) {
        $directory = Join-Path $platform.FullName 'Platforms/x64/PlatformToolsets'
        if (Test-Path -LiteralPath $directory) {
            $toolset = Get-ChildItem $directory -Directory | Where-Object Name -In @('v145', 'v143') |
                Sort-Object Name -Descending | Select-Object -First 1
        }
        if ($toolset) { break }
    }
    if (-not $toolset) { throw 'The profile harness requires MSVC v143 or v145.' }
    $buildLog = Join-Path $outputRoot 'profile-build.log'
    $buildArguments = @(
        (Join-Path $root 'src/native/runtime/runtime.vcxproj'),
        '/nologo', '/nodeReuse:false', '/p:Configuration=Release', '/p:Platform=x64',
        "/p:PlatformToolset=$($toolset.Name)", '/p:TestHarness=true', '/p:StaticRuntime=true',
        '/p:SanitizeRuntime=false', '/p:EnableCppCoreCheck=true', '/p:RunCodeAnalysis=true',
        '/p:CodeAnalysisRuleSet=CppCoreCheckRules.ruleset'
    )
    & (Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe') @buildArguments *> $buildLog
    if ($LASTEXITCODE -ne 0) { throw "Runtime profile harness build failed; see $buildLog." }
}
$exe = Join-Path $root 'out/native/runtime/profile-harness/SolRT-tests.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Runtime profile executable is missing.' }
$runId = [guid]::NewGuid().ToString('N')
$names = @($Scenario | ForEach-Object { $_.ToLowerInvariant() })
if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'Duplicate workload scenarios are not allowed.' }
foreach ($name in $names) {
    for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
        $workers = if ($name -eq 'concurrent') { $Threads } else { 1 }
        $info = [Diagnostics.ProcessStartInfo]::new($exe)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.Environment['SOL_GC_HEAP_LIMIT'] = $HeapLimit
        $info.Environment['SOL_GC_DIAGNOSTICS'] = if ($Events) { '1' } else { '0' }
        foreach ($argument in @('gc-profile', $name, "$Nodes", "$Cycles", "$Roots", "$workers", "$Seed")) {
            $info.ArgumentList.Add($argument)
        }
        $child = [Diagnostics.Process]::Start($info)
        try {
            $stdout = $child.StandardOutput.ReadToEndAsync()
            $stderr = $child.StandardError.ReadToEndAsync()
            if (-not $child.WaitForExit($TimeoutSeconds * 1000)) {
                $child.Kill($true)
                $child.WaitForExit()
                throw "Runtime profile timed out for $name."
            }
            $json = $stdout.GetAwaiter().GetResult()
            $message = $stderr.GetAwaiter().GetResult()
            if ($child.ExitCode -ne 0) { throw "Runtime profile failed for ${name}: exit $($child.ExitCode): $message" }
            if ($message.Trim().Length -ne 0) { throw "Runtime profile emitted unexpected diagnostics for ${name}: $message" }
            $profile = $json | ConvertFrom-Json -AsHashtable -Depth 64
            Test-RuntimeProfile $profile
            if ($profile.settings.scenario -cne $name -or $profile.settings.nodes -ne $Nodes -or
                $profile.settings.cycles -ne $Cycles -or $profile.settings.roots -ne $Roots -or
                $profile.settings.threads -ne $workers -or $profile.settings.seed -ne $Seed -or
                $profile.event_ring.enabled -ne [bool]$Events) {
                throw 'The executable returned settings that differ from the requested workload.'
            }
            $destination = Join-Path $outputRoot "$runId-$name-$iteration.json"
            # A successful parse is not sufficient: publish only after semantic consistency checks.
            $stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try {
                $encoded = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $stream.Write($encoded, 0, $encoded.Length)
            } finally { $stream.Dispose() }
            Write-Host "$name [$iteration/$Repeat]: allocations=$($profile.allocation_latency.samples), p99=$($profile.allocation_latency.p99_ns) ns, max=$($profile.allocation_latency.max_ns) ns"
            Write-Output $destination
        } finally { $child.Dispose() }
    }
}
