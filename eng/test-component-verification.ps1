# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root "out/package-verification/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$cases = @('valid', 'digest', 'unlisted', 'duplicate', 'traversal', 'missing', 'size', 'no-manifest', 'symlink', 'private')
try {
    foreach ($case in $cases) {
        $archive = Join-Path $scratch "$case.zip"
        $paths = @('bin/solc.exe', 'managed/OpenDot.Sol.Cli.dll', 'runtime/dotnet.exe', 'licenses/LICENSE', 'licenses/NOTICE')
        if ($case -eq 'missing') { $paths = @($paths | Where-Object { $_ -ne 'licenses/NOTICE' }) }
        $files = @()
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($path in $paths) {
                $bytes = [Text.Encoding]::UTF8.GetBytes("fixture:$path")
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                $size = $bytes.Length
                if ($path -eq 'bin/solc.exe') {
                    if ($case -eq 'digest') { $hash = '0' * 64 }
                    if ($case -eq 'size') { $size++ }
                }
                $files += @{ path = $path; size = $size; sha256 = $hash }
                $item = $zip.CreateEntry("$case/$path")
                if ($case -eq 'symlink' -and $path -eq 'bin/solc.exe') { $item.ExternalAttributes = -1577058304 } # Unix symlink mode 0xA2000000.
                $entry = $item.Open()
                try { $entry.Write($bytes) } finally { $entry.Dispose() }
            }
            $extra = switch ($case) {
                'unlisted' { 'managed/hidden.dll' }
                'duplicate' { 'BIN/SOLC.EXE' }
                'traversal' { '../outside.exe' }
                'private' { 'managed/.codex/AGENTS.md' }
                default { $null }
            }
            if ($extra) { $zip.CreateEntry("$case/$extra").Open().Dispose() }
            if ($case -ne 'no-manifest') {
                $manifest = @{ schema_version = 1; product = 'solc'; version = '0.0.1'; target = 'windows-x86_64'; files = $files } | ConvertTo-Json -Depth 5
                $writer = [IO.StreamWriter]::new($zip.CreateEntry("$case/component.json").Open())
                try { $writer.Write($manifest) } finally { $writer.Dispose() }
            }
        } finally { $zip.Dispose() }
        $failure = $null
        try { & (Join-Path $PSScriptRoot 'verify-component.ps1') -Archive $archive }
        catch { $failure = $_ }
        if ($case -eq 'valid' -and $failure) { throw $failure }
        if ($case -ne 'valid' -and -not $failure) { throw "Malformed component unexpectedly accepted: $case" }
    }
    Write-Host "Component verification regression checks passed: $($cases.Count)."
} finally {
    $boundary = [IO.Path]::GetFullPath((Join-Path $root 'out/package-verification')) + [IO.Path]::DirectorySeparatorChar
    if (-not $scratch.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected verification cleanup root.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
