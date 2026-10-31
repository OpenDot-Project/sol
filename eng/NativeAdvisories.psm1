# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:ExpectedUpstreamFixes = @(
    @{ advisory_id = 'OSV-2021-777'; commit = '8f5ccada05ddd4a1ff8e399ad39fc7cd4bd33325'
        files = @('xinclude.c'); subject = 'xmlAddChild() and xmlAddNextSibling() may not attach their second argument' },
    @{ advisory_id = 'OSV-2024-698'; commit = 'fcb7a777ce19ff2bef020ed2966a0c96e4f4da4c'
        files = @('HTMLtree.c', 'xmlIO.c'); subject = 'io: Make xmlOutputBufferCreate* not free encoder on error' }
)

function Assert-AdvisoryString($Value, [string]$Pattern, [string]$Name) {
    if ($Value -isnot [string] -or $Value.Length -gt 2048 -or $Value -match '[\x00-\x1f\x7f]' -or
        $Value -cnotmatch $Pattern) {
        throw "Invalid advisory $Name."
    }
}

function Get-AdvisoryRepository([string]$Value) {
    Assert-AdvisoryString $Value '^https://(github\.com|gitlab\.gnome\.org)/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?$' 'repository'
    return $Value.TrimEnd('/').ToLowerInvariant() -replace '\.git$', ''
}

function Assert-NativeAdvisoryCatalog($Catalog) {
    if ($Catalog -isnot [Collections.IDictionary] -or $Catalog.schema_version -isnot [long] -or
        $Catalog.schema_version -ne 1 -or $Catalog.components -isnot [array] -or $Catalog.components.Count -ne 4) {
        throw 'Invalid native advisory catalog.'
    }
    Assert-AdvisoryString $Catalog.vcpkg_commit '^[a-f0-9]{40}$' 'vcpkg commit'
    Assert-AdvisoryString $Catalog.verified_date '^\d{4}-\d{2}-\d{2}$' 'verification date'
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($component in $Catalog.components) {
        if ($component -isnot [Collections.IDictionary] -or $component.name -isnot [string] -or
            $component.name -cnotin @('llvm', 'zlib', 'zstd', 'libxml2') -or -not $names.Add($component.name)) {
            throw 'Unknown or duplicate native advisory component.'
        }
        Assert-AdvisoryString $component.version '^\d+\.\d+\.\d+$' 'version'
        Assert-AdvisoryString $component.commit '^[a-f0-9]{40}$' 'source commit'
        Assert-AdvisoryString $component.tag_object '^[a-f0-9]{40}$' 'tag object'
        if ($component.Contains('tag_commit')) {
            if ($component.name -cne 'zlib' -or $component.commit -ceq $component.tag_commit) {
                throw 'Unexpected upstream fix identity.'
            }
            Assert-AdvisoryString $component.tag_commit '^[a-f0-9]{40}$' 'base tag commit'
            Assert-AdvisoryString $component.source_archive_sha512 '^[a-f0-9]{128}$' 'source archive digest'
        }
        $tag = if ($component.name -eq 'llvm') { 'llvmorg-' + $component.version } else { 'v' + $component.version }
        if ($component.tag -cne $tag) { throw 'Native source version/tag mismatch.' }
        $repository = Get-AdvisoryRepository $component.repository
        if ($component.advisory_repositories -isnot [array] -or
            $component.advisory_repositories.Count -lt 1 -or $component.advisory_repositories.Count -gt 4) {
            throw 'Missing native advisory repository identities.'
        }
        $repositories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($item in $component.advisory_repositories) {
            if (-not $repositories.Add((Get-AdvisoryRepository $item))) { throw 'Duplicate advisory repository.' }
        }
        if (-not $repositories.Contains($repository)) { throw 'Primary source repository is not mapped.' }
        if ($component.name -ceq 'libxml2') {
            if ($component.verified_upstream_fixes -isnot [array] -or
                $component.verified_upstream_fixes.Count -ne $script:ExpectedUpstreamFixes.Count) {
                throw 'libxml2 upstream fix evidence is incomplete.'
            }
            foreach ($expected in $script:ExpectedUpstreamFixes) {
                $fixes = @($component.verified_upstream_fixes | Where-Object advisory_id -CEQ $expected.advisory_id)
                if ($fixes.Count -ne 1) { throw 'Missing or duplicate verified upstream fix.' }
                $fix = $fixes[0]
                if ($fix.commit -cne $expected.commit -or $fix.subject -cne $expected.subject -or
                    $fix.files -isnot [array] -or ($fix.files -join ',') -cne ($expected.files -join ',')) {
                    throw 'libxml2 upstream fix identity or patch scope changed.'
                }
                Assert-AdvisoryString $fix.commit '^[a-f0-9]{40}$' 'verified upstream fix commit'
                Assert-AdvisoryString $fix.subject '^.{8,200}$' 'verified upstream fix subject'
                foreach ($file in $fix.files) {
                    Assert-AdvisoryString $file '^(xinclude|HTMLtree|xmlIO)\.c$' 'verified upstream fix path'
                }
            }
        } elseif ($component.Contains('verified_upstream_fixes')) {
            throw 'Unexpected verified upstream fix evidence.'
        }
    }
}

