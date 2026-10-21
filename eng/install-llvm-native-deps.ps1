param([string]$LlvmRoot = $env:LLVM_ROOT)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$pin = Get-Content (Join-Path $PSScriptRoot 'llvm-native-dependencies.json') -Raw | ConvertFrom-Json
$source = Join-Path $root "out/dependencies/vcpkg-$($pin.vcpkg_commit)"
if (-not (Test-Path (Join-Path $source 'bootstrap-vcpkg.bat'))) {
    $archive = Join-Path $root 'out/vcpkg-source.zip'
    New-Item -ItemType Directory (Split-Path $archive -Parent) -Force | Out-Null
    Invoke-WebRequest "https://github.com/microsoft/vcpkg/archive/$($pin.vcpkg_commit).zip" -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $pin.archive_sha256) { throw 'vcpkg source digest mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root 'out/dependencies') -Force
}
if (-not (Test-Path (Join-Path $source 'vcpkg.exe'))) {
    & (Join-Path $source 'bootstrap-vcpkg.bat') -disableMetrics
    if ($LASTEXITCODE -ne 0) { throw 'vcpkg bootstrap failed.' }
}
$previous = $env:X_VCPKG_ASSET_SOURCES
try {
    $provider = Join-Path $PSScriptRoot 'fetch-native-asset.ps1'
    $pwsh = (Get-Command pwsh).Source
    $env:X_VCPKG_ASSET_SOURCES = "clear;x-script,`"$pwsh`" -NoProfile -File `"$provider`" -Url {url} -Sha512 {sha512} -Destination {dst};x-block-origin"
    $packages = @($pin.packages | ForEach-Object { "${_}:$($pin.triplet)" })
    & (Join-Path $source 'vcpkg.exe') install @packages --disable-metrics
    if ($LASTEXITCODE -ne 0) { throw 'LLVM C++ native dependency build failed.' }
} finally { $env:X_VCPKG_ASSET_SOURCES = $previous }
$installed = Join-Path $source "installed/$($pin.triplet)"
$output = Join-Path $root 'out/native/llvm-native'
$lib = Join-Path $output 'dependencies'
New-Item -ItemType Directory $lib -Force | Out-Null
Copy-Item (Join-Path $installed 'lib/*.lib') -Destination $lib
Copy-Item (Join-Path $installed 'lib/libxml2.lib') -Destination (Join-Path $lib 'xml2s.lib')
Copy-Item (Join-Path $installed 'lib/zstd.lib') -Destination (Join-Path $lib 'zstd_static.lib')
$llvm = & (Join-Path $LlvmRoot 'bin/llvm-config.exe') --link-static --libs core bitwriter bitreader passes nativecodegen orcjit
if ($LASTEXITCODE -ne 0) { throw 'LLVM C++ component inventory failed.' }
$libraries = @(($llvm -split '\s+') | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFileName($_) })
$libraries += 'psapi.lib','shell32.lib','ole32.lib','uuid.lib','advapi32.lib','ws2_32.lib','ntdll.lib'
$libraries += @(Get-ChildItem $lib -Filter '*.lib' | ForEach-Object Name)
$dependencies = [Security.SecurityElement]::Escape(($libraries -join ';') + ';%(AdditionalDependencies)')
@"
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemDefinitionGroup><Link><AdditionalDependencies>$dependencies</AdditionalDependencies></Link></ItemDefinitionGroup>
</Project>
"@ | Set-Content (Join-Path $output 'llvm-libraries.props') -Encoding utf8NoBOM
$licenses = Join-Path $output 'dependency-licenses'
New-Item -ItemType Directory $licenses -Force | Out-Null
Copy-Item (Join-Path $installed 'share/*') -Destination $licenses -Recurse -Force
Write-Host 'Pinned LLVM C++ dependency inventory is ready.'
