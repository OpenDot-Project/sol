# Sol

Sol is a Windows-only programming language and compiler project maintained by OpenDot Project.
The compiler is implemented primarily in C# 14 on .NET 10. The planned native backend targets LLVM 23.1.3.

**Current release: [Sol 0.0.1](https://github.com/OpenDot-Project/sol/releases/tag/v0.0.1).**
This release implements an initial lossless CST frontend. It can parse the supported syntax subset
and produce JSON for inspection. Semantic analysis, native code generation, Yellow Tea garbage
collection, and the standard library are still under development. It does not build runnable Sol programs.

## Try the frontend

Download the Windows x86-64 ZIP and its `.sha256` file from the release above. In PowerShell,
from the directory containing both files:

```powershell
$archive = 'sol-0.0.1-windows-x86_64.zip'
$expected = ((Get-Content "$archive.sha256" -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Checksum mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath .
$solc = '.\sol-0.0.1-windows-x86_64\bin\solc.exe'
& $solc --version --verbose
```

The component includes a private .NET 10 runtime assembled by our packaging scripts. You do not
need to install .NET separately to run this component. Keep its `bin`, `managed`, and `runtime`
directories together. This is a framework-dependent managed application with a bundled runtime;
it does not use .NET NativeAOT or SDK self-contained publishing.

Create `example.sol`:

```sol
module examples.arithmetic

func add(a, b: int) = a + b

val numbers = [1, 2, 3]
val sum = { x, y -> x + y }
val positionalSum = { $0 + $1 }
```

```powershell
& $solc .\example.sol --emit=cst -o .\example.cst.json
Get-Content .\example.cst.json -Raw | ConvertFrom-Json
```

Parsing succeeds for this example; it does not execute the functions or infer their semantic types.
Every source declares its logical module explicitly. The module name does not have to match the file path.

## Documentation

- [Getting started](docs/getting-started.md): installation, package layout, and first CST output.
- [Solc CLI](docs/cli.md): every currently accepted option, diagnostics, and output behavior.
- [Frontend guide](docs/frontend.md): the supported grammar subset and C# API usage.
- [PSI source views](docs/psi.md): typed source inspection available in development builds.
- [Structural AST](docs/ast.md): source origins, parameter expansion, and normalized type spelling.
- [Declaration analysis](docs/analysis.md): explicit modules, source imports, and partial composition.
- [Architecture](docs/architecture.md): existing projects, ownership, and future compiler stages.
- [Development and validation](docs/development.md): build, tests, coverage, DocFX, and packaging.
- [Status and roadmap](docs/status.md): implemented capabilities and the order of upcoming work.
- [Contributing](CONTRIBUTING.md) and [changelog](CHANGELOG.md).

These guides and generated API reference can be built locally with DocFX. See the development guide.

## License and contact

Copyright OpenDot Project. Licensed under **Apache-2.0 WITH LLVM-exception**;
see [LICENSE](LICENSE), [LICENSE-LLVM-exception](LICENSE-LLVM-exception), and [NOTICE](NOTICE).
Bundled third-party runtime files retain their own license and notice files.

Contact: [opendotproject.opendot@gmail.com](mailto:opendotproject.opendot@gmail.com).
