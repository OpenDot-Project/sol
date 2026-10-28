# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Save-PinnedNativeNotice {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Notice,
        [Parameter(Mandatory)][string]$Destination,
        [scriptblock]$Transport
    )

    if ($Notice.name -isnot [string] -or $Notice.name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*\z' -or
        $Notice.sha256 -isnot [string] -or $Notice.sha256 -cnotmatch '\A[a-fA-F0-9]{64}\z' -or
        $Notice.sources -isnot [array] -or $Notice.sources.Count -eq 0 -or $Notice.sources.Count -gt 4) {
        throw 'Malformed pinned native notice entry.'
    }
    if (Test-Path -LiteralPath $Destination) { throw 'Refusing to overwrite a cached native notice.' }

    $sources = @()
    foreach ($source in $Notice.sources) {
        $uri = $null
        if ($source.url -isnot [string] -or -not [uri]::TryCreate($source.url, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -cne 'https' -or $uri.IsDefaultPort -eq $false -or $uri.UserInfo -or $uri.Fragment -or
            $uri.Host -cnotin @('llvm.googlesource.com', 'raw.githubusercontent.com') -or
            $source.encoding -cnotin @('identity', 'base64')) {
            throw 'Invalid native notice source policy.'
        }
        $sources += @{ Uri = $uri; Encoding = [string]$source.encoding }
    }

    $failures = [Collections.Generic.List[string]]::new()
    foreach ($source in $sources) {
        $temporary = "$Destination.$([guid]::NewGuid().ToString('N')).partial"
        try {
            if ($Transport) { & $Transport $source.Uri $temporary }
            else { Invoke-WebRequest -Uri $source.Uri -OutFile $temporary -TimeoutSec 30 }

            if ($source.Encoding -ceq 'base64') {
                $encoded = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($temporary)).Trim()
                [IO.File]::WriteAllBytes($temporary, [Convert]::FromBase64String($encoded))
            }
            $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
            if ($actual -ine $Notice.sha256) { throw 'Pinned notice SHA-256 mismatch.' }
            [IO.File]::Move($temporary, $Destination)
            return
        } catch {
            $failures.Add("$($source.Uri.Host):$($_.Exception.GetType().Name)")
            Write-Warning "Native notice source $($source.Uri.Host) failed; trying the next pinned source."
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
    }
    throw "No source supplied pinned native notice '$($Notice.name)' ($($failures -join ', '))."
}

Export-ModuleMember -Function Save-PinnedNativeNotice
