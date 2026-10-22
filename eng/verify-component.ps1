# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([Parameter(Mandatory)][string]$Archive)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$name = [IO.Path]::GetFileNameWithoutExtension($Archive)
$prefix = "$name/"
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
try {
    if ($zip.Entries.Count -gt 5000) { throw 'Component entry limit exceeded.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    foreach ($entry in $zip.Entries) {
        $path = $entry.FullName
        if (-not $path.StartsWith($prefix, [StringComparison]::Ordinal) -or $path.Contains('\') -or
            $path -match '(^|/)(\.{1,2}|\.codex|\.dotnet|\.git|obj)(/|$)' -or $path.Contains(':') -or
            $path.EndsWith('/') -or -not $seen.Add($path)) { throw "Invalid component entry: $path" }
        if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "Symbolic link in component: $path" }
        if ($entry.Length -gt 512MB) { throw 'Component entry size limit exceeded.' }
        $total += $entry.Length
    }
    if ($total -gt 1GB) { throw 'Component expanded-size limit exceeded.' }
    $metadata = $zip.GetEntry("${prefix}component.json")
    if (-not $metadata -or $metadata.Length -gt 5MB) { throw 'Component inventory is missing or too large.' }
    $reader = [IO.StreamReader]::new($metadata.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.schema_version -ne 1 -or $manifest.product -ne 'solc' -or
        $manifest.target -ne 'windows-x86_64' -or "$($manifest.version)" -eq '') { throw 'Invalid component identity.' }
    $inventory = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        $path = [string]$file.path
        if ($path -notmatch '^(bin/|managed/|runtime/|licenses/|CHANGELOG\.md$)' -or
            -not $inventory.Add($path) -or $path -eq 'component.json') { throw "Invalid inventory path: $path" }
        $entry = $zip.GetEntry("$prefix$path")
        if (-not $entry -or $entry.Length -ne $file.size -or $file.sha256 -notmatch '^[0-9a-f]{64}$') { throw "Component size/identity mismatch: $path" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -ne $file.sha256) { throw "Component digest mismatch: $path" }
    }
    if ($inventory.Count + 1 -ne $zip.Entries.Count) { throw 'Component contains unlisted files.' }
    foreach ($required in @('bin/solc.exe', 'managed/OpenDot.Sol.Cli.dll', 'runtime/dotnet.exe', 'licenses/LICENSE', 'licenses/NOTICE')) {
        if (-not $inventory.Contains($required)) { throw "Required component file missing: $required" }
    }
    Write-Host "Component archive verified: $($inventory.Count) inventoried files."
} finally { $zip.Dispose() }
