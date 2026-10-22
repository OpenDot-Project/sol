# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([Parameter(Mandatory)][string]$Stage, [string]$BackendDirectory = 'out/native/backend')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content (Join-Path $PSScriptRoot 'native-package.json') -Raw | ConvertFrom-Json
$backend = [IO.Path]::GetFullPath((Join-Path $root $BackendDirectory))
$facts = Get-Content (Join-Path $backend 'build-info.json') -Raw | ConvertFrom-Json
if ($facts.schema_version -ne 1 -or $facts.llvm_version -ne $pin.llvm_version) { throw 'Native package build identity mismatch.' }
$destination = Join-Path $Stage 'managed/native'
$licenses = Join-Path $Stage 'licenses/native'
New-Item -ItemType Directory -Path $destination, $licenses -Force | Out-Null
# Deliberately omit developer link-paths.txt, tools, SDK libraries, PDBs and build intermediates.
foreach ($name in $pin.files) {
    $file = Get-Item -LiteralPath (Join-Path $backend $name)
    if ($file.Length -eq 0 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Invalid native payload: $name" }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $name)
}
$cache = Join-Path $root 'out/downloads/native-notices'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
foreach ($notice in $pin.notices) {
    $file = Join-Path $cache $notice.name
    if (-not (Test-Path -LiteralPath $file)) { Invoke-WebRequest $notice.url -OutFile $file }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $notice.sha256) { throw "Native notice digest mismatch: $($notice.name)" }
    Copy-Item -LiteralPath $file -Destination $licenses
}
$dependencies = Join-Path $root 'out/native/llvm-native/dependency-licenses'
$components = @()
foreach ($name in @('zlib', 'zstd', 'libxml2')) {
    $directory = Join-Path $licenses $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $dependencies "$name/copyright") -Destination $directory
    $spdx = Get-Content (Join-Path $dependencies "$name/vcpkg.spdx.json") -Raw | ConvertFrom-Json
    $port = @($spdx.packages | Where-Object SPDXID -EQ 'SPDXRef-port')
    $binary = @($spdx.packages | Where-Object SPDXID -EQ 'SPDXRef-binary')
    if ($port.Count -ne 1 -or $binary.Count -ne 1 -or -not $port[0].versionInfo) { throw "Missing native dependency identity: $name" }
    $components += @{ name = $name; version = $port[0].versionInfo; vcpkg_binary_abi = $binary[0].versionInfo }
}
$source = Get-Content (Join-Path $PSScriptRoot 'llvm-native-dependencies.json') -Raw | ConvertFrom-Json
@{
    llvm_version = $pin.llvm_version; embedded_linker = @('LLD.COFF', 'LLD.Common')
    msvc_toolset = $facts.msvc_toolset; reference_pack = $facts.reference_pack
    runtime_abi = 0; dependencies = $components; vcpkg_commit = $source.vcpkg_commit
    prerequisites = @('Windows x64', 'Compatible Microsoft Visual C++ v14 x64 Redistributable')
    aot_prerequisites = @('MSVC x64 C++ build libraries and Windows SDK in an x64 developer shell LIB environment')
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'backend.json') -Encoding utf8NoBOM
Write-Host 'Native payload and third-party notices staged; developer paths excluded.'
