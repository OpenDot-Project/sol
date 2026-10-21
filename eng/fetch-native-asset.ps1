param([Parameter(Mandatory)][uri]$Url, [Parameter(Mandatory)][string]$Sha512, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Url.Scheme -ne 'https' -or $Sha512 -notmatch '^[0-9a-fA-F]{128}$') { throw 'A pinned HTTPS asset is required.' }
Invoke-WebRequest -Uri $Url -OutFile $Destination
if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA512).Hash -ne $Sha512) { throw 'Native dependency asset digest mismatch.' }
