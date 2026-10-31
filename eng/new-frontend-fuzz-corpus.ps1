# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param([string]$OutputDirectory = 'fuzz/corpus/frontend', [switch]$Check)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$expected = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$scratch = Join-Path $root ('out/corpus-generation-' + [guid]::NewGuid().ToString('N'))
$output = if ($Check) { $scratch } else { $expected }
if (Test-Path $output) { throw 'Corpus output must not already exist. Use -Check to verify regeneration.' }
$utf8 = [Text.UTF8Encoding]::new($false)
$families = @('arithmetic', 'control-flow', 'declarations', 'generic-types', 'lambdas', 'arguments',
    'modules', 'properties', 'numeric-boundaries', 'string-escapes', 'trivia', 'truncation',
    'token-mutation', 'delimiter-mutation', 'unterminated', 'invalid-utf8', 'arbitrary-bytes',
    'nesting', 'operators', 'unicode-newlines')
function Get-CaseBytes([int]$Index, [string]$Family, [int]$Attempt) {
    $digest = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes("sol-frontend-v1/$Index/$Attempt"))
    $a = [int]([BitConverter]::ToUInt32($digest, 0) % 100003)
    $b = 1 + [int]([BitConverter]::ToUInt32($digest, 4) % 997)
    $c = [int]([BitConverter]::ToUInt32($digest, 8) % 13)
    $prefix = "module corpus`n"
    $expression = @("$a + $b * $c", "($a - $b) / $b", "-$a + ($b % $b)",
        "$a shl $c", "$a < $b", "$a == $b", "$a & $b | $c", "$a + $b - $c")[$c % 8]
    if ($Family -eq 'invalid-utf8') {
        $bytes = $utf8.GetBytes($prefix + "val value = $a`n") + [byte[]](0xFF, 0xC0, 0xAF, 0xED, 0xA0, 0x80)
        return ,([byte[]]$bytes)
    }
    if ($Family -eq 'arbitrary-bytes') {
        if ($Index -eq 16000) { return ,([byte[]]::new(0)) }
        $length = 1 + ($Index % 1024)
        $bytes = [byte[]]::new($length)
        for ($offset = 0; $offset -lt $length; $offset += 32) {
            $block = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes("sol-bytes-v1/$Index/$Attempt/$offset"))
            [Array]::Copy($block, 0, $bytes, $offset, [Math]::Min(32, $length - $offset))
        }
        return ,$bytes
    }
    $text = switch ($Family) {
        'arithmetic' { $prefix + "func value(a, b: int) = ($a + a) * ($b - b) + $c`n" }
        'control-flow' { $prefix + "func choose(x: int): int { if (x > $a) { $b } else { $c } }`n" }
        'declarations' { $prefix + "partial object Holder$a { val value: int = $b`nfunc read() = value + $c }`n" }
        'generic-types' { $prefix + "func take(x: Map<int, Vector<([]String)?>>): () { val v: []String? = [$a, $b] }`n" }
        'lambdas' { $prefix + "val named = { x, y -> x + y + $a }`nval positional = { `$0 * $b + `$1 }`n" }
        'arguments' { $prefix + "func call(a: int = $a, b: int = $b) = a + b`nval answer = call(b = $c)`n" }
        'modules' { "module corpus.part$a`nimport std.stdio`nfunc value() = $b + $c`n" }
        'properties' { $prefix + "class Item$a(val name: String) { val fullName: String get = this.name`nvar count: int = $b }`n" }
        'numeric-boundaries' {
            $number = @('0x', '0b102', '0o8', '1__0', '1e+', '18446744073709551616', '0xFFFF_FFFF', '1.0e-9999')[$c % 8]
            $prefix + "val first = $number`nval next = $a + $b`n"
        }
        'string-escapes' {
            $literal = @('"\u{1F600}"', '"\q"', "'ab'", "'\u{D800}'", '"\0\n\t"', '"\u{}"')[$c % 6]
            $prefix + "val first = $literal`nval next = $a + $b`n"
        }
        'trivia' {
            $spaces = ' ' * (1 + $c)
            $prefix + "/* outer /* nested */ done */`nval${spaces}value = $a /* gap */ + $b // tail`r`n"
        }
        'truncation' {
            $complete = $prefix + "func truncated(x: int = $a): int { val items = [$b, $c]; items[0] + x }"
            $complete.Substring(0, 1 + ($a % ($complete.Length - 1)))
        }
        'token-mutation' { $prefix + "func value$a(x @@ int) = x $(@('=>', '???', '##', ':=')[$c % 4]) $b`n" }
        'delimiter-mutation' { $prefix + "func value$a(x: int] { val result = [$b, ($c }; result )`n" }
        'unterminated' { $prefix + "val value = $a + $b`n" + @('"unfinished', '/* unfinished', "'unfinished", '"\u{')[$c % 4] }
        'nesting' { $depth = 1 + ($Index % 300); $prefix + 'val value = ' + ('(' * $depth) + "$a + $b" + (')' * $depth) + "`n" }
        'operators' { $prefix + "func operation(x: int) { x <<= $c`nx += $a`nx = $expression`nx!!?.member($b) }`n" }
        'unicode-newlines' { "module corpus`r`nval value$a = `"Türkçe 日本 😀 $b`"`rval next = $c`n" }
        default { throw 'Unknown corpus family.' }
    }
    return ,($utf8.GetBytes($text))
}
try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $shards = @()
    for ($shard = 0; $shard -lt 40; $shard++) {
        $family = $families[[int][Math]::Floor($shard / 2)]
        $name = 'shard-{0:D2}.jsonl' -f $shard
        $file = Join-Path $output $name
        $writer = [IO.StreamWriter]::new($file, $false, $utf8)
        $writer.NewLine = "`n"
        try {
            for ($slot = 0; $slot -lt 500; $slot++) {
                $index = $shard * 500 + $slot
                $attempt = 0
                do {
                    if ($attempt -ge 1000) { throw "Cannot produce a unique source for $index" }
                    $bytes = Get-CaseBytes $index $family $attempt
                    $attempt++
                    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                } while (-not $seen.Add($hash))
                $expectation = if ($family -in @('arithmetic', 'arguments', 'modules')) { 'parse' }
                    elseif ($family -eq 'invalid-utf8') { 'reject' } else { 'any' }
                $record = [ordered]@{ id = ('case-{0:D5}' -f $index); family = $family; expectation = $expectation
                    sha256 = $hash; source_base64 = [Convert]::ToBase64String($bytes) }
                $writer.WriteLine(($record | ConvertTo-Json -Compress))
            }
        } finally { $writer.Dispose() }
        $shards += [ordered]@{ file = $name; count = 500; family = $family
            sha256 = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $generator = [IO.File]::ReadAllText($PSCommandPath).Replace("`r`n", "`n")
    $manifest = [ordered]@{ schema_version = 1; target = 'frontend-utf8-v1'; count = 20000; max_input_bytes = 8192
        generator_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($generator))).ToLowerInvariant()
        families = $families; shards = $shards }
    [IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8 -Compress) + "`n", $utf8)
    if ($Check) {
        $actualNames = @(Get-ChildItem $expected -File | Sort-Object Name | ForEach-Object Name)
        $newNames = @(Get-ChildItem $output -File | Sort-Object Name | ForEach-Object Name)
        if (($actualNames -join ',') -cne ($newNames -join ',')) { throw 'Corpus file inventory differs from regeneration.' }
        foreach ($name in $newNames) {
            if ((Get-FileHash (Join-Path $expected $name)).Hash -cne (Get-FileHash (Join-Path $output $name)).Hash) {
                throw "Corpus regeneration differs: $name"
            }
        }
    }
    Write-Host 'Frontend corpus: 20000 unique source byte sequences, 20 families, 40 shards.'
} finally {
    if ($Check -and (Test-Path $scratch)) {
        $resolved = [IO.Path]::GetFullPath($scratch)
        $boundary = [IO.Path]::GetFullPath((Join-Path $root 'out')) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected generation cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
