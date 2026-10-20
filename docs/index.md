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
implementation/validation work. Source builds also expose [body type and flow analysis](body-analysis.md).

Native compilation and execution are not available in 0.0.1. A successful parse establishes that
the implemented syntax subset was accepted; it does not resolve names, check types, or run a program.