function Test-NativeAdvisoryFixInPinnedTag($Component, $Fix, [scriptblock]$Request) {
    $expected = @($script:ExpectedUpstreamFixes | Where-Object advisory_id -CEQ $Fix.advisory_id)
    $gitLabSource = @($Component.advisory_repositories | Where-Object {
        (Get-AdvisoryRepository $_) -ceq 'https://gitlab.gnome.org/gnome/libxml2'
    })
    if ($Component.name -cne 'libxml2' -or $Component.repository -cne 'https://github.com/GNOME/libxml2' -or
        $gitLabSource.Count -ne 1 -or $expected.Count -ne 1 -or
        $Fix.commit -cne $expected[0].commit -or $Fix.subject -cne $expected[0].subject -or
        $Fix.files -isnot [array] -or ($Fix.files -join ',') -cne ($expected[0].files -join ',')) {
        throw 'Unrecognized upstream advisory fix evidence.'
    }

    foreach ($file in $expected[0].files) {
        $found = $false
        for ($page = 1; $page -le 10; $page++) {
            $commits = @(& $Request $file $Component.tag $page)
            if ($commits.Count -gt 100) { throw 'Upstream commit history page exceeded its limit.' }
            foreach ($commit in $commits) {
                if ($commit -isnot [Collections.IDictionary]) { throw 'Invalid upstream commit history response.' }
                Assert-AdvisoryString $commit.id '^[a-f0-9]{40}$' 'upstream commit identity'
                if ($commit.id -ceq $Fix.commit) {
                    if ($commit.title -cne $Fix.subject) { throw 'Upstream fix subject does not match its pinned identity.' }
                    $found = $true
                    break
                }
            }
            if ($found -or $commits.Count -lt 100) { break }
        }
        if (-not $found) { return $false }
    }
    return $true
}

