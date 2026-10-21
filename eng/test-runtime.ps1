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
Write-Host 'ASan runtime, concurrent collector, exception roots, and heap budget checks passed.'
