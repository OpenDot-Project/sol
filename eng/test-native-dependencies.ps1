# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$installed = Join-Path $root 'out/dependencies/qualified-installed/x64-windows-static'
$catalogPath = Join-Path $PSScriptRoot 'native-advisories.json'
$catalog = Get-Content $catalogPath -Raw | ConvertFrom-Json -AsHashtable
$header = Join-Path $installed 'include/libxml2/libxml/xmlversion.h'
# The C++ harness checks the preprocessed macros: generated headers retain defines inside #if 0.
$zlib = @(Get-ChildItem (Join-Path $installed 'lib') -Filter '*.lib' | Where-Object BaseName -In @('zlib', 'zlibstatic', 'z', 'zs', 'libzs'))
if ($zlib.Count -ne 1) { throw 'Ambiguous zlib static library identity.' }
$xmlLibrary = Join-Path $installed 'lib/libxml2.lib'
$symbols = & (Join-Path $env:LLVM_ROOT 'bin/llvm-nm.exe') --defined-only $xmlLibrary 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect libxml2 definitions.' }
$definitions = $symbols -join "`n"
if ($definitions -match '\b(html[A-Z][A-Za-z0-9_]*|xmlXInclude[A-Za-z0-9_]*)\b' -or
    $definitions -notmatch '\bxmlReadMemory\b' -or $definitions -notmatch '\bxmlDocGetRootElement\b') {
    throw 'XML feature absence or required XML parser definitions could not be established.'
}
foreach ($name in @('zlib', 'libxml2')) {
    $spdx = Get-Content (Join-Path $installed "share/$name/vcpkg.spdx.json") -Raw | ConvertFrom-Json
    $component = $catalog.components | Where-Object name -CEQ $name
    $resource = @($spdx.packages | Where-Object SPDXID -EQ 'SPDXRef-resource-0')
    $ref = if ($name -eq 'zlib') { $component.commit } else { $component.tag }
    if ($resource.Count -ne 1 -or $resource[0].downloadLocation -cne "git+$($component.repository)@$ref") {
        throw "Native source provenance mismatch: $name"
    }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC is required for dependency qualification.' }
$toolset = $null
foreach ($platform in (Get-ChildItem (Join-Path $vs 'MSBuild/Microsoft/VC') -Directory | Sort-Object Name -Descending)) {
    $path = Join-Path $platform.FullName 'Platforms/x64/PlatformToolsets'
    if (Test-Path $path) { $toolset = Get-ChildItem $path -Directory | Where-Object Name -In @('v145', 'v143') | Sort-Object Name -Descending | Select-Object -First 1 }
    if ($toolset) { break }
}
if (-not $toolset) { throw 'Qualified native toolset unavailable.' }
& (Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe') (Join-Path $root 'src/native/dependencies/dependencies.vcxproj') `
    /nologo /nodeReuse:false /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$($toolset.Name)" `
    "/p:ZlibLibrary=$($zlib[0].FullName)" /p:EnableCppCoreCheck=true /p:RunCodeAnalysis=true /p:CodeAnalysisRuleSet=CppCoreCheckRules.ruleset
if ($LASTEXITCODE -ne 0) { throw 'Native dependency harness build failed.' }
& (Join-Path $root 'out/native/dependency-tests/NativeDependencyTests.exe')
if ($LASTEXITCODE -ne 0) { throw "Native dependency regression failed: $LASTEXITCODE" }
$evidence = [ordered]@{
    schema_version = 1
    catalog_sha256 = (Get-FileHash $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    zlib_commit = ($catalog.components | Where-Object name -CEQ 'zlib').commit
    zlib_library = $zlib[0].Name
    zlib_sha256 = (Get-FileHash $zlib[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    libxml2_sha256 = (Get-FileHash $xmlLibrary -Algorithm SHA256).Hash.ToLowerInvariant()
    xml_header_sha256 = (Get-FileHash $header -Algorithm SHA256).Hash.ToLowerInvariant()
    excluded_features = @('HTML', 'XINCLUDE')
    absent_symbol_families = @('html[A-Z]*', 'xmlXInclude*')
    gzip_failed_write_regression = 'passed'
    xml_parser_and_compression_smoke = 'passed'
}
$output = Join-Path $root 'out/native/llvm-native/dependency-qualification.json'
$evidence | ConvertTo-Json -Depth 6 | Set-Content $output -Encoding utf8NoBOM
Write-Host 'Native dependency hashes, source identities, excluded code and ASan smoke qualified.'
