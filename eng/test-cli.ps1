param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $root "src/managed/OpenDot.Sol.Cli/bin/$Configuration/net10.0-windows/OpenDot.Sol.Cli.dll"
$output = Join-Path $root "out/cli-smoke/$Configuration"
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet $cli --version
if ($LASTEXITCODE -ne 0) { throw 'Version smoke failed.' }
& dotnet $cli (Join-Path $root 'tests/fixtures/main.sol') --emit=cst -o (Join-Path $output 'main.cst.json')
if ($LASTEXITCODE -ne 0) { throw 'CST smoke failed.' }
$tree = Get-Content -LiteralPath (Join-Path $output 'main.cst.json') -Raw | ConvertFrom-Json
if ($tree.module -ne 'arbitrary.entry' -or $tree.root.kind -ne 'CompilationUnit') { throw 'CST content mismatch.' }
& dotnet $cli (Join-Path $root 'tests/fixtures/control-flow.sol') --emit=cst -o (Join-Path $output 'control-flow.cst.json')
if ($LASTEXITCODE -ne 0) { throw 'Control-flow CST smoke failed.' }
$control = Get-Content -LiteralPath (Join-Path $output 'control-flow.cst.json') -Raw | ConvertFrom-Json
if ($control.module -ne 'fixtures.control' -or $control.root.kind -ne 'CompilationUnit') { throw 'Control-flow CST content mismatch.' }
& dotnet $cli run 2>&1 | Out-Host
if ($LASTEXITCODE -ne 2) { throw 'Compiler subcommand was not rejected.' }
$invalid = Join-Path $output 'invalid.sol'
Set-Content -LiteralPath $invalid -Value 'func missingModule() {}' -Encoding utf8NoBOM
& dotnet $cli $invalid --emit=cst -o (Join-Path $output 'invalid.cst.json') 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1 -or (Test-Path (Join-Path $output 'invalid.cst.json'))) { throw 'Invalid source produced output.' }
# Expected negative cases must not become the PowerShell step's final native exit code.
$global:LASTEXITCODE = 0
Write-Host 'CLI process smoke tests passed.'
