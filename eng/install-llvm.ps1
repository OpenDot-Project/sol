# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content (Join-Path $PSScriptRoot 'llvm.json') -Raw | ConvertFrom-Json
$downloads = Join-Path $root 'out/downloads'
$tools = Join-Path $root 'out/toolchains'
New-Item -ItemType Directory -Path $downloads, $tools -Force | Out-Null
$archive = Join-Path $downloads "llvm-$($pin.version)-windows-x64.tar.xz"
if (-not (Test-Path $archive)) { Invoke-WebRequest $pin.url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pin.sha256) { throw 'LLVM SHA-256 mismatch.' }
$directory = Join-Path $tools $pin.directory
if (-not (Test-Path (Join-Path $directory 'bin/llvm-config.exe'))) {
    & tar -xf $archive -C $tools
    if ($LASTEXITCODE -ne 0) { throw 'LLVM extraction failed.' }
}
$actual = & (Join-Path $directory 'bin/llvm-config.exe') --version
if ($LASTEXITCODE -ne 0 -or $actual -ne $pin.version) { throw 'LLVM version mismatch.' }
if ($env:GITHUB_ENV) { "LLVM_ROOT=$directory" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8 }
Write-Host "LLVM_ROOT=$directory"
