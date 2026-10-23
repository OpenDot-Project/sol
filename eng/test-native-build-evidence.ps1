# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeBuildEvidence.psm1') -Force
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('sol-build-binding-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$script:rejected = 0
function Copy-Data($Value) { $Value | ConvertTo-Json -Depth 10 | ConvertFrom-Json -AsHashtable }
function Expect-Rejection([string]$Name, [scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw "Accepted invalid native build evidence: $Name" }
    $script:rejected++
}
try {
    $catalog = Join-Path $scratch 'catalog.json'
    $proof = Join-Path $scratch 'qualification.json'
    [IO.File]::WriteAllText($catalog, '{"fixture":"catalog"}')
    $catalogHash = (Get-FileHash $catalog -Algorithm SHA256).Hash.ToLowerInvariant()
    @{ catalog_sha256 = $catalogHash } | ConvertTo-Json | Set-Content $proof -Encoding utf8NoBOM
    $names = @('Compiler.dll', 'Runtime.lib')
    foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $scratch $name), 'fixture-native-payload') }
    $binding = Copy-Data (Get-NativeBuildBinding $scratch $proof $catalog $names)
    Assert-NativeBuildBinding $binding $scratch $proof $catalog $names
    # Inventory order is not a payload identity; the exact case-sensitive set is.
    $reordered = Copy-Data $binding
    [array]::Reverse($reordered.files)
    Assert-NativeBuildBinding $reordered $scratch $proof $catalog $names
    $cases = [ordered]@{
        'old build without binding' = { param($v) return $null }
        'unknown schema' = { param($v) $v.schema_version = 2; return $v }
        'array catalog digest' = { param($v) $v.catalog_sha256 = @($v.catalog_sha256); return $v }
        'stale qualification digest' = { param($v) $v.qualification_sha256 = ('0' * 64); return $v }
        'missing payload' = { param($v) $v.files = @($v.files[0]); return $v }
        'duplicate payload' = { param($v) $v.files[1] = $v.files[0]; return $v }
        'unexpected payload' = { param($v) $v.files[0].name = 'Other.dll'; return $v }
        'path traversal' = { param($v) $v.files[0].name = '../Compiler.dll'; return $v }
        'wrong case identity' = { param($v) $v.files[0].name = 'compiler.dll'; return $v }
        'array payload digest' = { param($v) $v.files[0].sha256 = @($v.files[0].sha256); return $v }
        'string payload length' = { param($v) $v.files[0].size = "$($v.files[0].size)"; return $v }
        'wrong payload length' = { param($v) $v.files[0].size++; return $v }
    }
    foreach ($case in $cases.GetEnumerator()) {
        $value = & $case.Value (Copy-Data $binding)
        Expect-Rejection $case.Key { Assert-NativeBuildBinding $value $scratch $proof $catalog $names }
    }
    $payload = Join-Path $scratch $names[0]
    $original = [IO.File]::ReadAllText($payload)
    [IO.File]::WriteAllText($payload, ('x' * $original.Length))
    Expect-Rejection 'same-size replaced native binary' { Assert-NativeBuildBinding $binding $scratch $proof $catalog $names }
    [IO.File]::WriteAllText($payload, $original)
    [IO.File]::AppendAllText($proof, "`n")
    Expect-Rejection 'replaced qualification bytes' { Assert-NativeBuildBinding $binding $scratch $proof $catalog $names }
    [IO.File]::AppendAllText($catalog, "`n")
    Expect-Rejection 'changed source catalog' { Assert-NativeBuildBinding $binding $scratch $proof $catalog $names }
    [IO.File]::WriteAllText($catalog, '{"fixture":"catalog"}')
    Expect-Rejection 'duplicate build inventory' { Get-NativeBuildBinding $scratch $proof $catalog @('Compiler.dll', 'Compiler.dll') }
    Write-Host "Native build evidence passed: $script:rejected rejection cases and exact/reordered inventory positives."
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $boundary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'sol-build-binding-*') { throw 'Unexpected fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
