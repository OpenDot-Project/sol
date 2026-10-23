# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([Parameter(Mandatory)][string]$VcpkgRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'NativeAdvisories.psm1') -Force
$catalog = Get-Content (Join-Path $PSScriptRoot 'native-advisories.json') -Raw | ConvertFrom-Json -AsHashtable
Assert-NativeAdvisoryCatalog $catalog
$zlib = $catalog.components | Where-Object name -CEQ 'zlib'
if (-not $zlib.Contains('tag_commit') -or $zlib.commit -ceq $zlib.tag_commit -or
    $zlib.source_archive_sha512 -cnotmatch '^[a-f0-9]{128}$') { throw 'Missing pinned zlib fix input.' }
$overlay = Join-Path $root 'out/dependencies/native-overlays'
New-Item -ItemType Directory -Path $overlay -Force | Out-Null
foreach ($name in @('zlib', 'libxml2')) {
    $source = Join-Path $VcpkgRoot "ports/$name"
    $target = Join-Path $overlay $name
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    # Copy upstream port support files; never edit the downloaded vcpkg checkout in place.
    Copy-Item (Join-Path $source '*') -Destination $target -Recurse -Force
    $file = Join-Path $target 'portfile.cmake'
    $text = [IO.File]::ReadAllText($file).Replace("`r`n", "`n")
    if ($name -eq 'zlib') {
        $ref = '    REF v${VERSION}'
        if ($text.Split($ref).Count -ne 2 -or ([regex]::Matches($text, '(?m)^    SHA512 [a-f0-9]{128}$')).Count -ne 1) {
            throw 'Pinned zlib port layout changed.'
        }
        $text = $text.Replace($ref, '    REF ' + $zlib.commit)
        $text = [regex]::Replace($text, '(?m)^    SHA512 [a-f0-9]{128}$', '    SHA512 ' + $zlib.source_archive_sha512)
    } else {
        foreach ($feature in @('HTML', 'XINCLUDE')) {
            $enabled = "-DLIBXML2_WITH_$feature=ON"
            if ($text.Split($enabled).Count -ne 2) { throw "Pinned libxml2 feature option changed: $feature" }
            $text = $text.Replace($enabled, "-DLIBXML2_WITH_$feature=OFF")
        }
    }
    [IO.File]::WriteAllText($file, $text, [Text.UTF8Encoding]::new($false))
}
Write-Output $overlay
