<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Sol documentation

Sol 0.1.0 is a Windows x86-64 compiler release with two package variants. The frontend component
provides CST, AST, and verified FIR inspection. The native component adds LLVM 23.1.3, embedded
LLD.COFF/LLD.Common, AOT artifact generation, and process-isolated ORC execution for a closed,
verified subset. Neither package implements the full language specification.

Start with [package installation and a first parse](getting-started.md), then consult the
[Solc CLI reference](cli.md). Use the [verified FIR/native guide](fir-native.md) and
[artifact guide](library-artifacts.md) for the implemented backend subset. The
[status page](status.md) separates shipped capabilities from remaining work.

The language contracts and detailed examples live in the [source specification](../spec/README.md).
Many contracts describe syntax or semantics that are not yet accepted by the native backend.
Generated API pages document the C# assemblies; they are not a complete language specification or
a stable NuGet SDK promise.

The [Yellow Tea section](yellow-tea/index.md) documents the experimental native collector. It uses
precise roots and concurrent mark/sweep, but does not carry a measured latency or production-readiness
guarantee. The managed compiler and native ORC guest use separate processes and heaps.

The architecture, native dependency review, and roadmap describe this repository's own components
and their current boundaries. Other OpenDot tools are documented in their own repositories.
