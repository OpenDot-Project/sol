# Source language contracts and examples

The repository keeps language contracts as commented Sol source in `spec/*.sol`, with usage examples
under `spec/examples/**/*.sol`. The [source index](../spec/README.md) explains each subject and the
[example index](../spec/examples/README.md) identifies independent source sets.

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
