<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Getting started

## Choose and install a package

Sol 0.1.0 has a frontend component and a native component. Both target Windows x86-64 and include
a private .NET 10.0.13 runtime.

- Download sol-0.1.0-windows-x86_64.zip for CST, AST, and verified FIR inspection. This component does not include LLVM.
- Download sol-0.1.0-windows-x86_64-native.zip for the same compiler plus the restricted LLVM/SolRT backend, AOT artifacts, and isolated ORC execution.

Download the selected archive and its matching SHA-256 file from the
[Sol 0.1.0 release](https://github.com/OpenDot-Project/sol/releases/tag/v0.1.0). In PowerShell,
from the directory containing both files:

```powershell
$archive = 'sol-0.1.0-windows-x86_64-native.zip'
$expected = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected.ToLowerInvariant()) { throw 'Checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath .
$component = [IO.Path]::GetFileNameWithoutExtension($archive)
$solc = Join-Path $component 'bin/solc.exe'
& $solc --version --verbose
```

Change the archive variable to the frontend ZIP when you only need source inspection. Keep each
extracted component's bin, managed, and runtime directories together. The compiler launcher uses
that component's private framework-dependent .NET runtime; this is not .NET NativeAOT or an SDK
self-contained application.

The native component needs the Microsoft Visual C++ v14 x64 Redistributable for its C++/CLI
adapter. AOT EXE/DLL linking additionally needs MSVC x64 libraries and the Windows SDK available
through an x64 Visual Studio developer shell. ORC, staticlib, and solib outputs do not require
installed LLVM tools or Windows SDK library paths. See [native component prerequisites](native-component.md).

## Parse a source file

Create example.sol:

```sol
module samples.math

func add(a, b: int) = a + b

func calculate(value: int): int {
    val doubled = value * 2
    doubled + 1
}

val values = [1, 2, 3]
val named = { x, y -> x + y }
val positional = { $0 + $1 }
```

Write its CST JSON with either component:

```powershell
& $solc .`example.sol --emit=cst -o .`example.cst.json
if ($LASTEXITCODE -ne 0) { throw 'Parsing failed.' }
$tree = Get-Content .`example.cst.json -Raw | ConvertFrom-Json
$tree.module
$tree.root.kind
```

The last two values are samples.math and CompilationUnit. CST output contains a structured syntax
root and ordered token stream with comments and whitespace. It does not contain resolved symbols
or executable code. A source file declares its logical module; that name does not have to match
the file path.

## Inspect FIR or compile a native program

The native package can emit verified FIR for supported inputs and can compile the closed subset
documented in [Verified FIR and native programs](fir-native.md). The managed compiler component
cannot link a native executable by itself. For example, from a source checkout with the native
sample available:

```powershell
& $solc spec/examples/native/FirstProgram.sol -o .`Hello.exe
.`Hello.exe Alice 20
& $solc spec/examples/native/FirstProgram.sol -C llvm-compiler=orc -- Alice 20
```

The executable is an AMD64 Windows PE program. ORC executes in a separate native guest process.
These examples use only the supported bootstrap subset; they do not demonstrate general object
construction, source exception handling, or full standard-library coverage.

## Observe a diagnostic

Remove the module declaration and parse again. Solc reports SOL1100 and returns exit code 1.
It does not write a replacement CST output for an input set containing errors. An already existing
output remains untouched on a parse failure, so check the exit code rather than treating a stale
file as evidence of success.

For machine-readable source diagnostics:

```powershell
& $solc .`example.sol --emit=cst --error-format=json --color=never 2> diagnostics.jsonl
```

The [CLI reference](cli.md) explains output modes and diagnostic paths.