function ConvertFrom-NativeAdvisoryPage($Response, $Component) {
    if ($Response -isnot [Collections.IDictionary]) { throw 'OSV response is not an object.' }
    if (@($Response.Keys | Where-Object { $_ -cnotin @('vulns', 'next_page_token') }).Count) {
        throw 'Unknown OSV response envelope.'
    }
    $token = ''
    if ($Response.Contains('next_page_token')) {
        Assert-AdvisoryString $Response.next_page_token '^[^\x00-\x1f\x7f]{0,2048}$' 'page token'
        $token = $Response.next_page_token
    }
    $records = @()
    if ($Response.Contains('vulns')) {
        if ($Response.vulns -isnot [array] -or $Response.vulns.Count -gt 10000) {
            throw 'Invalid OSV vulnerability collection.'
        }
        foreach ($vulnerability in $Response.vulns) {
            if ($vulnerability -isnot [Collections.IDictionary]) { throw 'Invalid OSV vulnerability.' }
            Assert-AdvisoryString $vulnerability.id '^[A-Za-z0-9][A-Za-z0-9_.-]{1,199}$' 'vulnerability identity'
            if ($vulnerability.affected -isnot [array] -or $vulnerability.affected.Count -eq 0) {
                throw 'OSV vulnerability has no affected component evidence.'
            }
            $matched = $false
            foreach ($affected in $vulnerability.affected) {
                if ($affected -isnot [Collections.IDictionary]) { throw 'Invalid OSV affected entry.' }
                if ($affected.Contains('ranges')) {
                    if ($affected.ranges -isnot [array]) { throw 'Invalid OSV ranges.' }
                    foreach ($range in $affected.ranges) {
                        if ($range -isnot [Collections.IDictionary] -or -not $range.Contains('type')) {
                            throw 'Invalid OSV range entry.'
                        }
                        if ($range.type -ceq 'GIT') {
                            $repository = Get-AdvisoryRepository $range.repo
                            if (@($Component.advisory_repositories | Where-Object {
                                (Get-AdvisoryRepository $_) -ceq $repository
                            }).Count) { $matched = $true }
                        }
                    }
                }
            }
            if (-not $matched) { throw 'OSV advisory repository does not match the queried component.' }
            # Preserve full upstream evidence. No severity guess, suppression, or reachability exemption.
            $records += $vulnerability
        }
    }
    return @{ advisories = $records; next_page_token = $token }
}

function Invoke-NativeAdvisoryQuery($Component, [scriptblock]$Request) {
    $tokens = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $records = @()
    $token = ''
    $pages = 0
    do {
        if (++$pages -gt 10) { throw 'OSV pagination limit exceeded.' }
        $query = @{ commit = $Component.commit }
        if ($token) { $query.page_token = $token }
        $page = ConvertFrom-NativeAdvisoryPage (& $Request $query) $Component
        foreach ($item in $page.advisories) {
            if (-not $identities.Add($item.id)) { throw 'Duplicate OSV advisory across pages.' }
            $records += $item
        }
        $token = $page.next_page_token
        if ($token -and -not $tokens.Add($token)) { throw 'Repeated OSV page token.' }
    } while ($token)
    return [ordered]@{
        name = $Component.name
        version = $Component.version
        repository = $Component.repository
        source_commit = $Component.commit
        upstream_tag = $Component.tag
        upstream_tag_commit = $(if ($Component.Contains('tag_commit')) { $Component.tag_commit } else { $Component.commit })
        query = @{ commit = $Component.commit }
        pages = $pages
        status = $(if ($records.Count) { 'advisories-found' } else { 'no-known-advisories' })
        advisories = $records
    }
}

