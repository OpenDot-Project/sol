<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
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
argument bounds, exact ref/out evidence, expected result types, invariant nested applications, and
constructed base projections. The solver requires a unique most-specific bound and checks method
and constructed-owner constraints after inference. Selected closed generic member dependencies and
direct local callable contexts have a bounded original-body reanalysis stage; this does not establish
complete monomorphization or native admission. See [generic inference](generic-inference.md) and
[automatic member body analysis](generic-fir-specialization/AutomaticMemberBodyAnalysis.md).
Numeric literal applicability and intrinsic/user-conversion ranks participate in selection. User
conversions are single-step.

Private and protected access, private setters/getters, explicit override, non-overridable members,
visibility reduction, incompatible returns, and widened checked effect budgets receive diagnostics.
Inherited callable groups merge across substituted base paths; duplicate constructed paths collapse,
and a more-derived same-signature declaration replaces the base recovery candidate. Inheritance
lookup is bounded to 128 levels and 8192 constructed receiver identities. The merge is not yet a
complete interface-default conflict protocol, and generated data members remain incomplete.
Protected receiver restrictions, cross-binary distribution metadata for `internal`, complete
property/indexer inheritance, and every accessor/delegation visibility case still need verification.
See [receiver and member resolution](member-resolution.md).

## Expression and lambda checking

The analyzer handles literals, arithmetic/Boolean operations, casts/type tests, tuples, array literals,
array indexing/length, nullable access/assertion/Elvis, property reads/writes, and supported calls.
Numeric variables do not silently widen. Contextual literals are checked against their target range,
including negative signed minima. Binary128 contextual rounding remains unverified.

Named and positional lambdas use expected function parameter/result types. Headerless parameterless
lambdas can infer their tail result. Without an expected result type, labeled lambda returns and the
reachable tail are joined using the unique common-type rules; nested callable returns do not enter
that join. Bare local returns contribute `()`, and bottom-valued returns do not create a normal
exit. Implicit `it` is not introduced. Ref/out lambda headers retain their syntax mode and out
obligations. Function effects can broaden to a compatible callback budget; a callback cannot
silently discard checked effects.

`BodyAnalysis.LambdaBodies` and `GetLambdaBody` expose each resolved lambda signature, abstract
normal-exit-state count, reachable fallthrough, per-out-parameter assignment, and subtree
completeness. These are source facts, not runtime executions, closure layouts, or FIR admission.
Missing signatures have no body summary; deferred or diagnosed bodies can have a summary marked
incomplete. A non-null lambda can be source-typed in a nullable function context and widened to the
nullable destination. The current FIR/native nullable-lift gate still rejects those lambda-origin
values with `SOL2400`.

Arithmetic operators and single-step conversions can bind companion declarations. Operator checks
validate owner and arity rules, required comparison/truth pairs, Boolean results, shift counts, and
increment/decrement results. Checked arithmetic variants take precedence when applicable; otherwise
the ordinary variant remains eligible. Applicable overloads are ordered by conversion rank; a more
specific conversion beats boxing, and unique implicit user conversions remain single-step. Equality
dispatches to a declared user operator before the intrinsic equality path. Compound assignments bind
their underlying binary operator and validate its result against the writable location. Conditional
`&&`/`||` checks the matching `true`/`false` and same-type `&`/`|` declarations. Nullable value
operands bind their underlying user operators;
arithmetic/bitwise and unary results are nullable, while equality and ordering produce `bool`. Builtin
nullable numeric, Boolean, and comparison expressions receive the same type treatment. Runtime null
dispatch, nullable Boolean truth-table lowering, executable short-circuit lowering, and all contextual
or generic operator inference are not yet complete. Unsupported expression shapes remain unverified.

## Flow, effects, and storage

Structured branches join definitely assigned locals across reachable continuations. Out parameters
must be assigned on every normal exit. A ref argument requires assigned writable storage of the exact
type; an out argument can start unassigned. Ordinary properties do not impersonate ref/out locations.

Stable local null/type tests can narrow values. Assignment, ref/out mutation, and arbitrary calls
invalidate relevant mutable facts. Property values do not become stable snapshots. Short-circuit and
safe-call branches do not establish unconditional assignment from an expression that might be skipped.
Loops conservatively retain only facts established on every continuing path.

