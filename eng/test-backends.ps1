# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([string]$BackendDirectory = 'out/native/backend')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$backend = [IO.Path]::GetFullPath((Join-Path $root $BackendDirectory))
$env:SOLC_NATIVE_ROOT = $backend
$scratch = Join-Path $root "out/native-tests/backends-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $scratch -Force | Out-Null
$cli = Join-Path $root 'src/managed/OpenDot.Sol.Cli/bin/Release/net10.0-windows/OpenDot.Sol.Cli.dll'
$dotnet = (Get-Command dotnet).Source

function InvokeChecked([string]$File, [string[]]$Arguments, [int]$Expected = 0) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "Backend test timeout: $File" }
        $out = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $Expected) { throw "Backend failure $($process.ExitCode): $File`n$errorText`n$out" }
        return @{ Out = $out; Error = $errorText }
    } finally { $process.Dispose() }
}

$program = Join-Path $scratch 'Main.sol'
$source = @'
module backend.tests
import std.stdio
import std.math
object Main {
    func main(args: []String) throws IOException {
        val value = args[0] + "🙂"
        val strings = [value.substring(0, 1), value + "!"]
        val numbers = [20, 22, 42]
        Gc.collect()
        println(value.length, ":", strings[0], ":", strings[1], ":", numbers[1])
        println(value.indexOf("🙂", 0), ":", value.compareOrdinal(value), ":", value == value)
        println(abs(-7), ":", min(20, 22), ":", max(20, 22))
        val bytes: []byte = [0, 255]
        val signed: []sbyte = [-128, 127]
        val shorts: []short = [-32768, 32767]
        val unsigned: []uint = [0, 4294967295]
        val longs: []long = [-9223372036854775808, 9223372036854775807]
        val ulongs: []ulong = [0, 18446744073709551615]
        val flags = [true, false]
        val singles: []single = [1.5, 2.5]
        val doubles: []double = [1.5, 2.5]
        val empty: []int = []
        Gc.collect()
        println(bytes[1], ":", signed[0], ":", shorts[0], ":", unsigned[1], ":", longs[0], ":", ulongs[1])
        println(flags[0], ":", flags[1], ":", singles[1], ":", doubles[1], ":", empty.length)
        println(value.substring(length = mark(1), start = mark(0)))
        var counter = 0
        for counter < 100 {
            val temporary = value + "!"
            Gc.collect()
            if temporary.length != 4 { return }
            counter += 1
        }
        println(counter)
    }
}
func mark(value: int): int throws IOException { print(value); value }
'@
[IO.File]::WriteAllText($program, $source, [Text.UTF8Encoding]::new($false))
$expected = "3:İ:İa🙂!:22`n2:0:true`n7:20:22`n255:-128:-32768:4294967295:-9223372036854775808:18446744073709551615`ntrue:false:2.5:2.5:0`n10İ`n100`n"
$count = 0
foreach ($static in @($false, $true)) {
    $flags = if ($static) { @('--static-solrt-lib') } else { @() }
    $directory = Join-Path $scratch "bin-$static"
    New-Item -ItemType Directory $directory -Force | Out-Null
    $exe = Join-Path $directory 'Main.exe'
    $null = InvokeChecked $dotnet (@($cli, $program, '-o', $exe) + $flags)
    $aot = InvokeChecked $exe @('İa')
    $orc = InvokeChecked $dotnet (@($cli, $program, '-Cllvm-compiler=orc') + $flags + @('--', 'İa'))
    if ($aot.Out -cne $expected -or $orc.Out -cne $aot.Out -or $aot.Error -or $orc.Error) { throw "AOT/ORC String/array/GC parity failed: $($aot.Out) / $($orc.Out) / $($orc.Error)" }
    $count += 2
    Write-Host "AOT/ORC collectible String/array parity passed (static runtime: $static)."
}

$library = Join-Path $scratch 'Library.sol'
[IO.File]::WriteAllText($library, 'module library.math; func add(a, b: int): int = a + b', [Text.UTF8Encoding]::new($false))
$consumer = Join-Path $scratch 'Consumer.c'
[IO.File]::WriteAllText($consumer, 'extern int add(int, int); extern void sol_library_shutdown(void); int main(void) { int result = add(20, 22); sol_library_shutdown(); return result == 42 ? 0 : 99; }')
$object = Join-Path $scratch 'Consumer.obj'
$null = InvokeChecked (Join-Path $env:LLVM_ROOT 'bin/clang-cl.exe') @('/nologo', '/c', '/MT', '/TC', $consumer, "/Fo$object")
$paths = Get-Content (Join-Path $backend 'link-paths.txt') | ForEach-Object { "/libpath:$_" }
foreach ($kind in @('cdylib', 'staticlib', 'solib')) {
    foreach ($static in @($false, $true)) {
        $directory = Join-Path $scratch "$kind-$static"
        New-Item -ItemType Directory $directory -Force | Out-Null
        $extension = switch ($kind) { 'cdylib' { '.dll' }; 'staticlib' { '.lib' }; 'solib' { '.solib' } }
        $output = Join-Path $directory "Library$extension"
        $flags = if ($static) { @('--static-solrt-lib') } else { @() }
        $null = InvokeChecked $dotnet (@($cli, $library, "--artifact-type=$kind", '-o', $output) + $flags)
        if ($kind -eq 'solib') {
            $repeat = Join-Path $directory 'Repeat.solib'
            $null = InvokeChecked $dotnet (@($cli, $library, "--artifact-type=$kind", '-o', $repeat) + $flags)
            if ((Get-FileHash $output).Hash -ne (Get-FileHash $repeat).Hash) { throw 'Sol library output is not deterministic.' }
        } else {
            $exe = Join-Path $directory 'Consumer.exe'
            $link = @('/nologo', '/machine:x64', '/subsystem:console', "/out:$exe", $object, (Join-Path $directory 'Library.lib'))
            if ($kind -eq 'staticlib' -and -not $static) { $link += Join-Path $directory 'SolRT.lib' }
            $link += @('libcmt.lib', 'libcpmt.lib', 'libvcruntime.lib', 'libucrt.lib', 'oldnames.lib', 'kernel32.lib')
            # This is an independent C/C++ consumer toolchain, not Solc's embedded linker.
            $null = InvokeChecked (Join-Path $env:LLVM_ROOT 'bin/lld-link.exe') ($link + $paths)
            $null = InvokeChecked $exe @()
        }
        $count++
        Write-Host "Library consumer/container passed: $kind (static runtime: $static)."
    }
}

foreach ($file in @('sol-exec-host.exe', 'sol-exec-host-static.exe', 'OpenDot.Sol.LlvmBridge.dll', 'OpenDot.Sol.LlvmNative.dll')) {
    $imports = InvokeChecked (Join-Path $env:LLVM_ROOT 'bin/llvm-readobj.exe') @('--coff-imports', (Join-Path $backend $file))
    if (($file -like 'sol-exec-host*' -or $file -eq 'OpenDot.Sol.LlvmNative.dll') -and $imports.Out -match '(?i)mscoree|hostfxr|coreclr|ijwhost') { throw 'Guest ORC host imports the managed runtime.' }
    if ($file -like 'OpenDot.Sol.Llvm*' -and $imports.Out -match '(?i)Name: SolRT.dll') { throw 'Host compiler bridge imports the guest runtime.' }
    [IO.File]::WriteAllText((Join-Path $scratch "$file.imports.txt"), $imports.Out)
}
Write-Host "Backend checks passed: $count AOT/ORC and library scenarios; host/guest runtime import boundaries verified."
