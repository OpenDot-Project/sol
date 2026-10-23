# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param([string]$OutputDirectory = 'out/security/native-advisories')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'NativeAdvisories.psm1') -Force
$catalog = Get-Content (Join-Path $PSScriptRoot 'native-advisories.json') -Raw | ConvertFrom-Json -AsHashtable
Assert-NativeAdvisoryCatalog $catalog
$native = Get-Content (Join-Path $PSScriptRoot 'native-package.json') -Raw | ConvertFrom-Json -AsHashtable
$dependencies = Get-Content (Join-Path $PSScriptRoot 'llvm-native-dependencies.json') -Raw | ConvertFrom-Json -AsHashtable
$llvm = Get-Content (Join-Path $PSScriptRoot 'llvm.json') -Raw | ConvertFrom-Json -AsHashtable
if ($catalog.vcpkg_commit -cne $dependencies.vcpkg_commit -or
    ($catalog.components | Where-Object name -CEQ 'llvm').version -cne $native.llvm_version -or
    $native.llvm_version -cne $llvm.version -or
    ($dependencies.packages -join ',') -cne 'zlib,zstd,libxml2[core]') {
    throw 'Advisory catalog does not match the LLVM/native dependency build pins.'
}

# Check port metadata from the hash-pinned vcpkg source before querying any native versions.
# This reads metadata only; it does not bootstrap vcpkg or build third-party code.
foreach ($component in $catalog.components | Where-Object name -CNE 'llvm') {
    $uri = "https://raw.githubusercontent.com/microsoft/vcpkg/$($catalog.vcpkg_commit)/ports/$($component.name)/vcpkg.json"
    $port = Invoke-RestMethod -Uri $uri -TimeoutSec 45
    $portVersion = if ($port.PSObject.Properties.Name -contains 'version') { $port.version }
        elseif ($port.PSObject.Properties.Name -contains 'version-semver') { $port.'version-semver' }
        elseif ($port.PSObject.Properties.Name -contains 'version-string') { $port.'version-string' }
        else { throw 'Unknown vcpkg version metadata.' }
    if ($portVersion -cne $component.version) { throw "Native advisory version mismatch: $($component.name)" }
}
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$reportPath = Join-Path $output "native-advisories-$([guid]::NewGuid().ToString('N')).json"
$results = @()
foreach ($component in $catalog.components) {
    try {
        $result = Invoke-NativeAdvisoryQuery $component {
            param($query)
            $raw = Invoke-WebRequest -Method Post -Uri 'https://api.osv.dev/v1/query' -ContentType 'application/json' `
                -Body ($query | ConvertTo-Json -Compress) -TimeoutSec 45
            return $raw.Content | ConvertFrom-Json -AsHashtable -Depth 64
        }
        $results += $result
        Write-Host "$($component.name) $($component.version): $($result.status), $($result.advisories.Count) advisory records."
    } catch {
        $results += @{ name = $component.name; version = $component.version; source_commit = $component.commit
            status = 'query-failed'; error = 'Network or advisory evidence validation failed.' }
        Write-Warning "Native advisory query failed for $($component.name): $($_.Exception.Message)"
    }
}
$source = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[a-f0-9]{40}$') { throw 'Source commit identity unavailable.' }
$report = [ordered]@{
    schema_version = 1
    source_commit = $source
    checked_at = [DateTimeOffset]::UtcNow.ToString('O')
    endpoint = 'https://api.osv.dev/v1/query'
    matching = 'native-upstream-commit'
    vcpkg_commit = $catalog.vcpkg_commit
    catalog_sha256 = (Get-FileHash (Join-Path $PSScriptRoot 'native-advisories.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    coverage = 'OSV commit range matching; not CodeQL triage, patched-binary attestation, or a complete SBOM.'
    components = $results
}
$stream = [IO.File]::Open($reportPath, [IO.FileMode]::CreateNew)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
try { $writer.Write(($report | ConvertTo-Json -Depth 64)) } finally { $writer.Dispose() }
Write-Host "Native advisory evidence: $reportPath"
if (@($results | Where-Object status -CNE 'no-known-advisories').Count) {
    throw 'Native advisory qualification failed. Findings and unknown results require review; no exceptions were applied.'
}
