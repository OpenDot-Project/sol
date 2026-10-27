<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic specialization and direct calls

These APIs are available in the development SDK. The published Sol 0.0.1 component remains the
initial CST frontend. This analysis does not generate code or certify function bodies.

## Type ownership and substitution

`TypeAnalysis.GetTypeParameters(declaration)` returns every declared parameter, including unused
parameters. `ResolvedType.Parameter` identifies the canonical owning declaration and zero-based
ordinal. Compatible partial parts share that identity. A method's `T` and its owner's `T` remain
distinct. Parameter spelling is not the substitution key.

`TypeSpecialization` extends one `TypeAnalysis` with an owned, synchronized interner. Existing written
types retain their identities; newly constructed types belong to this workspace. Its input analysis
remains frozen. Two workspaces may share their input types but cannot exchange their newly created
types. `ValidateOwnership` rejects foreign identities; numeric IDs alone are insufficient.

`Substitute` takes parameter-to-type replacements. Replacement is simultaneous: mapping `T -> U`
and `U -> int` replaces an occurrence of `T` with `U`, without rewriting the replacement again.
Arrays, fixed arrays, tuples, function parameter modes, results, and effect sets are reconstructed.
Nullable substitution normalizes `T?` specialized with `int?` to one nullable layer. Equal closed
results reuse an existing identity. Ref-like elements cannot enter managed arrays or ungated nominal
applications through substitution.

The default depth budget is 128 and the workspace permits 100,000 additional interned types.
Callers may lower these limits. Resource exhaustion throws `TypeSpecializationException` with
`ResourceLimit`; invalid constructed storage reports `InvalidType`. The call binder translates these
failures into source diagnostics. These budgets do not measure compiled generic machine-code
instances. The verified native subset now has a separate 4,096-instance compilation limit and
128-level specialization-type limit for closed module-level generic free functions; per-instance
source rebinding and the full Sol generic contract remain incomplete. Cancellation is observed during
substitution, constraint queries, inheritance traversal, and FIR instance scheduling.

## Bases, conversions, and constraints

`GetDirectBases` substitutes the definition's parameters into each written base. A class without a
concrete written base receives `std.core.Object` when that definition is available in the explicit
source distribution. Partial base lists are combined without repeating identical identities.

`ClassifyConversion` distinguishes identity, intrinsic reference/nullable conversion, boxing, and
absence of a supported intrinsic conversion. Nominal generic applications remain invariant. Numeric
variables do not widen. Nullable values cannot silently become non-null values. Bottom converts to
any value type; nullable bottom represents `null` and converts to nullable targets. This API does not
search user-defined implicit conversions or implement general function variance/effect conversion.

`CheckConstraints` checks one definition's ordered type arguments against inline and `where` bounds.
Bounds are substituted before checking inherited nominal interfaces and classes. Multiple bounds must
all hold. `T: Object` rejects value-type boxing. Ref-like values require `allows ref struct`; they can
satisfy an implemented interface for constrained dispatch without becoming boxed interface values.
`CheckTypeConstraints` visits nominal applications contained in a signature type. Invalid unresolved
bounds produce `Indeterminate`, rather than a successful constraint proof. These queries do not prove
heap storage, capture, escape, or generic body safety, and do not automatically validate every written
application in `SourceAnalysis`.

## Direct function call API

```csharp
using System;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Text;

var compilation = FrontendCompilation.Parse(new[]
{
    SourceText.FromString("module example\nfunc <T> identity(value: T): T = value\nfunc demo(): () { identity(42) }"),
});
var calls = compilation.AnalyzeCalls(new AnalysisOptions { RequireCore = false });
foreach (var binding in calls.Bindings)
{
    Console.WriteLine($"{binding.Status}: {binding.Selected?.ReturnType?.Name}");
}
```

The example prints `Bound: int`. `RequireCore = false` is for isolated SDK/bootstrap fixtures;
normal source distributions include the core module. Alternatively, compose `CallAnalysis.Create`
from an existing `SourceAnalysis` to reuse its declaration, written-type, and lexical tables.
There is no new Solc command or CLI emission mode for this analysis.

Supported targets are module-level source functions, unqualified or qualified through imported
module names/aliases. A local value shadowing a qualifier prevents module lookup. Functions from
ambiguous imported modules are not silently combined into a new overload set. Member calls,
constructors, local functions, function values, and compiler intrinsics remain `Deferred`.

Each candidate records the following independently:

- Ordered explicit or inferred type arguments and substituted parameter/result/effect identities.
- Source argument indexes mapped to parameter indexes, preserving evaluation order.
- Defaulted parameter indexes, expanded vararg elements, and managed-array spread arguments.
- Applicability, rejection/defer reason, and conversion ranks.

