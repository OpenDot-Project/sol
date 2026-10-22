<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Source language contracts and examples

The repository keeps language contracts as commented Sol source in `spec/*.sol`, with usage examples
under `spec/examples/**/*.sol`. The [source index](../spec/README.md) explains each subject and the
[example index](../spec/examples/README.md) identifies independent source sets.

Sol projects conventionally use lowercase directory names and PascalCase source filenames.
The expected layout follows module segments below the source root: `module myproject.example`
normally resides in `src/myproject/example/Greeter.sol`. Other explicit paths remain legal;
the compiler takes module identity from the declaration and does not discover modules by directory.
See the conventional-layout [Greeter](../spec/examples/layout/src/myproject/example/Greeter.sol) and
[Main](../spec/examples/layout/src/myproject/example/Main.sol) source set. The topic-based corpus
layout serves a different purpose and does not change the normal application convention.

The files cover lexical rules, built-in and core types, bindings, functions, named/default/vararg
arguments, both lambda forms, nullable arrays, classes/objects/companions, accessors, value/ref structs,
generics, modules/imports/partials, exceptions, loops/switches, delegation, operators, annotations,
native interop, entry selection, naming, memory, reflection and the intended standard module inventory.

Planned features are documented alongside supported frontend features. Each file has a status comment
and rule comments describing the intended semantics. A syntactically valid source file is not a
verified program, a running standard library or evidence of implemented GC/native code generation.

The test project copies this public corpus into its output and parses every source through CST,
PSI and AST ownership verification. This gate tests the syntax examples and source shape, not the
meaning of every rule comment. Semantic regressions remain separate focused tests. The documentation
checks also validate the corpus's Markdown links. DocFX publishes the public sources as downloadable
resources; private working notes are not inputs to that publication.

For the current implementation boundaries, read [argument contracts](arguments.md),
[generic call analysis](generic-calls.md), and the [status page](status.md).
