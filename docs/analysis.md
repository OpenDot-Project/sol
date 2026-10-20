# Declaration analysis

`OpenDot.Sol.Analysis` indexes an explicit set of AST snapshots in one distribution.
It provides logical modules, declaration identities, partial-type composition,
source import lookup, re-export inspection, and signature compilation groups.
It has no dependency on LLVM or native runtime initialization.

The session does not perform expression type checking. Name lookup returns candidates;
it does not choose a function overload from argument types or execute source code.
Development builds additionally compose [written type and lexical scope analyses](source-binding.md)
over the declaration session. Those analyses preserve their own explicit semantic limits.

## Session boundary

```csharp
using System;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;

var sources = new[]
{
    SyntaxTree.Parse(SourceText.FromString("module std.core\nclass String {}")),
    SyntaxTree.Parse(SourceText.FromString("module app\nclass Message {}")),
};
var session = AnalysisSession.Create(sources);
var file = session.Modules["app"].Files[0].Root;
var lookup = session.Lookup(file, "String");
Console.WriteLine(lookup.Kind);
```

The production default requires `std.core` in the explicit source set.
Compiler bootstrap/test callers may set `RequireCore = false`; this does not synthesize
core symbols or make the missing standard library implemented.
There is no CLI switch to bypass the core requirement.

`DistributionName` labels the one explicit distribution being analyzed.
Cross-distribution metadata loading and external binary references are not implemented.
The session does not discover sources from directories, filenames, or imports.

Snapshots, declarations, and symbols have session ownership checks.
A symbol from another session cannot be used as a member-lookup container.
ASTs remain immutable; analysis results are not written into PSI fields.
Create a new session after editing the source set.

## Modules and lookup

Module names are ordinal and case-sensitive on Windows.
Multiple files with the same explicit module name share module declarations,
while each file retains its own imports.
Moving a file does not change its logical module or partial composition key.

Lookup precedence is member container, current module, explicit imports, then implicit core.
Lexical local-variable scopes and expression reference binding are future work.
The current API accepts the member container explicitly; it does not infer it from a usage offset.

| Result | Interpretation |
|---|---|
| `Missing` | No visible declaration candidate |
| `Found` | One declaration identity |
| `OverloadSet` | Multiple function signatures originating in the same module |
| `Ambiguous` | Competing declaration identities without a valid overload set |

Source order never selects a winner between ambiguous imports.
Importing the same symbol through a diamond re-export deduplicates its identity.
An ambiguity is returned at the query point, rather than making every unused import an error.

Normal imports provide unqualified public members and qualified module access.
A module alias provides qualified access through the alias only.
Selective imports use `local = original`; only those local names enter unqualified lookup.
Selective import targets are checked against actual public exports.
Public imports re-export visible public bindings; private imports do not.
Public module aliases propagate as qualifiers through public import chains.

Private, internal, and protected declarations are not public exports.
Internal cross-module access within a distribution is not yet implemented by this public-export
lookup surface; the session conservatively exposes public declarations across module imports.
Type/member access control after receiver binding is also outside the current lookup API.

## Graphs and re-exports

`Graph` records source import dependencies. Ordinary cycles are legal signature groups.
`CompilationGroups` contains strongly connected components in dependency-first order,
with ordinal module names inside each group.
Unprovided module names remain visible as graph vertices and receive unresolved-module diagnostics.

`ReExportGraph` includes only public import edges.
Public re-export cycles are diagnosed, including self-cycles.
Export computation still reaches a finite fixed point on erroneous cyclic input,
so a recovery session can be inspected without recursive graph expansion.
Graph traversal uses explicit stacks and is tested on long dependency chains.

`TransitiveDependencies` returns unique ordinal dependency names and excludes the queried root,
including when the root participates in a cycle.
No graph operation reads files or schedules build jobs.

## Declaration and partial identities

Nominal identity includes logical module, containing nominal identity, name, and generic arity.
Class, struct, ref struct, and object declarations can compose partial parts.
Every repeated part must explicitly use `partial`.
Kinds, effective visibility, modifiers, type-parameter names, and written constraints must agree.
At most one part may own constructors, initializers, or delegated/stored property initialization.
Members from all compatible parts belong to one declaration symbol.

No source path determines initialization order or field layout.
The current session does not compute runtime layout or initialization code.
Base class/interface compatibility requires resolved types and remains future work;
written base lists are retained in the AST without claiming that compatibility was proved.

Function signature comparison includes name, generic arity, normalized written parameter types,
and value versus reference passing. Ref and out alone cannot distinguish overloads.
Parameter names, defaults, return types, constraints, and throws lists do not distinguish overloads.
Function type parameters are compared by ordinal in signatures.
Nominal aliases and imported type spellings are not resolved by this structural comparison;
additional duplicate/overload validation will be needed after type resolution.

Constructors and indexers have dedicated signature sets.
Extern function signatures enter module declaration lookup.
Enum payload variants remain nominal members; duplicate variant names are rejected.
Primary constructor parameters retain their AST property markers; generated property members,
data-class members, and implicit constructor symbols are not synthesized by this index.

The declaration validator also checks duplicate/conflicting modifiers, duplicate parameter names,
duplicate type parameters, last-position varargs, grouped default/property/vararg restrictions,
ref/out defaults, duplicate property accessors, and setters on `val` properties.
It does not infer parameter or return types.

## Diagnostics and inspection

Diagnostics retain the source snapshot and UTF-8 range that caused them.
The session collects syntax diagnostics and declaration-analysis diagnostics under one budget.
Results are ordered by source path, byte start, code, and message for presentation.
Source paths do not participate in module or type identities.

| Code | Current declaration diagnostic |
|---|---|
| `SOL2001` | Missing source module or implicit core |
| `SOL2003` | Duplicate signature or conflicting declaration name |
| `SOL2004` | Duplicate/conflicting declaration modifiers |
| `SOL2005` | Invalid parameter/type-parameter declaration shape |
| `SOL2006` | Conflicting property accessors |
| `SOL2007` | Public re-export cycle |
| `SOL2008` | Invalid selective import or alias combination |
| `SOL2030` | Incompatible partial composition |
| `SOL2090` | Named lambda header mixed with positional references in its own scope |
| `SOL2091` | Positional index cannot fit a supported function arity |

`AnalysisJson.Serialize` produces deterministic inspection JSON with modules, declaration symbols,
signatures, compilation groups, and diagnostics. It omits source paths and is not a metadata loader.
Session construction, graph construction, and inspection honor cancellation.
Once constructed, sessions support concurrent read-only lookup and serialization.

The next semantic boundary must resolve types, bases, constraints, visibility, local scopes,
call overloads, checked effects, nullability, definite assignment, and ref escape before verified FIR.
No diagnostic or successful lookup in this layer certifies those properties.
