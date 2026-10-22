# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([Parameter(Mandatory)][string]$Stage)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$stageRoot = [IO.Path]::GetFullPath($Stage)
$scratch = Join-Path $root "out/package-tests/relocated space İ-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
Copy-Item -LiteralPath $stageRoot -Destination (Join-Path $scratch 'component') -Recurse
$component = Join-Path $scratch 'component'
$launcher = Join-Path $component 'bin/solc.exe'
function Run([string]$File, [string[]]$Arguments, [int]$Code = 0) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $info.WorkingDirectory = $scratch
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorText = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Packaged native smoke timed out.' }
        $stdout = $output.GetAwaiter().GetResult()
        $stderr = $errorText.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $Code) { throw "Packaged native smoke [$($Arguments -join ' ')] exit $($process.ExitCode), expected ${Code}: $stderr" }
        return @{ Out = $stdout; Error = $stderr }
    } finally { $process.Dispose() }
}
$program = Join-Path $scratch 'Main.sol'
@'
module package.smoke
import std.stdio
object Main {
    func main(args: []String) throws IOException {
        val value = args[0] + "🙂"
        val values = [value, "kept"]
        Gc.collect()
        println(values[0], ":", value.length)
    }
}
'@ | Set-Content -LiteralPath $program -Encoding utf8NoBOM
$library = Join-Path $scratch 'Library.sol'
'module package.library; func add(a, b: int): int = a + b' | Set-Content -LiteralPath $library -Encoding utf8NoBOM
$saved = @{}
foreach ($name in @('PATH', 'LIB', 'LLVM_ROOT', 'SOLC_NATIVE_ROOT', 'DOTNET_ROOT', 'DOTNET_ROOT_X64')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:PATH = Join-Path $env:SystemRoot 'System32'
    foreach ($name in @('LIB', 'LLVM_ROOT', 'SOLC_NATIVE_ROOT', 'DOTNET_ROOT', 'DOTNET_ROOT_X64')) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    # Exercise the empty-variable regression as well as an actually absent backend override.
    $env:SOLC_NATIVE_ROOT = ''
    foreach ($static in @($false, $true)) {
        $flags = if ($static) { @('--static-solrt-lib') } else { @() }
        $result = Run $launcher (@($program, '-Cllvm-compiler=orc') + $flags + @('--', 'İa'))
        if ($result.Out -ne "İa🙂:3`n" -or $result.Error) { throw 'Relocated ORC output mismatch.' }
        Remove-Item -LiteralPath Env:SOLC_NATIVE_ROOT -ErrorAction SilentlyContinue
        foreach ($kind in @('staticlib', 'solib')) {
            $extension = if ($kind -eq 'solib') { 'solib' } else { 'lib' }
            $output = Join-Path $scratch "$kind-$static.$extension"
            $null = Run $launcher (@($library, "--artifact-type=$kind", '-o', $output) + $flags)
            if ((Get-Item -LiteralPath $output).Length -eq 0) { throw "Empty packaged $kind artifact." }
        }
    }
    $missing = Join-Path $scratch 'Missing.exe'
    $failure = Run $launcher @($program, '-o', $missing) 1
    if ($failure.Error -notmatch 'x64 Visual Studio developer shell' -or (Test-Path -LiteralPath $missing)) { throw 'Missing SDK must fail without emitting an executable.' }
    # Simulate the caller's developer shell, never store this build-machine configuration in the package.
    $env:LIB = (Get-Content (Join-Path $root 'out/native/backend/link-paths.txt')) -join ';'
    foreach ($static in @($false, $true)) {
        $flags = if ($static) { @('--static-solrt-lib') } else { @() }
        $output = Join-Path $scratch "Main-$static.exe"
        $null = Run $launcher (@($program, '-o', $output) + $flags)
        $result = Run $output @('İa')
        if ($result.Out -ne "İa🙂:3`n" -or $result.Error) { throw 'Relocated AOT output mismatch.' }
        $null = Run $launcher (@($library, '--artifact-type=cdylib', '-o', (Join-Path $scratch "Library-$static.dll")) + $flags)
    }
    Write-Host 'Relocated private-runtime native package: 11 ORC/AOT/library/missing-SDK checks passed.'
} finally {
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
    # Both roots were created by this invocation and stay beneath out/package-tests.
    $expected = [IO.Path]::GetFullPath((Join-Path $root 'out/package-tests')) + [IO.Path]::DirectorySeparatorChar
    if (-not $scratch.StartsWith($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected smoke cleanup root.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
