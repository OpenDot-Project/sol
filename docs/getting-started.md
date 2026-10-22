<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Getting started

## Install the Windows component

Download `sol-0.0.1-windows-x86_64.zip` and its `.sha256` companion from the
[Sol 0.0.1 release](https://github.com/OpenDot-Project/sol/releases/tag/v0.0.1).
In PowerShell, from the download directory:

```powershell
$archive = 'sol-0.0.1-windows-x86_64.zip'
$expected = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw 'Checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath .
$solc = '.\sol-0.0.1-windows-x86_64\bin\solc.exe'
& $solc --version --verbose
```

This component targets Windows x86-64. It carries its own .NET 10.0.13 runtime; the compiler's
managed application requires .NET 10, but a separate system installation is unnecessary for the
component. LLVM is also unnecessary for the released CST-only frontend.

The extracted layout is:

```text
sol-0.0.1-windows-x86_64/
  bin/solc.exe
  managed/                 Managed compiler assemblies and runtime configuration
  runtime/                 Private dotnet host and .NET runtime
  licenses/                Sol license and attribution files
  CHANGELOG.md
  component.json           File sizes, SHA-256 values, and component identity
```

Move the entire directory to relocate it. Moving only `solc.exe` breaks its relative payload paths.
The native launcher starts the package's own runtime and forwards arguments and the compiler exit
code. Our scripts assemble these directories; the managed application is not NativeAOT or an SDK
self-contained publish. Third-party runtime license and notice files remain inside `runtime`.

## Parse a source file

Create `example.sol` in your working directory with this content:

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

```powershell
& $solc .\example.sol --emit=cst -o .\example.cst.json
if ($LASTEXITCODE -ne 0) { throw 'Parsing failed.' }
$tree = Get-Content .\example.cst.json -Raw | ConvertFrom-Json
$tree.module
$tree.root.kind
```

The last two values are `samples.math` and `CompilationUnit`. The output includes a structured
syntax root and an ordered token stream with comments and whitespace. It does not contain resolved
symbols or executable code. The module is declared by the file; it is not derived from `example.sol`.

## Observe a diagnostic

Remove the module declaration and parse again. Solc reports `SOL1100` and returns exit code 1.
It does not write a replacement CST output for an input set containing errors. An already existing
output remains untouched on a parse failure, so check the exit code rather than treating a stale
file as evidence of success.

For machine-readable source diagnostics:

```powershell
& $solc .\example.sol --emit=cst --error-format=json --color=never 2> diagnostics.jsonl
```

The [CLI reference](cli.md) explains which diagnostic paths currently produce JSON.

## What to do next

Use the [frontend API](frontend.md) to inspect sources in a C# application, or follow
[development setup](development.md) to build a checkout. The [status page](status.md) lists the
features that still require implementation. A normal invocation without `--emit=cst` currently
returns `SOL0001`; it does not generate a native executable.
