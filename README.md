<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Sol

Sol is a Windows-only programming language and compiler project maintained by OpenDot Project.
Sol 0.1.0 introduces a verified FIR pipeline and a restricted native bootstrap. The language
specification is broader than the syntax, analysis, runtime, and code-generation subsets implemented
by this release.

The compiler is primarily C# 14 on .NET 10. Native LLVM operations cross a thin C++20/CLI bridge
to LLVM 23.1.3. The compiler package includes its private .NET 10 runtime; it uses neither
.NET NativeAOT nor SDK self-contained publishing.

## Download Sol 0.1.0

- [Frontend component](https://github.com/OpenDot-Project/sol/releases/download/v0.1.0/sol-0.1.0-windows-x86_64.zip) provides source, AST, and verified FIR inspection. It does not include the native backend.
- [Native component](https://github.com/OpenDot-Project/sol/releases/download/v0.1.0/sol-0.1.0-windows-x86_64-native.zip) adds LLVM/LLD, SolRT, AOT linking, and process-isolated ORC for the verified subset.

Download the matching SHA-256 file from the [Sol 0.1.0 release](https://github.com/OpenDot-Project/sol/releases/tag/v0.1.0) and verify the archive before extraction. The native component requires the Microsoft Visual C++ v14 x64 Redistributable. AOT EXE/DLL linking also needs the Windows SDK and MSVC libraries available through an x64 developer shell; ORC and library/container outputs do not use those SDK search paths.

The shipped native subset includes a simple Main.main([]String) entry, scalar operations, immutable UTF-8 strings, single-level scalar/String arrays, selected std.stdio and std.math APIs, and closed module-level generic functions. It does not implement the complete language, exception unwinding, general object construction, arbitrary closures, or the complete standard library. See [release notes](docs/release-notes/0.1.0.md) and the [status page](docs/status.md) for exact boundaries.

## Parse a source file

Create example.sol:

```sol
module examples.arithmetic

func add(a, b: int) = a + b
val numbers = [1, 2, 3]
```

Run the frontend component or the native component:

```powershell
solc .`example.sol --emit=cst -o .`example.cst.json
Get-Content .`example.cst.json -Raw | ConvertFrom-Json
```

CST output preserves syntax and tokens; it does not by itself establish semantic validity. For a
first native program and the exact supported subset, see [Verified FIR and native programs](docs/fir-native.md).

## Documentation

- [Getting started](docs/getting-started.md): packages, checksums, and the first parse.
- [Solc CLI](docs/cli.md): the compiler's accepted command-line contract.
- [Verified FIR and native programs](docs/fir-native.md): AOT, ORC, runtime linkage, and native limitations.
- [Native and Sol library artifacts](docs/library-artifacts.md): bin, cdylib, staticlib, and solib.
- [Standard library implementation](docs/standard-library.md): implemented APIs and remaining modules.
- [Yellow Tea](docs/yellow-tea/index.md): collector design, implementation, and qualification limits.
- [Source specification](spec/README.md): .sol language contracts and detailed examples, including features not implemented by this release.
- [Architecture](docs/architecture.md), [development](docs/development.md), and [contributing](CONTRIBUTING.md).
- [Changelog](CHANGELOG.md).

DocFX builds these guides and the generated API reference. See the development guide for local validation.

## License and contact

Copyright OpenDot Project. Licensed under **Apache-2.0 WITH LLVM-exception**;
see [LICENSE](LICENSE), [LICENSE-LLVM-exception](LICENSE-LLVM-exception), and [NOTICE](NOTICE).
Bundled third-party runtime files retain their own license and notice files.

Contact: [opendotproject.opendot@gmail.com](mailto:opendotproject.opendot@gmail.com).
