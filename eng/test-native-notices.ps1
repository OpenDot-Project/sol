# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativePackageNotices.psm1') -Force
$scratch = Join-Path (Split-Path $PSScriptRoot -Parent) "out/notice-tests/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$valid = [Text.Encoding]::UTF8.GetBytes('pinned LLVM notice bytes')
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($valid)).ToLowerInvariant()
$gitiles = 'https://llvm.googlesource.com/llvm-project/+/refs/tags/llvmorg-23.1.3/llvm/LICENSE.TXT?format=TEXT'
$raw = 'https://raw.githubusercontent.com/llvm/llvm-project/llvmorg-23.1.3/llvm/LICENSE.TXT'
$transportState = @{ Attempts = [Collections.Generic.List[uri]]::new(); Responses = @(); Valid = $valid }
$transport = {
    param([uri]$Uri, [string]$OutFile)
    $transportState.Attempts.Add($Uri)
    $response = $transportState.Responses[$transportState.Attempts.Count - 1]
    if ($response -eq 'failure') { throw 'Simulated HTTP 429.' }
    if ($response -eq 'valid-text') { $bytes = $transportState.Valid }
    elseif ($response -eq 'valid-base64') { $bytes = [Text.Encoding]::ASCII.GetBytes([Convert]::ToBase64String($transportState.Valid)) }
    elseif ($response -eq 'invalid-text') { $bytes = [Text.Encoding]::UTF8.GetBytes('<html>rate limited</html>') }
    else { throw 'Unexpected fixture response.' }
    [IO.File]::WriteAllBytes($OutFile, [byte[]]$bytes)
}.GetNewClosure()

function New-Notice([string[]]$Sources = @($gitiles, $raw)) {
    $entries = @()
    foreach ($source in $Sources) {
        $entries += if ($source -match '\?format=TEXT\z') { @{ url = $source; encoding = 'base64' } }
            else { @{ url = $source; encoding = 'identity' } }
    }
    return [pscustomobject]@{ name = 'llvm-LICENSE.txt'; sha256 = $script:digest; sources = $entries }
}
function Assert-Rejected([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid native notice case: $Name" }
}
function Invoke-Case([string[]]$Responses, [string[]]$Sources, [bool]$Succeeds, [string]$Name, [string[]]$ExpectedHosts = @()) {
    $transportState.Responses = $Responses
    $transportState.Attempts.Clear()
    $destination = Join-Path $scratch "$Name.txt"
    $failed = $false
    $failureMessage = ''
    try { Save-PinnedNativeNotice -Notice (New-Notice $Sources) -Destination $destination -Transport $transport -WarningAction SilentlyContinue }
    catch { $failed = $true; $failureMessage = $_.Exception.Message }
    if ($failed -eq $Succeeds) { throw "Native notice result mismatch: $Name $failureMessage" }
    if ($ExpectedHosts.Count -gt 0 -and (($transportState.Attempts | ForEach-Object Host) -join ',') -cne ($ExpectedHosts -join ',')) {
        throw "Native notice source order mismatch: $Name"
    }
    if ($Succeeds) {
        $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($actual -ine $script:digest) { throw "Native notice cache was not pinned: $Name" }
    } elseif (Test-Path -LiteralPath $destination) { throw "Failed native notice replaced destination: $Name" }
    if (@(Get-ChildItem -LiteralPath $scratch -Filter '*.partial').Count -ne 0) { throw "Partial native notice leaked: $Name" }
}

try {
    Invoke-Case @('valid-base64') @($gitiles, $raw) $true 'gitiles-pinned-success' @('llvm.googlesource.com')
    Invoke-Case @('failure', 'valid-base64') @($raw, $gitiles) $true 'github-429-fallback' @('raw.githubusercontent.com', 'llvm.googlesource.com')
    Invoke-Case @('invalid-text', 'valid-text') @($gitiles, $raw) $true 'wrong-bytes-fallback'
    Invoke-Case @('failure', 'failure') @($gitiles, $raw) $false 'all-sources-unavailable'
    Invoke-Case @('invalid-text', 'invalid-text') @($gitiles, $raw) $false 'all-sources-unpinned'

    $transportState.Responses = @('valid-base64')
    $transportState.Attempts.Clear()
    $overwrite = Join-Path $scratch 'existing.txt'
    [IO.File]::WriteAllText($overwrite, 'preserve existing content')
    Assert-Rejected 'destination overwrite' { Save-PinnedNativeNotice (New-Notice) $overwrite -Transport $transport }
    if ([IO.File]::ReadAllText($overwrite) -cne 'preserve existing content' -or $transportState.Attempts.Count -ne 0) {
        throw 'Existing native notice was modified or downloaded over.'
    }

    $badUri = New-Notice @('http://llvm.googlesource.com/llvm-project/notice')
    Assert-Rejected 'non-HTTPS source' { Save-PinnedNativeNotice $badUri (Join-Path $scratch 'http.txt') -Transport $transport }
    $badDigest = New-Notice
    $badDigest.sha256 = 'not-a-sha256'
    Assert-Rejected 'malformed digest' { Save-PinnedNativeNotice $badDigest (Join-Path $scratch 'digest.txt') -Transport $transport }
    if ($transportState.Attempts.Count -ne 0) { throw 'Invalid notice policy made a network request.' }
    Write-Host 'Native notice acquisition: 8 pinned transport, fallback, decoding, and preservation cases passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $boundary = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'out/notice-tests')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected notice test cleanup root.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
