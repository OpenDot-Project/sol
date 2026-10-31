# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NativeBuildBinding {
    param([string]$BackendDirectory, [string]$QualificationPath, [string]$CatalogPath, [string[]]$PayloadNames)
    $catalogHash = (Get-FileHash -LiteralPath $CatalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $proof = Get-Content -LiteralPath $QualificationPath -Raw | ConvertFrom-Json -AsHashtable
    if ($proof.catalog_sha256 -isnot [string] -or $proof.catalog_sha256 -cne $catalogHash) {
        throw 'Native qualification catalog is stale.'
    }
    if (-not $PayloadNames.Count) { throw 'Native payload inventory is empty.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = @()
    foreach ($name in $PayloadNames) {
        if ($name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9.-]*\.(dll|exe|lib)\z' -or -not $seen.Add($name)) {
            throw 'Invalid or duplicate native payload name.'
        }
        $file = Get-Item -LiteralPath (Join-Path $BackendDirectory $name)
        if ($file.PSIsContainer -or $file.Length -le 0 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Invalid native payload file.'
        }
        $files += @{ name = $name; size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    return @{ schema_version = 1; catalog_sha256 = $catalogHash
        qualification_sha256 = (Get-FileHash -LiteralPath $QualificationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        files = $files }
}

function Assert-NativeBuildBinding {
    param($Binding, [string]$BackendDirectory, [string]$QualificationPath, [string]$CatalogPath, [string[]]$PayloadNames)
    if ($Binding -isnot [Collections.IDictionary] -or $Binding.schema_version -isnot [long] -or
        $Binding.schema_version -ne 1 -or $Binding.files -isnot [array]) {
        throw 'Missing or malformed native build binding; rebuild the backend.'
    }
    $actual = Get-NativeBuildBinding $BackendDirectory $QualificationPath $CatalogPath $PayloadNames
    foreach ($field in @('catalog_sha256', 'qualification_sha256')) {
        if ($Binding[$field] -isnot [string] -or $Binding[$field] -cnotmatch '\A[a-f0-9]{64}\z' -or
            $Binding[$field] -cne $actual[$field]) { throw 'Native build qualification binding changed.' }
    }
    if ($Binding.files.Count -ne $actual.files.Count) { throw 'Native build payload inventory changed.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Binding.files) {
        if ($file -isnot [Collections.IDictionary] -or $file.name -isnot [string] -or
            -not $seen.Add($file.name) -or $file.name -cnotin $PayloadNames -or
            $file.size -isnot [long] -or $file.size -le 0 -or $file.sha256 -isnot [string] -or
            $file.sha256 -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Malformed native build payload identity.' }
        $expected = @($actual.files | Where-Object name -CEQ $file.name)
        if ($expected.Count -ne 1 -or $file.size -ne $expected[0].size -or $file.sha256 -cne $expected[0].sha256) {
            throw 'Native payload bytes do not match the qualified build.'
        }
    }
}

Export-ModuleMember -Function Get-NativeBuildBinding, Assert-NativeBuildBinding
