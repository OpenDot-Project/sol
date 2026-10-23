# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeAdvisories.psm1') -Force
$catalog = Get-Content (Join-Path $PSScriptRoot 'native-advisories.json') -Raw | ConvertFrom-Json -AsHashtable
Assert-NativeAdvisoryCatalog $catalog
$component = $catalog.components | Where-Object name -CEQ 'zlib'
$script:passed = 0
function Expect-Rejection([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Accepted invalid advisory evidence: $Name" }
    $script:passed++
}
function Copy-Data($Value) { return $Value | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable -Depth 64 }
function New-Advisory([string]$Id = 'CVE-2026-12345') {
    return @{ id = $Id; affected = @(@{ ranges = @(@{ type = 'GIT'; repo = 'https://github.com/madler/zlib' }) }) }
}

$mutations = [ordered]@{
    'string catalog version' = { param($v) $v.schema_version = '1' }
    'Boolean catalog version' = { param($v) $v.schema_version = $true }
    'fractional catalog version' = { param($v) $v.schema_version = 1.5 }
    'missing source component' = { param($v) $v.components = @($v.components | Select-Object -First 3) }
    'extra source component' = { param($v) $v.components += $v.components[0] }
    'duplicate source name' = { param($v) $v.components[1].name = 'llvm' }
    'unknown source name' = { param($v) $v.components[1].name = 'other' }
    'array source name' = { param($v) $v.components[1].name = @('zlib') }
    'abbreviated source SHA' = { param($v) $v.components[1].commit = 'abc123' }
    'tag substituted for SHA' = { param($v) $v.components[1].commit = 'v1.3.2' }
    'source version/tag mismatch' = { param($v) $v.components[1].version = '9.9.9' }
    'insecure repository' = { param($v) $v.components[1].repository = 'http://github.com/madler/zlib' }
    'repository credentials' = { param($v) $v.components[1].repository = 'https://user:secret@github.com/madler/zlib' }
    'repository query' = { param($v) $v.components[1].repository = 'https://github.com/madler/zlib?secret=x' }
    'unmapped primary repository' = { param($v) $v.components[1].advisory_repositories = @('https://github.com/other/zlib') }
    'duplicate repository alias' = { param($v) $v.components[1].advisory_repositories += 'https://github.com/madler/zlib.git' }
}
foreach ($case in $mutations.GetEnumerator()) {
    $value = Copy-Data $catalog
    & $case.Value $value
    Expect-Rejection $case.Key { Assert-NativeAdvisoryCatalog $value }
}

$invalidPages = [ordered]@{
    'null response' = $null
    'array response' = @(@{})
    'API error instead of response' = @{ error = 'quota exceeded' }
    'unknown response envelope' = @{ results = @() }
    'null vulnerabilities' = @{ vulns = $null }
    'singleton vulnerability object' = @{ vulns = (New-Advisory) }
    'string vulnerability collection' = @{ vulns = 'CVE-2026-12345' }
    'null vulnerability entry' = @{ vulns = @($null) }
    'array vulnerability identity' = @{ vulns = @(@{ id = @('CVE-2026-12345'); affected = @() }) }
    'missing affected evidence' = @{ vulns = @(@{ id = 'CVE-2026-12345'; affected = @() }) }
    'wrong advisory repository' = @{ vulns = @(@{ id = 'CVE-2026-12345'; affected = @(@{
        ranges = @(@{ type = 'GIT'; repo = 'https://github.com/other/zlib' }) }) }) }
    'wrong ecosystem without commit evidence' = @{ vulns = @(@{ id = 'CVE-2026-12345'; affected = @(@{
        package = @{ name = 'madler/zlib'; ecosystem = 'GitHub Actions' } }) }) }
    'null page token' = @{ next_page_token = $null }
    'array page token' = @{ next_page_token = @('next') }
    'control character page token' = @{ next_page_token = "next`n" }
}
foreach ($case in $invalidPages.GetEnumerator()) {
    $value = $case.Value
    Expect-Rejection $case.Key { ConvertFrom-NativeAdvisoryPage $value $component }
}

$clean = Invoke-NativeAdvisoryQuery $component { param($query)
    if ($query.Count -ne 1 -or $query.commit -cne $component.commit) { throw 'Wrong OSV request identity.' }
    return @{}
}
if ($clean.status -cne 'no-known-advisories' -or $clean.advisories.Count -ne 0 -or $clean.pages -ne 1) {
    throw 'Empty OSV response was not preserved as no known advisories.'
}
$empty = ConvertFrom-NativeAdvisoryPage @{ vulns = @(); next_page_token = '' } $component
if ($empty.advisories.Count -ne 0 -or $empty.next_page_token -cne '') { throw 'Empty final page is invalid.' }

$script:requests = [Collections.Generic.List[object]]::new()
$twoPages = Invoke-NativeAdvisoryQuery $component { param($query)
    $script:requests.Add($query)
    if ($script:requests.Count -eq 1) { return @{ vulns = @((New-Advisory)); next_page_token = 'second' } }
    if ($query.page_token -cne 'second' -or $query.commit -cne $component.commit) { throw 'Pagination changed the source identity.' }
    return @{ vulns = @((New-Advisory 'CVE-2026-23456')) }
}
if ($twoPages.status -cne 'advisories-found' -or $twoPages.pages -ne 2 -or $twoPages.advisories.Count -ne 2) {
    throw 'Paginated findings were lost.'
}
Expect-Rejection 'repeated continuation token' {
    Invoke-NativeAdvisoryQuery $component { param($query) return @{ next_page_token = 'same' } }
}
Expect-Rejection 'duplicate advisory on later page' {
    Invoke-NativeAdvisoryQuery $component { param($query) return @{ vulns = @((New-Advisory)); next_page_token = 'same' } }
}
$script:page = 0
Expect-Rejection 'unbounded continuation' {
    Invoke-NativeAdvisoryQuery $component { param($query)
        $script:page++
        return @{ next_page_token = "page-$script:page" }
    }
}
Expect-Rejection 'network failure' {
    Invoke-NativeAdvisoryQuery $component { param($query) throw 'Simulated network failure.' }
}
$xml = $catalog.components | Where-Object name -CEQ 'libxml2'
$mirror = @{ vulns = @(@{ id = 'OSV-2021-777'; affected = @(@{
    ranges = @(@{ type = 'GIT'; repo = 'https://gitlab.gnome.org/GNOME/libxml2.git' }) }) }) }
$matched = ConvertFrom-NativeAdvisoryPage $mirror $xml
if ($matched.advisories.Count -ne 1) { throw 'Explicit upstream mirror identity was lost.' }
Write-Host "Native advisory validation passed: $script:passed rejection cases, clean response, pagination, exact requests and mirror matching."