Throws and declared call/accessor effects propagate to enclosing budgets. Catch removes handled
subtypes, checks catch ordering, and analyzes handler effects separately. A call records its possible
checked exception types and a conservative `RuntimeException` edge before normal-return `out`
assignments are applied. Therefore a catch after an `out` call cannot read a previously assigned local
as though its pre-call value were unchanged. Function-value `out var` uses the parameter's exact type
and becomes definitely assigned only on normal return. `ref` calls clear narrowing before the call,
because the callee may mutate the aliased storage before either returning or throwing.

Each `try` body collects typed exceptional snapshots at supported call/accessor/operator/throw sites.
Handlers start from the join of snapshots whose declared effect can overlap the catch type; they do not
inherit assignments that happen only after a potentially throwing operation. A catch that cannot match
any collected effect reports `SOL2255`. A broader catch consumes a narrower effect; a narrower catch
does not erase the remaining possibility represented by a broader static effect. Handler exceptions
flow to the enclosing `try` rather than to sibling handlers. Calls inside a handler do not make later
sibling catches reachable.

Potentially throwing built-in expressions without a precise call edge use a conservative synthetic
`RuntimeException` snapshot. It intersects facts known at try entry and after normal completion, drops
narrowing, and defers analysis if its bounded syntax walk reaches the resource limit. This can reject a
valid read in a catch, but it does not treat a maybe-assigned local or smart cast as proven. Constant
`for false` bodies are analyzed for diagnostics but their effects and exception edges do not escape the
loop. Other loops retain only facts established on every continuing path; the complete CFG fixed point
and all loop-exit correlations remain future work.

Finally is analyzed on normal completion, each return exit, and each exception escaping the local
handler set. Its effects flow to the caller and to an enclosing exception sink. The bounded cleanup
slice tracks reached defer registrations, LIFO lexical-exit actions and source suppression metadata;
overflow stays deferred. This does not implement runtime Throwable identity or exception unwinding.
See the [cleanup/construction guide](cleanup-construction.md). RuntimeException subtypes are unchecked; a static
Exception/Throwable value remains checked. Out assignments performed in finally are applied only when
that exit completes normally.

Constructor checks cover stored-property assignment, read-before-initialization, repeated writes to
stored vals, direct receiver publication through arguments, and direct virtual dispatch during
construction. Base/secondary-constructor chains and complete partial/object initialization proofs
are still unverified. Empty implicit construction can obtain its nominal type without fabricating
a constructor AST node.

Captures of ref/out or ref-like storage require a proven nonescaping use; unsupported lifetime paths
are rejected. Known heap storage of ref-like fields is rejected.
Ref-like returns whose borrow origin is not established remain unverified; there is no complete borrow
origin solver yet. Cleanup path correlation and more complex exceptional/loop paths still need full flow proof.

Property delegation can validate `operator getValue`, `setValue`, and `provideDelegate` contracts
when the source set supplies `std.reflect.SolProperty<T>`. Getter/setter effects are separate from
initialization effects. Full interface forwarding, initialization publication, and descriptor/runtime
generation are not implemented by these source checks.

## Diagnostic groups

| Range | Subject |
|---|---|
| `SOL2200`–`SOL2207` | Return inference, expression domains, conversions, and operators. |
| `SOL2210`–`SOL2215` | Receivers, value/member lookup, indexing, and access. |
| `SOL2220`–`SOL2226` | Lambda shape, callable applicability/ambiguity, local returns, and anonymous-function default parameters. |
| `SOL2230`–`SOL2235` | Override contract compatibility. |
| `SOL2240`–`SOL2247` | Assignment, writable storage, nullable receivers, and continuation/result flow. |
| `SOL2250`–`SOL2255` | Checked effects, Throwable validity, catch reachability, and cleanup transfer. |
| `SOL2260`–`SOL2262` | Ref-like storage, borrow escape, and captures. |
| `SOL2270`–`SOL2273` | Constructor initialization and publication. |
| `SOL2280`–`SOL2282` | Delegation protocol selection and declaration combinations. |
| `SOL2290` | Bounded analysis/specialization resource failure. |

The remaining proof gaps are explicit. This work advances member/type checking and flow analysis;
it does not finish all language semantics. The separate [FIR/native pipeline](fir-native.md) admits
and independently verifies a restricted closed subset rather than treating body-analysis completeness
as a whole-program code-generation certificate. See the [roadmap](status.md).
