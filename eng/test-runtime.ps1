# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC tools are required.' }
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe'
$toolsets = Get-ChildItem (Join-Path $vs 'MSBuild/Microsoft/VC') -Directory | Sort-Object Name -Descending
$toolset = $null
foreach ($platform in $toolsets) {
    $directory = Join-Path $platform.FullName 'Platforms/x64/PlatformToolsets'
    if (Test-Path $directory) { $toolset = Get-ChildItem $directory -Directory | Where-Object Name -In @('v145', 'v143') | Sort-Object Name -Descending | Select-Object -First 1 }
    if ($toolset) { break }
}
if (-not $toolset) { throw 'MSVC platform toolset missing.' }
& $msbuild (Join-Path $root 'src/native/runtime/runtime.vcxproj') /nologo /nodeReuse:false /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$($toolset.Name)" /p:StaticRuntime=true /p:TestHarness=true /p:EnableCppCoreCheck=true /p:RunCodeAnalysis=true /p:CodeAnalysisRuleSet=CppCoreCheckRules.ruleset
if ($LASTEXITCODE -ne 0) { throw 'ASan runtime harness build failed.' }
$exe = Join-Path $root 'out/native/runtime/asan-harness/SolRT-tests.exe'
$process = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root 'out/native/runtime/asan-harness/stdout.log') -RedirectStandardError (Join-Path $root 'out/native/runtime/asan-harness/stderr.log')
try {
    if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'ASan runtime harness timed out.' }
    Get-Content (Join-Path $root 'out/native/runtime/asan-harness/stderr.log')
    if ($process.ExitCode -ne 0) { throw "ASan runtime harness failed: $($process.ExitCode)." }
} finally { $process.Dispose() }
foreach ($limit in @('0', '8MiB', '16MB', '18446744073709551615GiB', '16MiB')) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardOutput = $true
    $info.Environment['SOL_GC_HEAP_LIMIT'] = $limit
    $info.ArgumentList.Add('heap-pressure')
    $child = [Diagnostics.Process]::Start($info)
    try {
        $errorTask = $child.StandardError.ReadToEndAsync()
        $outputTask = $child.StandardOutput.ReadToEndAsync()
        if (-not $child.WaitForExit(30000)) { $child.Kill($true); throw 'Heap configuration test timed out.' }
        $message = $errorTask.GetAwaiter().GetResult()
        $null = $outputTask.GetAwaiter().GetResult()
        $expected = if ($limit -eq '16MiB') { 'OutOfMemoryError' } else { 'invalid SOL_GC_HEAP_LIMIT' }
        if ($child.ExitCode -ne 1 -or $message -notmatch $expected -or $message -match 'AddressSanitizer') { throw "Unexpected heap failure: $message" }
    } finally { $child.Dispose() }
}
foreach ($mode in @('slot-use-after-free', 'slot-overflow', 'cache-use-after-return')) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardOutput = $true
    $info.ArgumentList.Add($mode)
    $child = [Diagnostics.Process]::Start($info)
    try {
        $errorTask = $child.StandardError.ReadToEndAsync()
        $outputTask = $child.StandardOutput.ReadToEndAsync()
        if (-not $child.WaitForExit(30000)) { $child.Kill($true); throw 'Span poison self-test timed out.' }
        $message = $errorTask.GetAwaiter().GetResult()
        $null = $outputTask.GetAwaiter().GetResult()
        $message | Set-Content -LiteralPath (Join-Path $root "out/native/runtime/asan-harness/$mode.log")
        if ($child.ExitCode -eq 0 -or $child.ExitCode -eq 99 -or $message -notmatch 'AddressSanitizer: use-after-poison') {
            throw "ASan did not detect poisoned span memory for ${mode}: $message"
        }
    } finally { $child.Dispose() }
}
foreach ($mode in @('null-offsets', 'header-offset', 'unaligned-offset', 'outside-object', 'descriptor-extent', 'short-array',
                    'negative-length', 'zero-stride', 'oversized-payload', 'element-overlap', 'element-overflow')) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardOutput = $true
    $info.ArgumentList.Add("descriptor-$mode")
    $child = [Diagnostics.Process]::Start($info)
    try {
        $errorTask = $child.StandardError.ReadToEndAsync()
        $outputTask = $child.StandardOutput.ReadToEndAsync()
        if (-not $child.WaitForExit(30000)) { $child.Kill($true); throw 'Descriptor rejection timed out.' }
        $message = $errorTask.GetAwaiter().GetResult()
        $output = $outputTask.GetAwaiter().GetResult()
        if ($child.ExitCode -ne 1 -or $message -notmatch 'bootstrap invariant failed' -or
            $message -match 'AddressSanitizer' -or $output.Length -ne 0) {
            throw "Malformed descriptor reached an invalid load or was accepted: ${mode}: $message"
        }
    } finally { $child.Dispose() }
}
foreach ($setting in @('', 'true', '2', ' 1', '1 ')) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardOutput = $true
    $info.Environment['SOL_GC_DIAGNOSTICS'] = $setting
    $info.ArgumentList.Add('heap-pressure')
    $child = [Diagnostics.Process]::Start($info)
    try {
        $errorTask = $child.StandardError.ReadToEndAsync()
        $outputTask = $child.StandardOutput.ReadToEndAsync()
        if (-not $child.WaitForExit(30000)) { $child.Kill($true); throw 'Diagnostic configuration test timed out.' }
        $message = $errorTask.GetAwaiter().GetResult()
        $null = $outputTask.GetAwaiter().GetResult()
        if ($child.ExitCode -ne 1 -or $message -notmatch 'invalid SOL_GC_DIAGNOSTICS' -or $message -match 'AddressSanitizer') {
            throw 'Invalid diagnostic setting was not rejected before allocation.'
        }
    } finally { $child.Dispose() }
}
Import-Module (Join-Path $PSScriptRoot 'RuntimeProfile.psm1') -Force
$profiles = @()
foreach ($scenario in @('dense', 'sparse', 'wide', 'churn', 'roots', 'concurrent')) {
    $workers = if ($scenario -eq 'concurrent') { 3 } else { 1 }
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardOutput = $true
    $info.Environment['SOL_GC_DIAGNOSTICS'] = '1'
    $info.Environment['SOL_GC_HEAP_LIMIT'] = '128MiB'
    foreach ($argument in @('gc-profile', $scenario, '512', '2', '17', "$workers", '42')) { $info.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($info)
    try {
        $errorTask = $child.StandardError.ReadToEndAsync()
        $outputTask = $child.StandardOutput.ReadToEndAsync()
        if (-not $child.WaitForExit(60000)) { $child.Kill($true); throw 'ASan profile correctness workload timed out.' }
        $message = $errorTask.GetAwaiter().GetResult()
        $json = $outputTask.GetAwaiter().GetResult()
        if ($child.ExitCode -ne 0 -or $message.Trim().Length -ne 0) { throw "ASan profile workload failed: ${scenario}: $message" }
        $value = $json | ConvertFrom-Json -AsHashtable -Depth 64
        Test-RuntimeProfile $value -AllowSanitized
        if (-not $value.environment.address_sanitized) { throw 'ASan profile correctness run lacks its sanitizer build marker.' }
        $path = Join-Path $root "out/native/runtime/asan-harness/profile-$scenario.json"
        [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
        $profiles += $path
    } finally { $child.Dispose() }
}
& (Join-Path $PSScriptRoot 'test-runtime-profiles.ps1') -ProfilePath $profiles -AllowSanitized
Write-Host 'ASan runtime, poison/descriptor failures, barrier models, bounded tracing, profile validation and heap budget checks passed.'
