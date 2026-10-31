# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
param([string]$BackendDirectory = 'out/native/backend')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$backend = [IO.Path]::GetFullPath((Join-Path $root $BackendDirectory))
$scratch = Join-Path $root "out/native-tests/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$cli = Join-Path $root 'src/managed/OpenDot.Sol.Cli/bin/Release/net10.0-windows/OpenDot.Sol.Cli.dll'
$env:SOLC_NATIVE_ROOT = $backend

function RunProcess([string]$File, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "Native test timed out: $File" }
        return @{ Code = $process.ExitCode; Out = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

function CompileProgram([string]$Name, [string]$Body, [string]$Declarations, [bool]$Static) {
    $directory = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $source = Join-Path $directory 'Main.sol'
    $exe = Join-Path $directory 'Main.exe'
    $text = "module native.tests`nimport std.stdio`n$Declarations`nobject Main { func main(args: []String) throws IOException { $Body } }"
    [IO.File]::WriteAllText($source, $text, [Text.UTF8Encoding]::new($false))
    $arguments = @($cli, $source, '-o', $exe)
    if ($Static) { $arguments += '--static-solrt-lib' }
    $compile = RunProcess (Get-Command dotnet).Source $arguments
    if ($compile.Code -ne 0) { throw "Compile failed ($Name): $($compile.Error)" }
    return @{ Exe = $exe; Directory = $directory; Source = $source }
}

$cases = @(
    @{ Name = 'greeting'; Body = 'println("Hello, Sol!"); println(add(20, 22)); println(args.length); println(args[0])'; Declarations = 'func add(a, b: int): int = a + b'; Args = @('Alice', '20'); Expected = "Hello, Sol!`n42`n2`nAlice`n" },
    @{ Name = 'loop'; Body = 'var total = 0; for var i: int = 0; i < 10; i += 1 { if i == 2 { continue }; total += i; if total > 20 { break } }; println(total)'; Declarations = ''; Args = @(); Expected = "26`n" },
    @{ Name = 'ref-out'; Body = 'var a = 1; var b = 2; swap(ref a, ref b); var x: int; fill(out x); println(a, ":", b, ":", x)'; Declarations = 'func swap(ref a, b: int) { val saved = a; a = b; b = saved }; func fill(out x: int) { x = 42 }'; Args = @(); Expected = "2:1:42`n" },
    @{ Name = 'short-circuit'; Body = 'println(false && crash()); println(true || crash())'; Declarations = 'func crash(): bool { val zero = 0; val bad = 1 / zero; true }'; Args = @(); Expected = "false`ntrue`n" },
    @{ Name = 'unicode'; Body = 'println("Sol: ", args[0], " \u{1F642}")'; Declarations = ''; Args = @('İstanbul'); Expected = "Sol: İstanbul 🙂`n" },
    @{ Name = 'named-order'; Body = 'println(choose(b = mark(2), a = mark(1)))'; Declarations = 'func mark(value: int): int throws IOException { print(value); value }; func choose(a, b: int): int = a * 10 + b'; Args = @(); Expected = "2112`n" },
    @{ Name = 'function-value'; Body = 'val callback: (int) -> int = increment; println(apply(40, callback))'; Declarations = 'func increment(value: int): int = value + 1; func <T> apply(value: T, callback: (T) -> T): T = callback(value)'; Args = @(); Expected = "41`n" },
    @{ Name = 'function-value-array-index'; Body = 'val callbacks: []((int) -> int) = [increment, twice]; println(callbacks[1](21))'; Declarations = 'func increment(value: int): int = value + 1; func twice(value: int): int = value * 2'; Args = @(); Expected = "42`n" },
    @{ Name = 'function-value-array-loop'; Body = 'val callbacks: []((int) -> int) = [increment, twice]; var total = 0; for val callback in callbacks { total += callback(20) }; println(total)'; Declarations = 'func increment(value: int): int = value + 1; func twice(value: int): int = value * 2'; Args = @(); Expected = "61`n" },
    @{ Name = 'function-value-array-generic'; Body = 'val callbacks: []((String) -> String) = [identity, identity]; println(callbacks[1]("Sol"))'; Declarations = 'func <T> identity(value: T): T = value'; Args = @(); Expected = "Sol`n" },
    @{ Name = 'function-value-array-nested'; Body = 'val callbacks: [][]((int) -> int) = [[increment], [twice]]; println(callbacks[1][0](20))'; Declarations = 'func increment(value: int): int = value + 1; func twice(value: int): int = value * 2'; Args = @(); Expected = "40`n" },
    @{ Name = 'function-value-array-parameter'; Body = 'val callbacks: []((int) -> int) = [increment]; println(applyFirst(callbacks, 41))'; Declarations = 'func increment(value: int): int = value + 1; func applyFirst(callbacks: []((int) -> int), value: int): int = callbacks[0](value)'; Args = @(); Expected = "42`n" },
    @{ Name = 'generic-function-value'; Body = 'val callback: (String) -> String = identity; println(callback("Sol"))'; Declarations = 'func <T> identity(value: T): T = value'; Args = @(); Expected = "Sol`n" },
    @{ Name = 'generic-function-factory'; Body = 'val callback: (String) -> String = makeIdentity<String>(); println(callback("Sol"))'; Declarations = 'func <T> identity(value: T): T = value; func <T> makeIdentity(): (T) -> T = identity'; Args = @(); Expected = "Sol`n" },
    @{ Name = 'generic-object-function-value'; Body = 'val callback: (String) -> String = Identity.identity; println(callback("Sol"))'; Declarations = 'object Identity { func <T> identity(value: T): T = value }'; Args = @(); Expected = "Sol`n" },
    @{ Name = 'generic-object-function-factory'; Body = 'val callback: (String) -> String = makeIdentity<String>(); println(callback("Sol"))'; Declarations = 'object Identity { func <T> identity(value: T): T = value }; func <T> makeIdentity(): (T) -> T = Identity.identity'; Args = @(); Expected = "Sol`n" },
    @{ Name = 'stateless-object-direct-call'; Body = 'println(Math.add(20, 22))'; Declarations = 'object Math { func add(left, right: int) = left + right }'; Args = @(); Expected = "42`n" },
    @{ Name = 'stateless-object-inferred-overload-value'; Body = 'val callback: (String) -> String = Selection.choose; println(callback("Sol"))'; Declarations = 'object Selection { func <T> choose(value: T) = "generic"; func choose(value: String) = "specific" }'; Args = @(); Expected = "specific`n" },
    @{ Name = 'generic-vararg-function-value'; Body = 'val firstText: ([]String) -> String = first; println(firstText(["Sol"]))'; Declarations = 'func <T> first(vararg values: T): T = values[0]'; Args = @(); Expected = "Sol`n" },
    @{ Name = 'generic-out-function-value'; Body = 'val initialize: (out String, String) -> () = set; var result: String; initialize(out result, "Sol"); println(result)'; Declarations = 'func <T> set(out target: T, value: T): () { target = value }'; Args = @(); Expected = "Sol`n" },
    @{ Name = 'function-ref-out'; Body = 'val step: (ref int) -> () = incrementRef; var value = 41; step(ref value); val seed: (out int) -> () = initialize; var result: int; seed(out result); println(value + result)'; Declarations = 'func incrementRef(ref value: int): () { value += 1 }; func initialize(out value: int): () { value = 1 }'; Args = @(); Expected = "43`n" },
    @{ Name = 'signed-min'; Body = 'println(-2147483648); val small: sbyte = -128; println(small); val big: ulong = 18446744073709551615; println(big)'; Declarations = ''; Args = @(); Expected = "-2147483648`n-128`n18446744073709551615`n" },
    @{ Name = 'float'; Body = 'println(1.5 + 2.5); println(4.0 / 2.0)'; Declarations = ''; Args = @(); Expected = "4`n2`n" }
    @{ Name = 'float-special'; Body = 'val value: single = 1.2; println(value); println(0.0 / 0.0); println(1.0 / 0.0); val zero = 0.0; println(-zero)'; Declarations = ''; Args = @(); Expected = "1.2`nnan`ninf`n-0`n" }
)
$count = 0
foreach ($case in $cases) {
    foreach ($static in @($false, $true)) {
        $name = "$($case.Name)-$static"
        $built = CompileProgram $name $case.Body $case.Declarations $static
        $run = RunProcess $built.Exe $case.Args
        if ($run.Code -ne 0 -or $run.Out -cne $case.Expected -or $run.Error.Length -ne 0) { throw "Native result mismatch ($name): code=$($run.Code), out=$($run.Out), err=$($run.Error)" }
        if ($static -and (Test-Path (Join-Path $built.Directory 'SolRT.dll'))) { throw 'Static output copied a runtime DLL.' }
        $imports = & (Join-Path $env:LLVM_ROOT 'bin/llvm-readobj.exe') --coff-imports $built.Exe
        if ($LASTEXITCODE -ne 0) { throw 'PE import inspection failed.' }
        $text = $imports -join "`n"
        if (($text -match 'Name: SolRT.dll') -eq $static) { throw 'SolRT import mode mismatch.' }
        if ($text -match '(?i)mscoree|hostfxr|coreclr') { throw 'Native application depends on CLR.' }
        $count++
        $jitArguments = @($cli, $built.Source, '-Cllvm-compiler=orc')
        if ($static) { $jitArguments += '--static-solrt-lib' }
        $jit = RunProcess (Get-Command dotnet).Source ($jitArguments + @('--') + $case.Args)
        if ($jit.Code -ne 0 -or $jit.Out -cne $case.Expected -or $jit.Error.Length -ne 0) { throw "ORC parity mismatch ($name): code=$($jit.Code), out=$($jit.Out), err=$($jit.Error)" }
        $count++
    }
}

$failures = @(
    @{ Name = 'overflow'; Body = 'val maximum = 2147483647; println(maximum + 1)'; Error = 'overflow' },
    @{ Name = 'unsigned-overflow'; Body = 'val maximum: byte = 255; val one: byte = 1; println(maximum + one)'; Error = 'overflow' },
    @{ Name = 'divide-zero'; Body = 'val zero = 0; println(1 / zero)'; Error = 'division by zero' },
    @{ Name = 'signed-divide'; Body = 'val minimum = -2147483648; val minusOne = -1; println(minimum / minusOne)'; Error = 'overflow' },
    @{ Name = 'shift'; Body = 'val shift = 32; println(1 << shift)'; Error = 'shift count' },
    @{ Name = 'negative-shift'; Body = 'val shift = -1; println(1 << shift)'; Error = 'shift count' },
    @{ Name = 'bounds'; Body = 'println(args[0])'; Error = 'IndexOutOfBoundsException' }
)
foreach ($case in $failures) {
    $built = CompileProgram $case.Name $case.Body '' $false
    $run = RunProcess $built.Exe @()
    if ($run.Code -ne 1 -or $run.Error -notmatch $case.Error) { throw "Expected native failure ($($case.Name)): $($run.Error)" }
    $count++
    $jit = RunProcess (Get-Command dotnet).Source @($cli, $built.Source, '-Cllvm-compiler=orc')
    if ($jit.Code -ne 1 -or $jit.Error -notmatch $case.Error) { throw "Expected ORC failure ($($case.Name)): $($jit.Error)" }
    $count++
}

$rejected = CompileProgram 'retained-output' 'println(1)' '' $true
$hash = (Get-FileHash $rejected.Exe -Algorithm SHA256).Hash
[IO.File]::WriteAllText($rejected.Source, 'module rejected; object Main { func main(args: []String) { var x: int; x } }')
$invalid = RunProcess (Get-Command dotnet).Source @($cli, $rejected.Source, '-o', $rejected.Exe)
if ($invalid.Code -eq 0 -or (Get-FileHash $rejected.Exe -Algorithm SHA256).Hash -ne $hash) { throw 'Invalid input replaced a prior executable.' }
Write-Host "Native AOT/ORC checks passed: $count execution/import cases plus output preservation."