function Resolve-NativeAdvisoryResult($Result, $Catalog, [string]$EvidencePath, [string]$LibraryDirectory, $FixEvidence = @()) {
    $dispositions = @()
    if ($Result.advisories.Count -and $Result.name -ceq 'libxml2') {
        $xmlComponents = @($Catalog.components | Where-Object name -CEQ 'libxml2')
        if ($xmlComponents.Count -ne 1) { throw 'Native advisory catalog has no unique libxml2 identity.' }
        $xmlComponent = $xmlComponents[0]
        $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json -AsHashtable
        $catalogJson = Join-Path $PSScriptRoot 'native-advisories.json'
        foreach ($field in @('catalog_sha256', 'zlib_sha256', 'libxml2_sha256', 'xml_header_sha256')) {
            Assert-AdvisoryString $evidence[$field] '^[a-f0-9]{64}$' "binary $field"
        }
        Assert-AdvisoryString $evidence.zlib_commit '^[a-f0-9]{40}$' 'qualified zlib commit'
        if ($evidence.excluded_features -isnot [array] -or $evidence.absent_symbol_families -isnot [array] -or
            $evidence.gzip_failed_write_regression -isnot [string] -or
            $evidence.xml_parser_and_compression_smoke -isnot [string] -or $evidence.zlib_library -isnot [string]) {
            throw 'Malformed native binary qualification fields.'
        }
        if ($evidence.schema_version -isnot [long] -or $evidence.schema_version -ne 1 -or
            $evidence.catalog_sha256 -cne (Get-FileHash $catalogJson -Algorithm SHA256).Hash.ToLowerInvariant() -or
            $evidence.zlib_commit -cne ($Catalog.components | Where-Object name -CEQ 'zlib').commit -or
            $evidence.gzip_failed_write_regression -cne 'passed' -or
            $evidence.xml_parser_and_compression_smoke -cne 'passed' -or
            ($evidence.excluded_features -join ',') -cne 'HTML,XINCLUDE' -or
            ($evidence.absent_symbol_families -join ',') -cne 'html[A-Z]*,xmlXInclude*' -or
            $evidence.zlib_library -cnotin @('zs.lib', 'libzs.lib', 'zlib.lib', 'zlibstatic.lib', 'z.lib')) {
            throw 'Native binary qualification evidence does not match the build catalog.'
        }
        foreach ($pair in @(@('libxml2.lib', 'libxml2_sha256'), @('xml2s.lib', 'libxml2_sha256'), @($evidence.zlib_library, 'zlib_sha256'), @('zs.lib', 'zlib_sha256'))) {
            $actual = (Get-FileHash (Join-Path $LibraryDirectory $pair[0]) -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -cne $evidence[$pair[1]]) { throw 'Qualified native library bytes changed.' }
        }
        foreach ($record in $Result.advisories) {
            $fix = @($xmlComponent.verified_upstream_fixes | Where-Object advisory_id -CEQ $record.id)
            $proof = @($FixEvidence | Where-Object advisory_id -CEQ $record.id)
            $verified = $false
            if ($fix.Count -eq 1 -and $proof.Count -eq 1 -and $proof[0].status -ceq 'verified' -and
                $proof[0].commit -ceq $fix[0].commit -and $proof[0].source_tag -ceq $xmlComponent.tag -and
                $proof[0].files -is [array] -and ($proof[0].files -join ',') -ceq ($fix[0].files -join ',')) {
                $verified = $true
            }
            $dispositions += @{ id = $record.id
                status = $(if ($verified) { 'fixed-upstream-commit-verified' } else { 'blocking' })
                path_status = $(if ($verified) { 'upstream-fix-in-pinned-tag-and-reported-feature-absent' }
                    elseif ($fix.Count -eq 1 -and $proof.Count -eq 1) { $proof[0].status }
                    else { 'unknown' })
                fix_commit = $(if ($fix.Count -eq 1) { $fix[0].commit } else { '' })
                excluded_feature = $(switch -CaseSensitive ($record.id) {
                    'OSV-2021-777' { 'XINCLUDE' }
                    'OSV-2024-698' { 'HTML' }
                    default { '' }
                }) }
        }
        $Result.upstream_fix_evidence = @($FixEvidence)
        $Result.qualification = @{ evidence_sha256 = (Get-FileHash $EvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
            libxml2_sha256 = $evidence.libxml2_sha256; zlib_sha256 = $evidence.zlib_sha256 }
    } else {
        $dispositions = @($Result.advisories | ForEach-Object { @{ id = $_.id; status = 'blocking'; excluded_feature = '' } })
    }
    $Result.dispositions = $dispositions
    $Result.blocking_advisories = @($dispositions | Where-Object status -CEQ 'blocking').Count
    if ($Result.advisories.Count -and $Result.blocking_advisories -eq 0) {
        $Result.status = 'advisories-present-upstream-fix-verified'
    }
    return $Result
}

Export-ModuleMember -Function Assert-NativeAdvisoryCatalog, ConvertFrom-NativeAdvisoryPage, Invoke-NativeAdvisoryQuery, Test-NativeAdvisoryFixInPinnedTag, Resolve-NativeAdvisoryResult
