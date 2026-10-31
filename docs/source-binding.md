<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Written types and lexical scopes

Development builds provide `TypeAnalysis`, `LexicalAnalysis`, and the composed `SourceAnalysis`
over an existing declaration session. These are immutable analysis tables over the AST,
not a new compiler intermediate representation. They do not load LLVM.

## Compose a source analysis

```csharp
using System;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Text;

var compilation = FrontendCompilation.Parse(new[]
{
    SourceText.FromString("module std.core\nclass String {}"),
    SourceText.FromString("module app\nfunc echo(value: String): String = value"),
});
var analysis = compilation.AnalyzeSources();
Console.WriteLine(analysis.HasErrors);
foreach (var binding in analysis.Lexical.Bindings)
{
    Console.WriteLine(binding.Reference.Spelling);
}
```

`SourceAnalysis.Create(declarations)` also accepts an existing declaration session.
Its `Types` and `Lexical` properties share that exact session and its AST snapshots.
The aggregate diagnostic list combines the three phases under the session's explicit budget.
Cancellation is accepted during construction and inspection; completed results support concurrent readers.

The production default still requires an explicit `std.core` source module.
Bootstrap callers may disable that requirement through `AnalysisOptions` without generating fake core types.
The CLI `--emit=ast` option remains structural output; it does not run these analyses.

## Type identities

`TypeAnalysis.GetType(astType)` returns an interned `ResolvedType`.
Equivalent references in the same analysis share object identity and a local integer ID.
`ValidateOwnership` rejects types from another analysis, even if their IDs happen to match.
IDs are not stable cache keys, binary metadata identities, or runtime type descriptors.

The current kinds are error, primitive, nominal, parameter, bottom, nullable, managed array,
fixed array, tuple, and function. Nominal types retain their `DeclarationSymbol` and ordered arguments.
Parameter types are scoped to their generic owner and ordinal. Compatible partial type parts
therefore share parameter identities; unrelated method parameters remain distinct.
Companion scopes do not capture their enclosing class's generic parameters.

Primitive names are `sbyte`, `short`, `int`, `long`, `byte`, `uint`, `ulong`, `half`, `single`,
`double`, `quad`, and `bool`. They do not require fake nominal declarations.
There is no `ubyte` primitive. Primitive names cannot be redeclared or shadowed.
`String`, `Object`, and exception types remain nominal source declarations.

Type lookup uses generic parameters, containing nominal scopes, module declarations,
explicit imports, and implicit core. Import aliases and selective renames resolve to the original
declaration identity. Unknown names, wrong generic arity, and competing imported types produce diagnostics.
Nested nominal types can be reached through their containing type.

Array element binding follows the source grammar: `[]String?` and `[](String?)` have the same identity;
`([]String)?` has a different outer nullable identity. Fixed array lengths normalize their numeric radix,
so `[16]int` and `[0x10]int` have the same written type identity.
Function passing modes and effect identities participate in interning. Repeated effect entries are deduplicated;
this does not yet verify that an effect is a non-null Throwable subtype.

Ref structs retain their ref-like classification through nullable, tuple, and fixed-array wrappers.
Managed arrays reject ref-like elements. A ref-like generic argument requires the written
`allows ref struct` gate. That gate does not prove that the generic body obeys its storage rules.

## Written base validation

The type analysis checks that explicit bases resolve to non-null class/interface declarations,
that interfaces and structs do not inherit concrete classes, and that ordinary final classes are not inherited.
`std.core.Object` retains its intrinsic root role.
Multiple incompatible concrete bases across partial parts and inheritance cycles are diagnosed.
Base `by` delegation requires an interface.

This is not a complete inheritance or layout verifier. Implicit base insertion, generic substitution
through base members, full visibility, sealed distribution boundaries, interface implementation,
override contracts, and inline layout cycles require further semantic work.
Resolved nongeneric parameter identities additionally expose duplicate signatures hidden by import aliases.
Generic substitution and constraint satisfaction still need a complete overload/type checker.

## Lexical scopes

`LexicalAnalysis.Scopes` exposes immutable scope ownership, parent IDs, and source declarations.
`Lookup(astLocation, name)` first examines visible lexical declarations and then uses the declaration session.
`GetBinding(astNameExpression)` returns the result retained for an ordinary unqualified value reference.
Foreign snapshot locations and references are rejected.

Parameters are independent even when their source type was grouped.
Local variables become visible after their initializer; an initializer can therefore refer to an outer name
that a new local subsequently shadows. Block-local declarations do not escape the block.
Duplicate names in one lexical scope are diagnosed; nested shadowing remains distinct.
Local functions are visible from their declaration start, including within their own bodies.
Same-name local functions remain overload candidates; call selection and complete local signature validation
belong to the later expression binder.

For-each names are visible in the body, not in the iterable expression or after the loop.
Three-clause for-loop locals span the condition, update, and body only.
Catch names are confined to their handler. Flat payload-pattern bindings are confined to their case.
Destructuring `_` creates no binding. An `out var` declaration is retained for subsequent statements;
definite assignment after exceptional control flow is not proven here.

