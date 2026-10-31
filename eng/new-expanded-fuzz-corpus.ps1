# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param([string]$OutputDirectory = 'fuzz/corpus', [switch]$Check)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$expected = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$scratch = Join-Path $root ('out/expanded-corpus-' + [guid]::NewGuid().ToString('N'))
$output = if ($Check) { $scratch } else { $expected }
$utf8 = [Text.UTF8Encoding]::new($false)
$targets = [ordered]@{
    semantic = @('binding-scopes','default-arguments','named-arguments','varargs','ref-out','nullables',
        'generic-types','partial-types','module-cycles','import-aliases','duplicate-symbols','overloads',
        'inheritance','lambda-shapes','exception-types','properties','destructuring','enum-patterns',
        'malformed-declarations','missing-imports')
    runtime = @('ascii-short','ascii-long','unicode-bmp','unicode-astral','embedded-nul','empty-strings',
        'concat-left-empty','concat-right-empty','substring-empty','substring-whole','substring-middle',
        'search-present','search-missing','hash-equality','array-zero','array-single','array-many',
        'exception-cause','exception-suppressed','exception-carrier')
    backend = @('add','subtract','multiply','signed-divide','unsigned-divide','signed-remainder',
        'unsigned-remainder','bit-and','bit-or','bit-xor','shift-left','shift-logical','shift-arithmetic',
        'stack-load-store','branch-phi','function-call','integer-casts','floating-compare','foreign-handle',
        'terminated-block')
    gc = @('chain-first','chain-last','cycle-single','cycle-none','tree-root','tree-leaves','random-sparse',
        'random-dense','self-cycles','disconnected','multi-roots','root-drop','strong-handle','weak-handles',
        'two-clusters','large-graph','small-graph','diamond','cross-links','root-rotation')
}
function SemanticBytes([int]$Mode, [int]$A, [int]$B) {
    $declaration = switch ($Mode) {
        0 { "func f(x: int) { val y = x + $A; { z -> y + z + $B } }" }
        1 { "func f(a: int = $A, b: int = a + $B) = a + b" }
        2 { "func f(a: int, b: int) = a + b`nval result = f(b = $B, a = $A)" }
        3 { "func f(vararg args: int, last: int = $A) = last + $B" }
        4 { "func f(ref x: int, out y: int) { y = x + $A; x = $B }" }
        5 { "class C$A(val value: String?) { func f() = value?.length ?: $B }" }
        6 { "class Box$A<T> { val item: Vector<[]T?>? = null }" }
        7 { "partial class Shared$A { func one() = $B }" }
        8 { "import support`nclass C$A {}" }
        9 { "import alias = support : Renamed = Item`nval number = $A + $B" }
        10 { "class C$A {}`nclass C$A {}" }
        11 { "func f(x: int) = x + $A`nfunc f(x: long) = x + $B" }
        12 { "open class Base$A {}`nclass Child$A : Base$A() {}" }
        13 { "val f$A = { x -> `$0 + $B }" }
        14 { "class E$A : Exception() {}`nfunc f(): () throws E$A { throw E$A() }" }
        15 { "class C$A(val name: String) { val fullName: String get = this.name; var count = $B }" }
        16 { "func f() { val (x, y) = ($A, $B); x + y }" }
        17 { "enum Choice$A { case First; case Second(int) }" }
        18 { "class C$A { func broken(x: ) { val y = $B + } }" }
        19 { "import absent$A`nclass C$B {}" }
    }
    $extra = if ($Mode -eq 7) { "module app`npartial class Shared$A { func two() = $B }" }
        elseif ($Mode -eq 8) { "module support`nimport app`nclass Item$A {}" }
        else { "module support`nclass Item { func number() = $A + $B }" }
    $payload = [ordered]@{ files = @(
        [ordered]@{ path = 'Main.sol'; source = "module app`n$declaration`n" },
        [ordered]@{ path = 'Support.sol'; source = "$extra`n" }) }
    return ,($utf8.GetBytes(($payload | ConvertTo-Json -Depth 6 -Compress)))
}
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $generator = [IO.File]::ReadAllText($PSCommandPath).Replace("`r`n", "`n")
    $generatorHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($generator))).ToLowerInvariant()
    $targetCode = 0
    foreach ($target in $targets.Keys) {
        $directory = Join-Path $output $target
        if (Test-Path $directory) { throw "Target output already exists: $target" }
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $shards = @()
        for ($shard = 0; $shard -lt 40; $shard++) {
            $mode = [int][Math]::Floor($shard / 2)
            $family = $targets[$target][$mode]
            $name = 'shard-{0:D2}.jsonl' -f $shard
            $file = Join-Path $directory $name
            $writer = [IO.StreamWriter]::new($file, $false, $utf8)
            $writer.NewLine = "`n"
            try {
                for ($slot = 0; $slot -lt 500; $slot++) {
                    $index = $shard * 500 + $slot
                    $attempt = 0
                    do {
                        if ($attempt -gt 1000) { throw 'Corpus uniqueness budget exhausted.' }
                        $digest = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes("sol-expanded-v1/$target/$index/$attempt"))
                        $attempt++
                        if ($target -eq 'semantic') {
                            $bytes = SemanticBytes $mode ([int]([BitConverter]::ToUInt32($digest, 0) % 1000003)) `
                                ([int]([BitConverter]::ToUInt32($digest, 4) % 997))
                        } else {
                            $bytes = [byte[]]::new(64)
                            [Array]::Copy([byte[]](0x53,0x46,0x5A,0x31,$targetCode,$mode,1,0), $bytes, 8)
                            [Array]::Copy($digest, 0, $bytes, 8, 32)
                            $tail = [Security.Cryptography.SHA256]::HashData($digest)
                            [Array]::Copy($tail, 0, $bytes, 40, 24)
                        }
                        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                    } while (-not $seen.Add($hash))
                    [ordered]@{ id = ('case-{0:D5}' -f $index); family = $family; expectation = 'any'
                        sha256 = $hash; source_base64 = [Convert]::ToBase64String($bytes) } | ConvertTo-Json -Compress |
                        ForEach-Object { $writer.WriteLine($_) }
                }
            } finally { $writer.Dispose() }
            $shards += [ordered]@{ file = $name; count = 500; family = $family
                sha256 = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
        $targetName = if ($target -eq 'semantic') { 'semantic-modules-v1' } else { "$target-trace-v1" }
        $manifest = [ordered]@{ schema_version = 1; target = $targetName; count = 20000; max_input_bytes = 8192
            generator_sha256 = $generatorHash; families = $targets[$target]; shards = $shards }
        [IO.File]::WriteAllText((Join-Path $directory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8 -Compress) + "`n", $utf8)
        if ($Check) {
            $old = Join-Path $expected $target
            $files = @(Get-ChildItem $directory -File | Sort-Object Name)
            $existing = @(Get-ChildItem $old -File | Sort-Object Name)
            if (($files.Name -join ',') -cne ($existing.Name -join ',')) { throw 'Expanded corpus file inventory differs.' }
            foreach ($item in $files) {
                if ((Get-FileHash $item.FullName).Hash -cne (Get-FileHash (Join-Path $old $item.Name)).Hash) {
                    throw "Expanded corpus regeneration differs: $target/$($item.Name)"
                }
            }
        }
        Write-Host "$target corpus: 20000 unique inputs, 20 families, 40 shards."
        $targetCode++
    }
} finally {
    if ($Check -and (Test-Path $scratch)) {
        $resolved = [IO.Path]::GetFullPath($scratch)
        $boundary = [IO.Path]::GetFullPath((Join-Path $root 'out')) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected corpus cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
