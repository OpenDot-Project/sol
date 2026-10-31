# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([string]$LlvmRoot = $env:LLVM_ROOT)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content (Join-Path $PSScriptRoot 'llvm-native-dependencies.json') -Raw | ConvertFrom-Json
$dependencyRoot = if ($env:SOL_DEPENDENCY_ROOT) { [IO.Path]::GetFullPath($env:SOL_DEPENDENCY_ROOT) } else { Join-Path $root 'out/dependencies' }
New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
$source = Join-Path $dependencyRoot "vcpkg-$($pin.vcpkg_commit)"
if (-not (Test-Path (Join-Path $source 'bootstrap-vcpkg.bat'))) {
    $archive = Join-Path $dependencyRoot 'vcpkg-source.zip'
    New-Item -ItemType Directory (Split-Path $archive -Parent) -Force | Out-Null
    Invoke-WebRequest "https://github.com/microsoft/vcpkg/archive/$($pin.vcpkg_commit).zip" -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $pin.archive_sha256) { throw 'vcpkg source digest mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $dependencyRoot -Force
}
if (-not (Test-Path (Join-Path $source 'vcpkg.exe'))) {
    & (Join-Path $source 'bootstrap-vcpkg.bat') -disableMetrics
    if ($LASTEXITCODE -ne 0) { throw 'vcpkg bootstrap failed.' }
}
$previous = $env:X_VCPKG_ASSET_SOURCES
$previousConcurrency = $env:VCPKG_MAX_CONCURRENCY
$overlay = & (Join-Path $PSScriptRoot 'prepare-native-overlays.ps1') -VcpkgRoot $source
$installRoot = Join-Path $dependencyRoot 'qualified-installed'
if (-not $previousConcurrency) { $env:VCPKG_MAX_CONCURRENCY = '2' }
try {
    $provider = Join-Path $PSScriptRoot 'fetch-native-asset.ps1'
    $pwsh = (Get-Command pwsh).Source
    $env:X_VCPKG_ASSET_SOURCES = "clear;x-script,`"$pwsh`" -NoProfile -File `"$provider`" -Url {url} -Sha512 {sha512} -Destination {dst};x-block-origin"
    $packages = @($pin.packages | ForEach-Object { "${_}:$($pin.triplet)" })
    & (Join-Path $source 'vcpkg.exe') install @packages "--overlay-ports=$overlay" "--x-install-root=$installRoot" --disable-metrics
    if ($LASTEXITCODE -ne 0) { throw 'LLVM C++ native dependency build failed.' }
} finally { $env:X_VCPKG_ASSET_SOURCES = $previous; $env:VCPKG_MAX_CONCURRENCY = $previousConcurrency }
$installed = Join-Path $installRoot $pin.triplet
$output = Join-Path $root 'out/native/llvm-native'
$lib = Join-Path $output 'dependencies'
New-Item -ItemType Directory $lib -Force | Out-Null
Copy-Item (Join-Path $installed 'lib/*.lib') -Destination $lib
Copy-Item (Join-Path $installed 'lib/libxml2.lib') -Destination (Join-Path $lib 'xml2s.lib')
Copy-Item (Join-Path $installed 'lib/zstd.lib') -Destination (Join-Path $lib 'zstd_static.lib')
$zlib = @(Get-ChildItem (Join-Path $installed 'lib') -Filter '*.lib' | Where-Object BaseName -In @('zlib', 'zlibstatic', 'z', 'zs', 'libzs'))
if ($zlib.Count -ne 1) { throw 'Ambiguous qualified zlib library.' }
Copy-Item -LiteralPath $zlib[0].FullName -Destination (Join-Path $lib 'zs.lib')
$llvm = & (Join-Path $LlvmRoot 'bin/llvm-config.exe') --link-static --libs core bitwriter bitreader passes nativecodegen orcjit libdriver dlltooldriver windowsmanifest windowsdriver lto dtlto debuginfopdb option all-targets
if ($LASTEXITCODE -ne 0) { throw 'LLVM C++ component inventory failed.' }
$libraries = @(($llvm -split '\s+') | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFileName($_) })
$libraries += 'psapi.lib','shell32.lib','ole32.lib','uuid.lib','advapi32.lib','ws2_32.lib','ntdll.lib','bcrypt.lib'
$libraries += 'lldCOFF.lib','lldCommon.lib'
$libraries += @(Get-ChildItem (Join-Path $installed 'lib') -Filter '*.lib' | ForEach-Object Name)
$libraries += 'xml2s.lib', 'zstd_static.lib', 'zs.lib'
$dependencies = [Security.SecurityElement]::Escape(($libraries -join ';') + ';%(AdditionalDependencies)')
@"
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemDefinitionGroup><Link><AdditionalDependencies>$dependencies</AdditionalDependencies></Link></ItemDefinitionGroup>
</Project>
"@ | Set-Content (Join-Path $output 'llvm-libraries.props') -Encoding utf8NoBOM
$licenses = Join-Path $output 'dependency-licenses'
New-Item -ItemType Directory $licenses -Force | Out-Null
foreach ($name in @('zlib', 'zstd', 'libxml2')) {
    $directory = Join-Path $licenses $name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($file in @('copyright', 'vcpkg.spdx.json')) {
        Copy-Item -LiteralPath (Join-Path $installed "share/$name/$file") -Destination (Join-Path $directory $file)
    }
}
Write-Host 'Pinned LLVM C++ dependency inventory is ready.'
