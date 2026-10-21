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
& $exe
if ($LASTEXITCODE -ne 0) { throw 'ASan runtime ABI/argument-lifetime harness failed.' }
Write-Host 'ASan runtime bootstrap checks passed.'