Binding first validates argument structure. Ordered named/positional mixing is allowed; after a
named argument skips or reorders parameters, ordinary following arguments must be named. Names
must exist and cannot fill an occupied parameter again. Required parameters must be present.
The one vararg may occur anywhere; later parameters are named or a final function parameter is
supplied by an outside lambda. Named vararg takes an array, with or without `*`. Defaults and empty
varargs have separate records. See [argument contracts](arguments.md) for the complete rules.
Ref/out markers must match; supported ref/out locations are mutable typed locals and ref/out
parameters, with exact storage type. `out var name` can receive a selected parameter's type at the
call, but uses of that new local still need later inference/flow analysis. Property access is not
substituted with a temporary writable location. Definite assignment and lifetime remain unchecked.

Generic inference uses argument lower bounds, exact ref/out evidence, and an expected call result when
the enclosing body supplies one. Structural matching preserves invariant generic applications and
nullable normalization. Constructed base projections can infer a parameter from an implementing or
derived argument. Multiple bounds require one unique most-specific candidate; numeric widening is
not introduced. Constraints are checked after inference for both method-owned and constructed-owner
parameters. Explicit type arguments bypass evidence collection for those parameters but still pass
constraint and conversion checks. Bounded unordered matching can infer multiple top-level callback
effect patterns when exactly one complete assignment survives; ambiguous and nested function-type
sets remain deferred. Complete lambda/array literal evidence and monomorphized body rechecking also
remain deferred. See
[generic inference](generic-inference.md) for bounds, limits, and examples.

Argument typing currently covers written lexical types, simple literal-initialized locals, typed
properties, grouping, tuples, and nested bound direct calls. Numeric literals prefer default
`int`/`double` matches, then other representable contextual targets; integer bounds are checked before
selection. Binary128 literal rounding is deferred. Built-in and supported user-defined unary/binary
operators bind in body analysis; generic operator arguments can use operand and expected-result
evidence. Array literal inference, lambdas, arbitrary property initializers, and full expression-body
return inference remain incomplete.
A block function without a result annotation supplies `()`; expression-body functions need a written
result for this binder. Declared effects are substituted and retained, without checked-effect coverage.

Applicable candidates are compared per argument: identity, intrinsic reference conversion, then
boxing. Incomparable argument rankings remain ambiguous. Equal rankings prefer ordinary arity over
vararg expansion, fewer defaults, then an exact non-generic candidate over a generic candidate.
Source declaration order is never a tie breaker. If a potentially applicable candidate needs an
unimplemented analysis, the entire selection remains deferred. When user implicit conversions exist,
a type mismatch requiring that missing search is deferred.

## Results and inspection

| Status | Meaning |
|---|---|
| `Bound` | Unique selected direct-function candidate within this supported analysis |
| `NoApplicableCandidate` | All visible candidates were rejected |
| `Ambiguous` | No unique best candidate, or ambiguous imported function name |
| `Deferred` | Receiver, argument, inference, conversion, or signature analysis is unavailable |
| `Unresolved` | No visible module-level function for a direct non-intrinsic name |

`HasErrors` includes input source errors and call diagnostics. `IsComplete` means the input is free
of reported source errors and every inspected call is bound; it does not mean the program is fully
type checked, effect safe, or executable. `SOL2130` is an unresolved direct call, `SOL2131` no applicable
overload, `SOL2132` ambiguity, and `SOL2134` a bounded specialization failure. Default analysis adds
`SOL2135` for replacement override defaults, `SOL2136` for unavailable own/later parameter reads,
`SOL2137` for competing inherited default origins, and `SOL2138` for bounded default lookup failures.

`CallBindingJson.Serialize` writes version-1 inspection JSON containing file indexes, owned type
identities, parameter owners/ordinals, candidate outcomes, argument mappings, defaults, effects, and
selected symbols. It omits source paths and distinguishes files of the same module. This is not a
loader, stable cache schema, executable artifact, or FIR. Completed call tables support concurrent
readers; further explicit workspace queries may intern additional types.

Receiver member analysis is available through `BodyAnalysis`, including constructed generic bases,
inherited overload groups, and method inference. The direct `CallAnalysis` API still cannot consume
an enclosing expected-result type. Complete protected receiver checks, cross-distribution access,
all user conversions, checked-effect coverage, smart casts, definite assignment, ref escape, and
full verified FIR remain future stages. A bounded closed generic free-function slice is now carried
from `BodyAnalysis` into verified FIR; it does not add a stable generic binary ABI or make all
body-analysis results executable. See [member resolution](member-resolution.md) and
[generic FIR lowering](generic-fir-specialization-lowering.md).
