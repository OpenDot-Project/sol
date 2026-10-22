<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Sol source specification

The `.sol` files in this directory explain the intended language through declarations, examples,
and rule comments. They include semantics that the compiler has not implemented yet. A source
example is a language contract, not a claim that the current compiler can build or execute it.

`examples/` contains larger usage examples grouped by subject. Every file declares its logical
module explicitly; directory and module names do not have to match. Compile source sets explicitly.
The normal project convention is lowercase directories and PascalCase `.sol` filenames. Follow the
declared module segments below the source root: `module myproject.example` conventionally lives in
`src/myproject/example/Greeter.sol`. The compiler accepts other explicit paths; layout is a naming
convention, not a module-resolution requirement. This specification corpus groups subjects for
reading, rather than placing every contract into a conventional application source tree.
Do not link this entire corpus as one application: several examples intentionally have independent
`Main` objects, and the module/partial example has a documented two-file source set.

| Contract | Topics |
|---|---|
| [Lexical.sol](Lexical.sol) | UTF-8, comments, separators, identifiers, literals |
| [Types.sol](Types.sol) | Primitive types, arrays, tuples, bottom and nullable bottom |
| [Bindings.sol](Bindings.sol) | val/var/constants, assignment, initialization |
| [Functions.sol](Functions.sol) | Grouped parameters, named/default/vararg, evaluation order |
| [Lambdas.sol](Lambdas.sol) | Named/positional/headerless lambdas, trailing syntax, ref/out |
| [NullSafety.sol](NullSafety.sol) | Nullable elements versus nullable arrays, casts and safe access |
| [Objects.sol](Objects.sol) | Classes, objects, companions, constructors, accessors, data/sealed |
| [Structs.sol](Structs.sol) | Value copying, defaultability, ref struct, ref/out locations |
| [Generics.sol](Generics.sol) | Invariance, bounds, anti-constraints, specialization limits |
| [Modules.sol](Modules.sol) | Logical identities, aliases, selective/public imports, partials |
| [Exceptions.sol](Exceptions.sol) | Checked/unchecked hierarchy, throws, catch, cleanup |
| [ControlFlow.sol](ControlFlow.sol) | Four loop forms, switch patterns, try results, tail results |
| [Delegation.sol](Delegation.sol) | Property/interface delegation and descriptor identity |
| [Operators.sol](Operators.sol) | Companion operators, pairing and location semantics |
| [Annotations.sol](Annotations.sol) | Annotation schemas, constant arguments and builtin names |
| [NativeInterop.sol](NativeInterop.sol) | Windows C boundary, unsafe, explicit native representations |
| [Naming.sol](Naming.sol) | Public names, constants, private backing names and intrinsic exceptions |
| [Entry.sol](Entry.sol) | Main object, exact argument/result contract and process boundary |
| [Memory.sol](Memory.sol) | Reachability, roots/barriers, closures, concurrency and scoped storage |
| [Reflection.sol](Reflection.sol) | Native metadata, SolProperty access and descriptor ownership |
| [StandardLibrary.sol](StandardLibrary.sol) | Intended modules and collection types |

## Implementation boundaries

- The development frontend parses the illustrated syntax into lossless CST, PSI and structural AST.
- Declaration, written-type and lexical analyses cover explicit source sets, imports, partials and
  portions of generic/type/default metadata. Direct module-function calls have a supported binding
  subset. Argument mapping preserves ordered mixed named/positional calls, middle varargs, array-valued
  named varargs, defaults and trailing-lambda destinations.
- Lambda typing, complete receiver/override/accessibility validation, contextual expression/return
  inference, generic body validation, checked effects, smart casts, definite assignment, ref escape,
  constant evaluation and generated data/delegation members remain incomplete.
- Default-expression origins and evaluation order are represented by the analysis API. Actual
  evaluation, virtual calls, native exceptions, standard library behavior and garbage collection
  require later verified FIR, LLVM and runtime implementation. No example currently produces a native
  executable. The released Sol 0.0.1 component contains only the initial CST frontend.

The corpus is syntax-checked separately from semantic tests. Syntax validation does not supply
`std.core`, resolve library APIs, prove the rule comments, or execute the examples. Planned rules
remain documented even when their semantic tests or implementation do not exist.

See [argument contracts](../docs/arguments.md), [generic call analysis](../docs/generic-calls.md), and
the [implementation status](../docs/status.md) for the supported API boundaries.
