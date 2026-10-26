# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
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
# Retry immutable GitHub commit archives through GitHub's dedicated archive host.
# The digest is still checked against the original vcpkg pin before installation.
if ($Url.Host -eq 'github.com' -and $Url.IsDefaultPort -and -not $Url.UserInfo -and
    -not $Url.Query -and -not $Url.Fragment -and
    $Url.AbsolutePath -cmatch '^/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/archive/(?<commit>[a-f0-9]{40})\.tar\.gz$') {
    $owner = $Matches.owner
    $repository = $Matches.repo
    $commit = $Matches.commit
    $candidates += [uri]"https://codeload.github.com/$owner/$repository/tar.gz/$commit"
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
