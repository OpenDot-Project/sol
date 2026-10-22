# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content (Join-Path $PSScriptRoot 'doxygen.json') -Raw | ConvertFrom-Json
$downloads = Join-Path $root 'out/downloads'
$tools = Join-Path $root "out/toolchains/doxygen-$($pin.version)"
New-Item -ItemType Directory -Path $downloads, $tools -Force | Out-Null
$archive = Join-Path $downloads "doxygen-$($pin.version).zip"
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $pin.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pin.sha256) { throw 'Doxygen SHA-256 mismatch.' }
if (-not (Test-Path (Join-Path $tools 'doxygen.exe'))) { Expand-Archive -LiteralPath $archive -DestinationPath $tools -Force }
$executable = Join-Path $tools 'doxygen.exe'
$version = & $executable --version
if ($LASTEXITCODE -ne 0 -or ($version.Trim() -split '\s+')[0] -ne $pin.version) { throw 'Doxygen version mismatch.' }
Push-Location $root
try {
    New-Item -ItemType Directory -Path out/doxygen -Force | Out-Null
    & $executable Doxyfile
    if ($LASTEXITCODE -ne 0) { throw 'Doxygen native documentation validation failed.' }
    if (-not (Test-Path out/doxygen/html/index.html)) { throw 'Missing Doxygen HTML output.' }
    [xml]$index = Get-Content out/doxygen/xml/index.xml -Raw
    foreach ($name in @('SolRT.h', 'Gc.h', 'Core.h', 'GcWorker.cpp', 'Api.h', 'Session.h', 'Compiler.cpp', 'main.cpp', 'Main.cpp')) {
        if ($name -notin @($index.doxygenindex.compound.name)) { throw "Native documentation input is missing: $name" }
    }
    foreach ($name in @('OpenDot::Sol::LlvmBridge::Compiler', 'OpenDot::Sol::Runtime::TypeDescriptor', 'OpenDot::Sol::Runtime::StringArray', 'OpenDot::Sol::Runtime::RootFrame', 'OpenDot::Sol::Runtime::ExceptionCarrier')) {
        if ($name -notin @($index.doxygenindex.compound.name)) { throw "Native API extraction is missing: $name" }
    }
    Write-Host "Doxygen $($pin.version) native HTML/XML validation passed."
} finally { Pop-Location }
