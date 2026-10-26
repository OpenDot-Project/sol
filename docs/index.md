<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Sol documentation

Sol is a Windows-only programming language project. Sol 0.0.1 provides the first compiler frontend
slice: UTF-8 source handling, lexical analysis, bounded parsing, and lossless CST JSON output.

Start with [installation and a first parse](getting-started.md), then use the [CLI reference](cli.md)
or [frontend guide](frontend.md). Contributors should read [development and validation](development.md).
Source builds also provide the [typed PSI source interface](psi.md).

The [architecture guide](architecture.md) describes both existing components and their planned
extensions. The [status page](status.md) distinguishes released behavior from future work.
Generated API pages document the actual C# assemblies; they are not a complete language specification.

The [Yellow Tea section](yellow-tea/index.md) documents the native collector design and its pending
implementation/validation work. Source builds also expose [body type and flow analysis](body-analysis.md),
with focused guides for [generic inference](generic-inference.md), [member resolution](member-resolution.md),
and [path-sensitive flow and checked effects](flow-and-effects.md). The [cleanup and construction
effects guide](cleanup-construction.md) specifies lexical `defer` behavior and checked effects from
implicit construction, including the current analysis and native-backend boundaries.

The [array and cursor iteration guide](array-iteration.md) defines dynamic-array `for-in`, item-copy
semantics, loop transfers, GC obligations, the future cursor protocol, and the verified-FIR/native
qualification boundary. The corresponding normative contract and fixture matrix are indexed in the
[source specification](../spec/README.md).

Native compilation and execution are not available in 0.0.1. A successful parse establishes that
the implemented syntax subset was accepted; it does not resolve names, check types, or run a program.
