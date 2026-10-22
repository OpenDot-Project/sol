# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC C++ tools are required for the native launcher.' }
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe'
$platforms = Get-ChildItem (Join-Path $vs 'MSBuild/Microsoft/VC') -Directory | Sort-Object Name -Descending
$toolset = $null
foreach ($platform in $platforms) {
    $candidate = Join-Path $platform.FullName 'Platforms/x64/PlatformToolsets'
    if (Test-Path -LiteralPath $candidate) {
        $toolset = Get-ChildItem -LiteralPath $candidate -Directory | Where-Object Name -In @('v145', 'v143') | Sort-Object Name -Descending | Select-Object -First 1
        if ($toolset) { break }
    }
}
if (-not $toolset) { throw 'The launcher requires MSVC v143 or v145 with C++20 support.' }
& $msbuild (Join-Path $root 'src/native/launcher/launcher.vcxproj') /nologo /nodeReuse:false /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$($toolset.Name)" /p:EnableCppCoreCheck=true /p:RunCodeAnalysis=true /p:CodeAnalysisRuleSet=CppCoreCheckRules.ruleset
if ($LASTEXITCODE -ne 0) { throw 'Native launcher build failed.' }