Named lambda and anonymous-function parameters own separate scopes. No implicit `it` is introduced.
References crossing a lambda or local-function boundary are marked as outer-local capture candidates.
`LocalFunctionCaptures`/`GetLocalFunctionCaptures(function)` records the resolved local symbols a
nested named function references across its function boundary, including source-ordered references
and combined read/write access. It excludes top-level/member functions and does not create a closure
ABI or prove lifetime. These inventories are not escape, lifetime, or closure-allocation proofs.
Receiver/member captures are tracked separately for lambdas and anonymous functions; local-function
receiver environments remain outside this inventory.

Primary constructor property parameters are available in the type's source scope.
Constructor-only parameters are available to instance initialization, not ordinary methods.
Setter/init accessors receive their `value` context binding.
Generated backing-slot semantics and constructor definite assignment remain unimplemented.

Missing unqualified value references remain query results rather than unconditional diagnostics.
Some such names can be module qualifiers, intrinsic calls, or receiver-dependent references;
the body binder distinguishes these before issuing unresolved-value errors. Positional lambda
references require an expected function type for complete binding.

## Body analysis and callable expressions

`BodyAnalysis.Create(sourceAnalysis)` layers expression types, selected supported calls and operators,
checked-effect propagation, return/result checking, definite assignment for supported `out` storage,
and explicit deferred-proof locations over the immutable source tables. `GetLocalType` returns a
resolved local type only when that analysis owns the symbol and has a non-error type. This remains
source analysis; it does not write types into AST nodes or create FIR.

Anonymous function expressions have their own parameter scope and result/effect flow. The public
`AnonymousFunctionBodies` inventory records a resolved function type, abstract normal-exit-state
count, reachable fallthrough, and per-`out`-parameter assignment facts. The count is not a count of
runtime paths. `IsComplete` means this function's analyzed source subtree has no known diagnostics or
deferred proof gap. `GetAnonymousFunctionBody` returns null if the function signature or body summary
could not be produced. Local/receiver capture inventories and escape classifications are separate
tables; none of these source facts establish closure layout or authorize native code.

Lambdas expose parallel `LambdaBodies`/`GetLambdaBody` facts: resolved function type, abstract exit
count, fallthrough, out-parameter assignment, and completeness. With no expected result type, the
body binder joins reachable labeled returns and the tail while excluding nested callable bodies.
Nullable function context supplies lambda parameter types; the lambda expression remains non-null
and widens at the destination. Current native FIR still rejects that nullable lambda lift.

See [function values and closure limits](generic-fir-specialization/FunctionValues.md) for the exact
noncapturing lambda/anonymous-function FIR subset and the rejected closure cases.

## Inspection and limits

`BindingJson.Serialize(sourceAnalysis)` writes flat schema-version-1 inspection JSON with type IDs,
scope parents, local declarations, reference candidates, and capture flags. It omits source paths.
Snapshot-local file IDs distinguish references from different files of the same module.
It is not a loader, executable artifact, or stable external semantic schema.

| Code | Current diagnostic |
|---|---|
| `SOL2002` | Ambiguous type or unqualified value candidates |
| `SOL2003` | Duplicate signature exposed by resolved imported types |
| `SOL2110` | Type resolution nesting budget exceeded |
| `SOL2111` | Fixed array length does not fit a nonnegative int |
| `SOL2112` | Unknown/ambiguous-arity or malformed type reference |
| `SOL2113` | Invalid explicit base kind, final inheritance, or concrete-base conflict |
| `SOL2114` | Inheritance cycle |
| `SOL2115` | Invalid ref-like array or ungated generic argument |
| `SOL2116` | Primitive type name redeclared/shadowed |
| `SOL2120` | Duplicate lexical declaration |

The optional [generic specialization and call analysis](generic-calls.md) extends these tables with
substitution, nominal constraints, and a supported subset of direct-call overload selection.
`SourceAnalysis.Defaults` additionally indexes declaration-owned/inherited defaults and checks
their parameter scope. See [argument contracts](arguments.md) for these query guarantees and limits.

Expression typing, overload selection, conversions, checked effects, and selected definite-assignment
checks exist only for documented bounded constructs. User-defined conversion search, complete generic
body rebinding, smart casts, full definite-assignment coverage, and general ref/closure escape proof
remain incomplete. Verified FIR and native emission apply a stricter subset and independently check
source correspondence. A source analysis without diagnostics does not certify the whole language.

`LambdaEnvironmentPlanner` and `AnonymousFunctionEnvironmentPlanner` combine the source capture,
receiver, escape, body-completeness, and resolved-local-type inventories into deterministic field and
lifetime candidates. Receiver requirements have a distinct `ReceiverCapture` storage candidate;
they are not labeled as ref/out borrowed locals. A ref-like receiver that may escape keeps lifetime
unknown. `Stack` and `Heap` describe source candidates only: these planners do not choose allocation,
define field offsets, emit a closure ABI, or produce a Yellow Tea root descriptor.

The escape inventory can prove a mutable local callable alias non-escaping when every tracked write
has a known callable or local-alias source and the joined uses remain local. It unions all observed
callable identities across writes and branch alternatives; it does not choose a path. Null or
untracked writes, ref/out consumers, nested callable capture, publication, and unsupported use remain
unknown or escape-capable. This refinement describes observed source uses; it does not authorize
stack allocation or imply that a call argument is non-retaining.
