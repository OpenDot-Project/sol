# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$tracked = & git -C $root ls-files
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect tracked files.' }
foreach ($path in $tracked) {
    if ($path -match '(^|/)\.codex(/|$)' -or $path -match '(^|/)\.dotnet(/|$)') {
        throw "Private/local runtime path is tracked: $path"
    }
}
$ignored = & git -C $root check-ignore --no-index .codex/AGENTS.md
if ($LASTEXITCODE -ne 0 -or -not $ignored) { throw 'Private notes must be ignored.' }
Write-Host 'Repository privacy boundary verified.'
