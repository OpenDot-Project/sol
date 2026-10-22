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
    param([string[]]$Responses, [string[]]$Hosts, [bool]$Succeeds, [string]$Url = 'https://mirror.msys2.org/msys/x86_64/sample.pkg.tar.zst', [string]$Hash = $digest)
    $state.Responses = $Responses
    $state.Attempts.Clear()
    [IO.File]::WriteAllText($destination, 'previous destination')
    $failureMessage = 'No exception was raised.'
    $failed = $false
    try { & $provider -Url $Url -Sha512 $Hash -Destination $destination -WarningAction SilentlyContinue }
    catch { $failed = $true; $failureMessage = $_.Exception.Message }
    if ($failed -eq $Succeeds) { throw "Asset provider success/failure mismatch for $Url ($($Responses -join ',')): $failureMessage" }
    if ((($state.Attempts | ForEach-Object Host) -join ',') -ne ($Hosts -join ',')) { throw 'Asset provider mirror order mismatch.' }
    if ($Succeeds) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA512).Hash -ne $digest) { throw 'Unverified destination.' }
    } elseif ([IO.File]::ReadAllText($destination) -ne 'previous destination') { throw 'Failed download replaced the destination.' }
    if (@(Get-ChildItem -LiteralPath $directory -Filter '*.partial').Count -ne 0) { throw 'Partial download leaked.' }
    foreach ($attempt in $state.Attempts) {
        if ($attempt.AbsolutePath -notmatch '/msys/x86_64/sample.pkg.tar.zst$') { throw 'Mirror changed the asset path.' }
    }
}

Assert-AssetCase @('valid') @('mirror.msys2.org') $true
Assert-AssetCase @('failure', 'valid') @('mirror.msys2.org', 'repo.msys2.org') $true
Assert-AssetCase @('invalid', 'failure', 'valid') @('mirror.msys2.org', 'repo.msys2.org', 'mirror.selfnet.de') $true
Assert-AssetCase @('invalid', 'failure', 'invalid') @('mirror.msys2.org', 'repo.msys2.org', 'mirror.selfnet.de') $false
Assert-AssetCase @('failure') @('example.invalid') $false -Url 'https://example.invalid/msys/x86_64/sample.pkg.tar.zst'
Assert-AssetCase @() @() $false -Url 'http://mirror.msys2.org/msys/x86_64/sample.pkg.tar.zst'
Assert-AssetCase @() @() $false -Hash 'not-a-sha512'
Write-Host 'Native asset provider: 7 deterministic transport, integrity, and destination-preservation cases passed.'
