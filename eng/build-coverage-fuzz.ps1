# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param(
    [ValidateSet('all', 'runtime', 'backend', 'gc')]
    [string[]]$Target = @('all')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if (-not $env:LLVM_ROOT) { throw 'LLVM_ROOT must point to the pinned LLVM 23.1.3 installation.' }
$clang = Join-Path $env:LLVM_ROOT 'bin/clang-cl.exe'
if (-not (Test-Path -LiteralPath $clang -PathType Leaf)) { throw "clang-cl is missing: $clang" }
$version = (& $clang --version | Select-Object -First 1)
if ($version -notmatch 'clang version 23\.1\.3(?:\s|$)') { throw "Expected LLVM 23.1.3, found: $version" }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ build tools are required by the LLVM Windows toolchain.' }
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe'
$project = Join-Path $root 'src/native/fuzz/CoverageFuzz.proj'
$targets = if ($Target.Count -eq 1 -and $Target[0] -eq 'all') { @('runtime', 'backend', 'gc') } else { @($Target | Select-Object -Unique) }
if ($targets -contains 'all') { throw 'Target=all cannot be combined with individual targets.' }
foreach ($name in $targets) {
    & $msbuild $project /nologo /m:1 /nodeReuse:false "/p:LLVM_ROOT=$env:LLVM_ROOT" "/p:FuzzTarget=$name"
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed for the $name coverage-guided fuzz target." }
}
Write-Host "Built LLVM 23.1.3 libFuzzer targets: $($targets -join ', ')."
