# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC tools are required for native fuzz replay.' }
$toolset = $null
foreach ($platform in (Get-ChildItem (Join-Path $vs 'MSBuild/Microsoft/VC') -Directory | Sort-Object Name -Descending)) {
    $directory = Join-Path $platform.FullName 'Platforms/x64/PlatformToolsets'
    if (Test-Path $directory) {
        $toolset = Get-ChildItem $directory -Directory | Where-Object Name -In @('v145', 'v143') |
            Sort-Object Name -Descending | Select-Object -First 1
    }
    if ($toolset) { break }
}
if (-not $toolset) { throw 'Qualified MSVC toolset is unavailable.' }
& (Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe') (Join-Path $root 'src/native/runtime/runtime.vcxproj') `
    /nologo /m:1 /nodeReuse:false /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$($toolset.Name)" `
    /p:StaticRuntime=true /p:TestHarness=true /p:FuzzHarness=true /p:SanitizeRuntime=true `
    /p:EnableCppCoreCheck=true /p:RunCodeAnalysis=true /p:CodeAnalysisRuleSet=CppCoreCheckRules.ruleset
if ($LASTEXITCODE) { throw 'Native ASan fuzz worker build failed.' }
Write-Host 'Native ASan fuzz worker built in its own output/intermediate flavor.'
