# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$provider = Join-Path $PSScriptRoot 'fetch-native-asset.ps1'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root "out/asset-tests/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $directory -Force | Out-Null
$destination = Join-Path $directory 'asset.bin'
$valid = [Text.Encoding]::UTF8.GetBytes('pinned native asset')
$digest = [Convert]::ToHexString([Security.Cryptography.SHA512]::HashData($valid))
$state = @{ Attempts = [Collections.Generic.List[uri]]::new(); Responses = @(); Valid = $valid }

# Exercise the actual provider with deterministic transport failures and hostile bytes.
${function:Invoke-WebRequest} = {
    param([uri]$Uri, [string]$OutFile, [int]$TimeoutSec)
    if ($Uri.Scheme -ne 'https' -or $TimeoutSec -ne 120) { throw 'Unexpected transport policy.' }
    $index = $state.Attempts.Count
    $state.Attempts.Add($Uri)
    $response = $state.Responses[$index]
    if ($response -eq 'failure') { throw 'Simulated unavailable mirror.' }
    $bytes = if ($response -eq 'valid') { $state.Valid } else { [Text.Encoding]::UTF8.GetBytes('<html>Verification Required</html>') }
    [IO.File]::WriteAllBytes($OutFile, $bytes)
}.GetNewClosure()

function Assert-AssetCase {
    param(
        [string[]]$Responses,
        [string[]]$Hosts,
        [bool]$Succeeds,
        [string]$Url = 'https://mirror.msys2.org/msys/x86_64/sample.pkg.tar.zst',
        [string]$Hash = $digest,
        [string[]]$Paths = @('/msys/x86_64/sample.pkg.tar.zst')
    )
    $state.Responses = $Responses
    $state.Attempts.Clear()
    [IO.File]::WriteAllText($destination, 'previous destination')
    $failureMessage = 'No exception was raised.'
    $failed = $false
    try { & $provider -Url $Url -Sha512 $Hash -Destination $destination -WarningAction SilentlyContinue }
    catch { $failed = $true; $failureMessage = $_.Exception.Message }
    if ($failed -eq $Succeeds) { throw "Asset provider success/failure mismatch for $Url ($($Responses -join ',')): $failureMessage" }
    if ((($state.Attempts | ForEach-Object Host) -join ',') -ne ($Hosts -join ',')) { throw 'Asset provider mirror order mismatch.' }
    if ($Paths.Count -ne 1 -and $state.Attempts.Count -ne $Paths.Count) {
        throw 'Asset provider path expectation count mismatch.'
    }
    for ($index = 0; $index -lt $state.Attempts.Count; $index++) {
        if ($Paths.Count -eq 1) {
            if ($state.Attempts[$index].AbsolutePath -cnotmatch ([regex]::Escape($Paths[0]) + '$')) {
                throw 'Asset provider changed a pinned source path.'
            }
        } elseif ($state.Attempts[$index].AbsolutePath -cne $Paths[$index]) {
            throw 'Asset provider changed a pinned source path.'
        }
    }
    if ($Succeeds) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA512).Hash -ne $digest) { throw 'Unverified destination.' }
    } elseif ([IO.File]::ReadAllText($destination) -ne 'previous destination') { throw 'Failed download replaced the destination.' }
    if (@(Get-ChildItem -LiteralPath $directory -Filter '*.partial').Count -ne 0) { throw 'Partial download leaked.' }
}

Assert-AssetCase @('valid') @('mirror.msys2.org') $true
Assert-AssetCase @('failure', 'valid') @('mirror.msys2.org', 'repo.msys2.org') $true
Assert-AssetCase @('invalid', 'failure', 'valid') @('mirror.msys2.org', 'repo.msys2.org', 'mirror.selfnet.de') $true
Assert-AssetCase @('invalid', 'failure', 'invalid') @('mirror.msys2.org', 'repo.msys2.org', 'mirror.selfnet.de') $false
Assert-AssetCase @('failure') @('example.invalid') $false -Url 'https://example.invalid/msys/x86_64/sample.pkg.tar.zst'
Assert-AssetCase @() @() $false -Url 'http://mirror.msys2.org/msys/x86_64/sample.pkg.tar.zst'
Assert-AssetCase @() @() $false -Hash 'not-a-sha512'
$githubCommit = 'a' * 40
$githubArchive = "https://github.com/madler/zlib/archive/$githubCommit.tar.gz"
$githubPaths = @("/madler/zlib/archive/$githubCommit.tar.gz", "/madler/zlib/tar.gz/$githubCommit")
Assert-AssetCase @('failure', 'valid') @('github.com', 'codeload.github.com') $true `
    -Url $githubArchive -Paths $githubPaths
Assert-AssetCase @('invalid', 'valid') @('github.com', 'codeload.github.com') $true `
    -Url $githubArchive -Paths $githubPaths
Assert-AssetCase @('failure', 'failure') @('github.com', 'codeload.github.com') $false `
    -Url $githubArchive -Paths $githubPaths
Assert-AssetCase @('failure') @('github.com') $false `
    -Url 'https://github.com/madler/zlib/archive/v1.3.2.tar.gz' `
    -Paths @('/madler/zlib/archive/v1.3.2.tar.gz')
Assert-AssetCase @('failure') @('github.com') $false `
    -Url "${githubArchive}?download=1" -Paths @("/madler/zlib/archive/$githubCommit.tar.gz")
Assert-AssetCase @('failure') @('github.com') $false `
    -Url "https://github.com:8443/madler/zlib/archive/$githubCommit.tar.gz" `
    -Paths @("/madler/zlib/archive/$githubCommit.tar.gz")
Assert-AssetCase @('failure') @('github.com') $false `
    -Url "https://user@github.com/madler/zlib/archive/$githubCommit.tar.gz" `
    -Paths @("/madler/zlib/archive/$githubCommit.tar.gz")
Write-Host 'Native asset provider: 14 deterministic transport, integrity, path, and destination-preservation cases passed.'
