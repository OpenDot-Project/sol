# Body analysis

Source builds provide an optional `BodyAnalysis` SDK over a `SourceAnalysis`. It adds receiver/member
lookup, expression and lambda types, and structured control-flow checks. These are source-owned side
tables over AST nodes, not FIR or executable output. Sol 0.0.1 does not contain this API.

## Entry points and results

Create an analysis with `BodyAnalysis.Create(sources, cancellationToken)`, or compose parsing and
source binding through `FrontendCompilation.AnalyzeBodies(options, cancellationToken)`.

`Expressions` contains visited expression facts: source node, resolved type, status, writable/storage
classification, and checked/declared effects observed for that expression. `Bodies` records callable
return types, normal completion, status, and escaping checked effects. Result order is deterministic
by source path and byte position; identities belong to the supplied source set and type workspace.

`GetExpression` and `GetBody` reject foreign snapshots even when node IDs and source text match.
A null result means that the owned node was not visited as an independently analyzed expression/body.
`Types` is the specialization workspace owning inferred types. It can construct function types with
parameter passing modes and effect sets without mutating the written-type analysis.

## Completeness is separate from diagnostics

`HasErrors` includes upstream syntax, declaration, type, scope, and default-contract failures.
`IsComplete` also requires that no unverified semantic obligations remain. `UnverifiedNodes` exposes
the locations of those obligations. A program with no diagnostics can still require further proof.

The statuses `Bound`, `Invalid`, and `Deferred` describe available facts. They do not certify native
layout, generated code, or whole-program safety. Consumers must not use `HasErrors == false` alone
as authorization to execute or emit native code.

## Receiver and callable checking

Implemented paths include instance/object/companion methods, inherited members with substituted
generic receiver arguments, constrained type-parameter members, source constructors, and positional
function-value calls. Local functions without unresolved capture dependencies are callable. Module
qualifiers retain explicit import and shadowing behavior.

Call mapping reuses the named/default/vararg contract: source-order mapping, named arrays, middle
varargs, explicit ref/out modes, and final-parameter trailing lambdas. Generic inference uses known
argument and expected return types; it is not an unrestricted solver. Numeric literal applicability
and intrinsic/user-conversion ranks participate in selection. User conversions are single-step.

Private and protected access, private setters/getters, explicit override, non-overridable members,
visibility reduction, incompatible returns, and widened checked effect budgets receive diagnostics.
Current inherited lookup is intentionally bounded; full inherited overload merging, interface-default
conflict resolution, and generated data members remain work to complete.

## Expression and lambda checking

The analyzer handles literals, arithmetic/Boolean operations, casts/type tests, tuples, array literals,
array indexing/length, nullable access/assertion/Elvis, property reads/writes, and supported calls.
Numeric variables do not silently widen. Contextual literals are checked against their target range,
including negative signed minima. Binary128 contextual rounding remains unverified.

Named and positional lambdas use expected function parameter/result types. Headerless parameterless
lambdas can infer their tail result. Implicit `it` is not introduced. Ref/out lambda headers retain
their syntax mode and out obligations. Function effects can broaden to a compatible callback budget;
a callback cannot silently discard checked effects.

Arithmetic operators and single-step conversions can bind companion declarations. Complete operator
pair validation, nullable user-operator lifting, conditional truth operators, checked-variant preference,
and all contextual inference cases are not yet complete. Unsupported expression shapes remain unverified.

## Flow, effects, and storage

Structured branches join definitely assigned locals across reachable continuations. Out parameters
must be assigned on every normal exit. A ref argument requires assigned writable storage of the exact
type; an out argument can start unassigned. Ordinary properties do not impersonate ref/out locations.

Stable local null/type tests can narrow values. Assignment, ref/out mutation, and arbitrary calls
invalidate relevant mutable facts. Property values do not become stable snapshots. Short-circuit and
safe-call branches do not establish unconditional assignment from an expression that might be skipped.
Loops conservatively retain only facts established on every continuing path.

Throws and declared call/accessor effects propagate to enclosing budgets. Catch removes handled
subtypes, checks catch ordering, and analyzes handler effects separately. RuntimeException subtypes
are unchecked; a static Exception/Throwable value remains checked. Finally effects remain visible,
and out assignments performed in finally are applied to normal return exits.

Constructor checks cover stored-property assignment, read-before-initialization, repeated writes to
stored vals, direct receiver publication through arguments, and direct virtual dispatch during
construction. Base/secondary-constructor chains and complete partial/object initialization proofs
are still unverified. Empty implicit construction can obtain its nominal type without fabricating
a constructor AST node.

Ref/out and ref-like closure captures are rejected. Known heap storage of ref-like fields is rejected.
Ref-like returns whose borrow origin is not established remain unverified; there is no complete borrow
origin solver yet. Defer cleanup and more complex exceptional/loop paths still need full flow proof.

Property delegation can validate `operator getValue`, `setValue`, and `provideDelegate` contracts
when the source set supplies `std.reflect.SolProperty<T>`. Getter/setter effects are separate from
initialization effects. Full interface forwarding, initialization publication, and descriptor/runtime
generation are not implemented by these source checks.

## Diagnostic groups

| Range | Subject |
|---|---|
| `SOL2200`–`SOL2207` | Return inference, expression domains, conversions, and operators. |
| `SOL2210`–`SOL2215` | Receivers, value/member lookup, indexing, and access. |
| `SOL2220`–`SOL2225` | Lambda shape, callable applicability/ambiguity, and local returns. |
| `SOL2230`–`SOL2235` | Override contract compatibility. |
| `SOL2240`–`SOL2247` | Assignment, writable storage, nullable receivers, and continuation/result flow. |
| `SOL2250`–`SOL2254` | Checked effects, Throwable validity, catches, and cleanup transfer. |
| `SOL2260`–`SOL2262` | Ref-like storage, borrow escape, and captures. |
| `SOL2270`–`SOL2273` | Constructor initialization and publication. |
| `SOL2280`–`SOL2282` | Delegation protocol selection and declaration combinations. |
| `SOL2290` | Bounded analysis/specialization resource failure. |

The remaining proof gaps are explicit. This work advances member/type checking and flow analysis;
it does not finish all language semantics or produce verified FIR. See the [roadmap](status.md).
