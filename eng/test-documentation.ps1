param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root "out/docs-smoke/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$cli = Join-Path $root "src/managed/OpenDot.Sol.Cli/bin/$Configuration/net10.0-windows/OpenDot.Sol.Cli.dll"
$files = @(Get-Item (Join-Path $root 'README.md'), (Join-Path $root 'CONTRIBUTING.md')) + @(Get-ChildItem (Join-Path $root 'docs') -Filter '*.md') + @(Get-ChildItem (Join-Path $root 'spec') -Filter '*.md' -Recurse)
$count = 0
foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\[[^\]]+\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(https?://|mailto:|#)') { continue }
        $path = Join-Path $file.DirectoryName ($target -split '#')[0]
        if (-not (Test-Path -LiteralPath $path)) { throw "Broken link in $($file.Name): $target" }
    }
    foreach ($match in [regex]::Matches($text, '(?ms)^```sol\r?\n(.*?)^```')) {
        $count++
        $source = Join-Path $scratch "sample-$count.sol"
        $output = Join-Path $scratch "sample-$count.cst.json"
        [IO.File]::WriteAllText($source, $match.Groups[1].Value, [Text.UTF8Encoding]::new($false))
        & dotnet $cli $source --emit=cst -o $output
        if ($LASTEXITCODE -ne 0) { throw "Sol example in $($file.Name) failed." }
        $tree = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
        if ($tree.root.kind -ne 'CompilationUnit') { throw 'Invalid sample output.' }
    }
}
Write-Host "Documentation links and $count Sol examples verified."
