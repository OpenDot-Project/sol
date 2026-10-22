param([Parameter(Mandatory)][uri]$Url, [Parameter(Mandatory)][string]$Sha512, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Url.Scheme -ne 'https' -or $Sha512 -notmatch '^[0-9a-fA-F]{128}$') { throw 'A pinned HTTPS asset is required.' }
$candidates = @($Url)
# The redirecting MSYS2 mirror can return an interactive verification page on CI.
# Only upstream MSYS2 mirrors may substitute the host; the pinned digest never changes.
if ($Url.Host -eq 'mirror.msys2.org') {
    foreach ($mirror in @('repo.msys2.org', 'mirror.selfnet.de')) {
        $prefix = if ($mirror -eq 'mirror.selfnet.de') { '/msys2' } else { '' }
        $candidates += [uri]("https://$mirror$prefix" + $Url.PathAndQuery)
    }
}
$temporary = "$Destination.$([guid]::NewGuid().ToString('N')).partial"
try {
    foreach ($candidate in $candidates) {
        try {
            Invoke-WebRequest -Uri $candidate -OutFile $temporary -TimeoutSec 120
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA512).Hash -ne $Sha512) {
                throw 'Native dependency asset digest mismatch.'
            }
            Move-Item -LiteralPath $temporary -Destination $Destination -Force
            return
        } catch {
            Write-Warning "Native asset attempt failed for $($candidate.Host): $($_.Exception.GetType().Name)"
        }
    }
    throw 'No native dependency source supplied the pinned asset.'
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
