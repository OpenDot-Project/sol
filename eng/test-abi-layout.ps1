# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([Parameter(Mandatory)][string]$Harness)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = 1048576
$reader = [Xml.XmlReader]::Create((Join-Path $root 'docs/abi-manifest.xml'), $settings)
$document = [Xml.XmlDocument]::new()
$document.XmlResolver = $null
try { $document.Load($reader) } finally { $reader.Dispose() }
$json = & $Harness abi-layout
if ($LASTEXITCODE -ne 0) { throw 'Native ABI layout probe failed.' }
$probe = $json | ConvertFrom-Json -AsHashtable
if ($probe.schema -ne 1) { throw 'Native ABI probe schema mismatch.' }
$expected = @($document.'sol-abi'.layouts.layout)
if ($probe.layouts.Count -ne $expected.Count) { throw 'Native ABI layout inventory mismatch.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($layout in $probe.layouts) {
    if (-not $seen.Add($layout.name)) { throw 'Duplicate native ABI layout.' }
    $contract = @($expected | Where-Object name -CEQ $layout.name)
    if ($contract.Count -ne 1 -or $layout.size -ne [int]$contract[0].size -or
        $layout.alignment -ne [int]$contract[0].alignment) { throw "Native ABI layout drift: $($layout.name)" }
    $fields = @($contract[0].field)
    if ($layout.offsets.Count -ne $fields.Count) { throw 'Native ABI field inventory mismatch.' }
    for ($index = 0; $index -lt $fields.Count; $index++) {
        if ($layout.offsets[$index] -ne [int]$fields[$index].offset) { throw "Native ABI field offset drift: $($layout.name)" }
    }
}
Write-Host "Native sizeof/alignof/offsetof: $($expected.Count) layouts match abi-manifest.xml."
