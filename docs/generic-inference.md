<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic inference in source analysis

This page explains the development implementation. The normative language rules are in
[GenericInference.sol](../spec/GenericInference.sol), and method/member lookup is described in
[member resolution](member-resolution.md). `CallAnalysis` and `BodyAnalysis` are inspection APIs;
neither is verified FIR or an executable backend.

The detailed contracts are split into [common types](../spec/GenericInference.CommonTypes.sol),
[recursive bounds and cycles](../spec/GenericInference.Cycles.sol),
[flow-sensitive evidence](../spec/GenericInference.Flow.sol),
[literal/context evidence](../spec/GenericInference.Literals.sol), and the newer
[lambda-candidate](../spec/GenericInference.LambdaCandidates.sol),
[array-candidate](../spec/GenericInference.ArrayCandidates.sol),
[candidate-transaction](../spec/GenericInference.CandidateTransactions.sol),
[effect-candidate](../spec/GenericInference.EffectCandidates.sol), and
[evidence-provenance](../spec/GenericInference.EvidenceProvenance.sol),
[lambda fixed-point](../spec/GenericInference.LambdaFixedPoint.sol),
[array contextual typing](../spec/GenericInference.ArrayContextualTyping.sol),
[array/vararg mapping](../spec/GenericInference.ArrayVarargMapping.sol), and
[candidate evidence ledger](../spec/GenericInference.CandidateEvidenceLedger.sol) contracts. These
files describe required language behavior, including cases the current binder does not yet prove.

## Inputs and outputs

The solver receives declaration-owned type parameters, a fixed substitution, and a set of typed
constraints. A constraint retains the source template, actual/expected type, relation, and origin.
The fixed substitution normally includes constructed receiver parameters and explicit type
arguments. The result reports one of:

| Status | Meaning |
|---|---|
| `Solved` | Every requested parameter has one unique closed type. |
| `MissingEvidence` | At least one parameter has no source argument or expected-result evidence. |
| `ConflictingEvidence` | Exact, lower, or upper bounds have no unique compatible solution. |
| `IncompatibleShape` | An invariant nested type or callable shape cannot match. |
| `ResourceLimit` | Structural or constructed-base traversal exceeded a fixed bound. |

The service is internal to the compiler SDK. Public analysis results expose candidate outcomes and
substituted types instead of exposing a solver object whose implementation limits could later
change.

Each collected bound retains its relation and source origin. Candidate reasons summarize the
distinct relevant argument or expected-result origins in deterministic order, capped at eight
displayed origins. This makes a conflict inspectable without exposing machine paths or publishing
the solver's internal type identities as a stable SDK contract.

## Input, exact, and expected evidence

For a value argument, a direct formal parameter `T` receives the actual type as a lower bound. For
`Box<T>`, the outer nominal constructor must match `Box`; then its invariant type argument provides
exact evidence. For `ref` and `out`, a writable exact storage type is required. The generic solver
does not convert or widen a ref/out location.

The expected type of a call expression is an output bound. If `identity<T>` returns T and the call
is assigned to a `String`, the result context can infer T as String. For a generic nominal result,
expected-result inference can project the open result type through its base graph before collecting
invariant nested evidence. The specialized result is still checked against the expected type after
inference. A final expression in a block-bodied callable whose result is `()` is evaluated and
discarded; it has no return-context type to feed back into generic inference.

Each candidate receives a private copy of its substitutions and evidence. One rejected overload
cannot alter the inference state, diagnostics, flow, or lambda context of another candidate.

## Constructed base projection

Input evidence can pass through a base projection. In this example, the source parameter of
`Reader<T>` is recovered from the implemented interface rather than from the concrete class name:

```sol
module docs.generic.projection

interface Reader<T> {
    func read(): T
}

class StringReader: Reader<String> {
    override func read(): String = "value"
}

func <T> readValue(reader: Reader<T>): T = reader.read()

func demo(): String = readValue(StringReader())
```

If one type reaches the same generic base through several paths, every distinct constructed base
identity contributes evidence. A repeated path to the exact same type identity is deduplicated. If
the paths imply conflicting type arguments, the candidate is rejected. Invariance prevents a
projection from changing `Reader<Derived>` into `Reader<Base>`.

Expected-result projection works in the opposite direction. Incompatible routes are ignored while
the solver checks other constructed bases. If more than one route can satisfy the expected type but
the routes produce different generic evidence, the expected type does not select a route. It adds no
evidence at that projection; independent input evidence may still solve the call, and the specialized
result is then checked for conversion to the expected type. If the remaining parameters have no
evidence, inference stays unresolved.

Generic bounds may themselves provide projections. For a parameter `T: Reader<String>`, the
parameter's bound is traversed after its enclosing fixed substitution. Every inline and `where`
bound participates. Constraint satisfaction remains separate from inference and is checked after
the substitution is complete.

## Common input types

When several input arguments contribute lower bounds, the solver considers the constructed nominal
and constraint graph reachable from the bounds. It selects a type only if all lower bounds convert
to it, it satisfies every expected-result bound, and there is exactly one most-specific candidate.
Thus two values with a unique shared base can infer that base. Two equally suitable unrelated
interfaces leave the inference ambiguous.

This process does not add numeric widening. For example, an already typed `byte` variable and an
`int` variable do not infer a common numeric target merely because a machine instruction could
convert one representation. Numeric variables retain Sol's explicit conversion rules. Literal
values retain their original lexical value and are checked against the specialized target after
inference.

Boxing is considered only where intrinsic conversion and the generic constraints permit it. A
ref-like argument cannot infer a heap `Object` substitution by boxing. `T: Object` does not accept a
struct merely because a regular value conversion to Object exists.

## Call mapping and generic members

Argument mapping runs before inference and supplies the formal index for each source argument. A
vararg element contributes evidence against the element type; a spread array is checked as an array
before its element type is used. A named vararg array must satisfy the invariant element identity.
Empty varargs and empty array literals supply no element evidence.

Defaults do not infer a type parameter. They are evaluated at the call site under the selected
substitution after explicit arguments. An `out var` is typed after a unique candidate has been
selected, so it cannot be used as speculative evidence that would decide which candidate wins.

For a method on `Store<T>`, receiver substitution resolves the owner `T` before method inference.
A method's type parameter with the same spelling remains independent. For a constructor, the
constructed nominal arguments and constructor method arguments (if any) are validated against their
own constraints.

## Lambda, array, literal, and effect evidence

Typed lambda parameters may provide evidence when their full function shape is known. An untyped
lambda must wait for a candidate's expected function type. Positional parameters such as `$0` have
no standalone type and cannot choose an overload by guessing. If candidate-specific contextual
typing is incomplete, the candidate remains deferred.

Array literals use an expected element type or a unique element common type. `[]` does not become an
arbitrary array type. A non-empty literal must check each element against the one selected element
type; it cannot infer a different type per element.

An uncontextualized integer or floating literal contributes the default source type `int` or
`double`. The exact literal value is then checked for representability against every candidate's
target. Literal fitting does not permit an already typed numeric variable to widen implicitly.
Binary128 literal rounding remains separately deferred.

Function effect sets are unordered. The candidate binder can infer multiple open effect patterns
when fixed effects match and there is exactly one complete one-to-one mapping that satisfies all
generic bounds and constraints. It never pairs members by source position. Matching a constructed
effect can expose another function-type effect set; the solver recursively matches that set under
the same state budget. No mapping, multiple valid mappings, or incomplete evidence remains deferred
or inapplicable as appropriate. Declared effects are substituted after successful type inference
and checked by the separate effect analysis.

Generic operator candidates use the same structural solver for operand evidence and a surrounding
expected result. A return-only type parameter can be inferred from an expected type, with the
operator's declared constraints checked before selection. Missing or contradictory evidence does
not become an arbitrary type. This does not make operator bodies eligible for FIR/native lowering.

## Determinism and resource limits

The structural matcher has a depth limit of 128. One constructed-base projection or common-type
closure visits at most 4096 unique types. A single type parameter accepts no more than 256 distinct
bounds. Candidate-local lambda reanalysis uses at most 128 fixed-point passes, while each candidate
analysis rolls back its maps, diagnostics, and flow state. `TypeSpecialization` independently limits
its workspace to 100,000 new interned identities.
Limit failures produce explicit deferred/resource results; they are not interpreted as empty bounds
or a successful fallback to Object.

Every traversal observes cancellation. `OperationCanceledException` propagates, and no partial
substitution is published as a successful binding. Type identities are owned by the current
`TypeSpecialization`; IDs from another analysis cannot be inserted by numeric equality.

Results are deterministic for the same source set regardless of input-file enumeration order.
Sorted candidate output is for inspection only. It never breaks an inference or overload tie.

## Current implementation boundary

The body call binder now uses the shared structural solver for generic member and direct call
candidates. It supports input, exact ref/out, expected-result bounds for written returns,
invariant nested applications, constructed base projections, non-empty array literal evidence, and
candidate-local lambda parameter/result evidence. Lambda candidates are replayed against updated
substitutions until the inference map stabilizes or reaches its 128-pass budget. Multiple open
function-effect patterns can be inferred when bounded unordered matching proves one complete
mapping. The matcher recursively solves nested function-effect sets reached through constructed
effect identities; ambiguity and incomplete nested evidence remain deferred. Constraints are
evaluated for both generic methods and constructed type owners. A missing parameter marks the call
deferred; a conflicting/incompatible candidate is rejected so another independently valid overload
can remain.

The direct `CallAnalysis` SDK has no enclosing expression-context parameter. It can use argument
evidence but cannot infer a method type from an expected result outside the call. It reports that
case as deferred when argument evidence is insufficient.

Not yet implemented: every lambda/array-literal shape and complete cross-argument contextual
inference, all cross-lambda fixed-point interactions, full common-interface/boxing interactions,
generic body rechecking for each monomorphized substitution, user-defined conversion inference,
checked-effect coverage, safe-call lowering, and complete flow and borrow verification. A bounded FIR/native slice
now specializes closed module-level generic free functions over admitted primitive, String, and
single-level array ABI types. It does not make the complete generic contract executable. The open
work is also listed in
[generic calls](generic-calls.md) and the [compiler status](status.md).

## Candidate-local lambda replay

The call binder resolves and maps each overload candidate independently. It first applies the
constructed receiver and explicit generic arguments, then collects facts from arguments whose types
do not depend on an expected lambda type. This first solve can close some parameters and reject a
candidate before the compiler analyzes any lambda body.

If generic parameters remain open, the binder inspects each mapped lambda in a temporary analysis
transaction. It substitutes parameters already known for that candidate and preserves unresolved
parameter identities in the callback shape. The transaction captures expression and call maps,
body summaries, local type overrides, active recursion markers, reported-diagnostic keys, deferred
nodes, diagnostic count, and body nesting depth. A `finally` restoration runs even when nested
analysis throws or is canceled.

The transaction starts from the caller's current state for every candidate. A lambda assignment,
narrowing, exception effect, or synthetic local discovered while inspecting one overload is not
visible to a sibling overload. The selected candidate is analyzed again after ranking; only that
analysis publishes ordinary body facts.

Lambda evidence can come from an explicit parameter type, an independently inferred body result,
or a supported effect-set residual. Contextual typing alone is not evidence. In particular, the
unannotated parameter of `(T) -> T` and an identity body `{ $0 }` cannot solve T by returning the
same open T that was supplied as context.

### Fixed-point passes

All mapped lambdas in one pass see the same substitution snapshot. Their evidence is accumulated as
a batch and solved after the pass. A new solution is then available to all lambdas in the next
pass. This makes inference independent of the order in which source arguments or candidate objects
are enumerated.

The pass repeats while the substitution changes or the ordered set of unresolved parameters
changes. If the map and open set remain unchanged, the candidate has reached an unresolved fixed
point and is deferred. It does not pick the first evidence fact or assign a fallback type.

The current cap is 128 complete passes per candidate. This is separate from the structural depth
limit, graph node limit, type-parameter fact limit, and body-expression depth limit. Exceeding the
pass cap reports the resource diagnostic at the call and keeps it in `UnverifiedNodes`. It does not
return the last partial map as solved.

```sol
module docs.generic.lambda_replay

func <T> combine(first: (T) -> T, second: (T) -> T): T = second(first(default<T>()))

func valid(): String = combine(
    { value: String -> value },
    { value -> value }
)
```

The typed parameter of `first` contributes exact evidence for T. `second` receives String on the
next replay and its untyped parameter is contextually checked. It does not contribute an exact
String fact merely because the context supplied String.

```sol
module docs.generic.lambda_conflict

func conflicting() = combine(
    { value: String -> value },
    { value: int -> value }
)
```

This candidate has contradictory exact parameter facts. The compiler rejects it; it does not use a
common Object result to make the parameter annotations compatible.

Candidate outcomes remain independent. If one overload has a closed valid lambda context and
another has contradictory exact lambda parameters, only the latter is rejected. If the second
candidate is merely missing an independent bound and could outrank the first, the entire call stays
deferred.

## Array literal evidence path

The call mapper decides whether an array syntax node is an ordinary array argument, fixed-array
argument, positional vararg element, named array-valued vararg, or spread argument. That decision is
fixed for one candidate before generic solving. Array evidence cannot rewrite the mapping after a
different parameter has closed.

For an element-shaped formal such as `[]T`, a non-empty literal contributes one input fact for each
element. The solver selects one T satisfying all elements. Nested array syntax is matched
recursively against the nested formal. A fixed array checks its literal count before gathering any
element facts, so an invalid length cannot produce a partial generic solution.

When the formal itself is an open parameter T, a non-empty array literal first needs one unique
standalone element common type E. The constructed `[]E` is then the actual type used as evidence for
T. This differs from inferring the element of a formal `[]T`. If two unrelated interfaces are
equally good element candidates, the standalone array type is ambiguous.

```sol
module docs.generic.array_evidence

func <T> consume(values: []T): T = values[0]
func <T> pass(value: T): T = value

func examples(): () {
    val element = consume(["a", "b"])
    val wholeArray = pass(["a", "b"])
}
```

The first call infers the formal element parameter T from each element. The second call infers the
whole value parameter T as an array type after finding a unique element type. These facts have
different structural paths and retain different origins.

An empty literal contributes no element evidence. It is valid only after another source closes the
element type: an explicit type argument, expected result, independent argument, or previously
closed candidate substitution. `[]` does not mean `[]Object`, bottom, or a process-global empty
array.

Numeric literal tokens keep their exact spelling/value until target selection. Contextual
representability can allow an integer token in a smaller numeric element type. A typed numeric local
keeps its declared type and cannot be widened just because another element is a literal. No
intermediate parse overflow may wrap the literal before the representability check.

Named and spread array forms use array-level mapping and preserve invariant element identity as
required by their formal. They do not inspect a variable's runtime contents. Omitted varargs and
empty arrays have distinct source mappings and both contribute no element facts.

## Supported effect-set evidence

Function effect sets have set semantics. Their source display order is not a type relation. The
solver cannot zip formal and actual effect arrays by index, because reordering either list would
change the inferred exception.

The current implementation performs bounded unordered matching for all open top-level patterns in
the callback effect set. It removes exact fixed identities, explores complete one-to-one mappings,
and accepts a result only when one full mapping survives structural matching and owner constraints.
Closed independent constraints prune invalid partial assignments. The set-member and visited-state
caps are 256 and 4096; reaching either produces a resource diagnostic rather than a guessed mapping.
When matching a constructed effect exposes another function-type effect set, the solver recursively
matches that set with the same root search budget. Nested mappings that remain ambiguous or lack
required evidence stay deferred.

| Formal effects | Actual effects | Current result |
|---|---|---|
| `{IOException}` | `{IOException}` | no open parameter; exact set match |
| `{E}` | `{IOException}` | infer E as IOException |
| `{IOException, E}` | `{IOException, TimeoutException}` | infer E as TimeoutException |
| `{IOException, E}` | `{TimeoutException, IOException}` | same result; order is irrelevant |
| `{ReadFailure<T>, NetworkFailure<U>}` | `{ReadFailure<String>, NetworkFailure<int>}` | infer T=String, U=int; only one complete mapping |
| `{E, F}` | `{IOException, TimeoutException}` | ambiguous; both complete mappings remain valid |
| `{IOException, E}` | `{TimeoutException}` | fixed effect missing; reject effect match |
| `{E}` | `{IOException, TimeoutException}` | cardinality mismatch; unresolved/inapplicable |
| `{IOException}` | `{TimeoutException}` | no exact set match |

For multiple open effects, the binder searches complete one-to-one assignments under constructed
generic effect identities and accepts the candidate only when exactly one assignment satisfies all
available bounds and owner constraints. The row above remains ambiguous because both permutations
are valid. A nested set exposed by a constructed effect uses the same recursive matcher; it cannot
resolve an outer tie by source order. No result changes with source order.

Checked effect inference is not checked-effect verification. After substitution, the call binder
still validates the resulting declared effect set against the callable budget. Unchecked
`RuntimeException` subtypes follow the exception hierarchy and do not become checked evidence.

## Applicability and incomplete candidate table

| Condition | Candidate state | Call resolver behavior |
|---|---|---|
| all type arguments close and constraints hold | applicable candidate | rank it against complete candidates |
| one required type parameter has no fact | deferred candidate | retain if it could outrank another |
| independent argument facts conflict | rejected candidate | continue with sibling candidates |
| exact ref/out type differs | rejected candidate | continue with sibling candidates |
| nested invariant shape differs | rejected candidate | continue with sibling candidates |
| lambda has wrong arity or mode | rejected candidate | continue with sibling candidates |
| lambda produces candidate-specific body diagnostic | rejected candidate | discard speculative state |
| array literal has no unique common type | deferred/rejected by shape rule | preserve source literal origin |
| constraint check is indeterminate | deferred candidate | do not assume constraint success |
| candidate exceeds a bounded budget | incomplete candidate | do not claim overload set complete |
| user cancels analysis | canceled operation | restore transactions and propagate |

An applicable candidate is selectable only if every incomplete candidate is proven unable to be
better under the overload ordering. The solver cannot use an unresolved parameter as a conversion
rank or assume that missing evidence is a worse conversion. Sorting candidates for display is never
the tie breaker.

## Resource limits and observable boundaries

The implementation keeps independent bounds for distinct work units. The current generic solver
uses a 128-level structural recursion limit, at most 4096 graph nodes in a projection/common-type
closure, and at most 256 distinct facts per type parameter. Lambda fixed-point replay has a 128-pass
limit. Member lookup has its own path-state and per-candidate route limits. Type interning has a
separate workspace limit.

Each limit reports the source operation that attempted the bounded work. The analysis result is
incomplete and retains the relevant node in `UnverifiedNodes`. A limit is not converted to “no such
type,” “no such member,” empty evidence, Object, or a successful fallback. Cancellation remains
distinct from all source and resource diagnostics.

The evidence ledger, type IDs, candidate transaction snapshots, and fixed-point round numbers are
internal implementation facts. Public SDK consumers receive supported candidate outcomes, selected
source bindings, diagnostics, and completeness state. A serialized ledger format is not currently a
stable public API.

### Evidence is not execution

Candidate inference reads declared types and source structure. It can inspect literal values,
argument names, generic base projections, and lambda body type facts. It does not execute a getter,
run a default, allocate a collection, invoke a closure, start a thread, or observe a mutable array's
contents. Runtime behavior is a later verified program phase.

For example, a variable typed as `[]String` contributes the declared array type, not its current
elements. A property with a getter contributes its declared/specialized result only if the property
operation is available to the candidate. The getter's effect contract is checked separately; the
getter is not called merely to learn a generic argument.

### Parameter ownership and substitution order

The inference map is keyed by the owner of each generic parameter. A class parameter T and a method
parameter T are different entries even if both names are identical. A candidate starts with receiver
substitution, applies explicit method arguments, then collects facts for the method parameters that
remain open.

| Parameter source | Owner | Inference treatment |
|---|---|---|
| `Store<T>` receiver | constructed declaring type | fixed receiver substitution |
| `func <T> map(...)` | method declaration | candidate solver parameter |
| explicit `map<String>(...)` | method declaration | fixed explicit argument |
| constructor `Box<T>(...)` | constructed type declaration | constructed owner inference |
| inherited `Base<T>.read()` | declaring base owner path | specialized along each base edge |
| lambda-local type parameter | lambda declaration, if grammar permits | never aliases outer T by spelling |

When a generic member is inherited through `Child<String> -> Base<T>`, the resolver first
constructs `Base<String>`. Only then does method inference examine the method's own open parameters.
This prevents a same-spelling method parameter from being overwritten by a receiver substitution.

### Common-type selection details

For input/lower facts, the candidate set is a bounded closure over source-declared nominal bases,
interface bounds, and permitted nullable relationships. It is not the set of all types in the
program. A type C can be selected only when each lower input converts to C and every expected
constraint accepts C. Among valid candidates there must be exactly one most-specific type.

| Inputs | Possible closure | Result condition |
|---|---|---|
| same constructed type | that identity and allowed supertypes | exact same identity is unique minimum |
| derived class + base class | shared declared base chain | nearest common legal base is unique |
| classes share one interface | the interface and its parents | interface is unique minimum |
| classes share two unrelated interfaces | both interface candidates | unresolved unless another fact removes one |
| byte local + int local | graph only, no numeric widening edge | no numeric common type from machine widening |
| string literal + String local | literal context and String identity | check literal against String |
| nullable String + String | nullable closure | choose nullable String only if rule permits |
| ref-like value + Object | conversion may require boxing | reject/defer under ref-like storage rule |
| two invariant `Box<T>` values | exact Box argument identity | do not infer a covariant Box base argument |

The implementation deduplicates identical constructed identities before candidate selection. If the
same type is reachable through two paths, it is one type candidate for generic common-type purposes;
member lookup separately preserves both paths where route-sensitive hiding/override rules require
them.

### Conflicts and recovery ownership

The solver reports conflicting parameter identities rather than throwing away all evidence. Its
caller rejects the current overload candidate and continues evaluating other candidates. A nested
shape failure, evidence conflict, unsatisfied bound, and missing evidence have different outcomes:

| Failure | Solver/candidate effect | May another overload win? |
|---|---|---:|
| no fact for a required parameter | missing evidence; candidate deferred | yes, if deferred cannot outrank or is resolved |
| exact bounds disagree | conflicting evidence; candidate rejected | yes |
| lower bounds have no unique common type | conflicting/ambiguous solution | yes |
| nested invariant generic constructor differs | incompatible shape; candidate rejected | yes |
| constraint known false after solve | candidate rejected | yes |
| constraint cannot yet be proven | candidate deferred | yes, subject to rank completeness |
| lambda body has candidate-specific diagnostic | candidate rejected after transaction restore | yes |
| one callback remains under-typed | missing or shape failure | only according to candidate completeness |
| any inference budget exceeded | candidate set incomplete | do not claim a unique winner |
| analysis canceled | operation canceled | no source-level recovery result |

A known applicable candidate does not automatically beat a deferred candidate. Overload ranking
must establish that the incomplete candidate cannot be better before publishing the known
candidate. This rule prevents a bounded inference pass from changing program meaning merely because
one overload was cheaper to analyze.

### Source origin and diagnostics

Every source fact keeps an origin label internal to analysis: ordinary argument index, array
element path, lambda parameter index, lambda result, effect residual, expected result, or exact
storage. Candidate diagnostics may summarize these origins in deterministic order and cap the
displayed count. Display truncation does not remove facts from the solver.

When a source expression has a parser recovery node, the binder preserves that diagnostic and marks
the affected call unverified. It does not treat the missing type as an empty evidence set that could
make another candidate appear uniquely applicable. Error recovery must not become successful
generic inference.

An `out var` is created with the selected formal type only after one candidate wins. Before that
point, the local has no candidate-neutral type. This avoids selecting an overload based on a type
that is itself produced by the selected overload.

### Scope of the current SDK surface

`SourceAnalysis` owns declarations, written types, and lexical scopes. `BodyAnalysis` owns the
current bounded expression/call facts and completeness markers. `GenericInferenceSolver` is an
internal analysis service. There is no public `solc` command that prints a solver ledger, no stable
generic-instantiation object for downstream binaries, and no complete FIR specialization API.
`FirCompilation` internally admits a bounded closed free-function specialization slice, subject to
its separate native verifier and ABI gate.

Direct `CallAnalysis` does not receive an enclosing expression's expected result. Its candidate
inputs are therefore narrower than `BodyAnalysis` call sites where the parent expression supplies
an expected type. The two APIs share the structural solver, but their evidence surface is not
identical. Consumers must inspect the API-specific completion result instead of assuming that
successful source declaration analysis means every body call is bound.

The contracts in `spec/GenericInference.*.sol` are normative design inputs. They deliberately
include resource, ambiguity, capture, effects, and array cases beyond the present implementation.
Each contract's status comment identifies its current coverage boundary. The examples are parsed as
source fixtures but are not all semantically executable today.

### Candidate replay checklist

For a candidate with at least one open parameter, the current binder follows this order:

1. Keep the original receiver/member target and source argument map.
2. Preserve every fixed receiver and explicit argument substitution.
3. Solve available argument, expected-result, and array-element evidence.
4. Snapshot the substitution and unresolved parameter sequence for the round.
5. Replay each mapped lambda under the round-entry map, restoring analysis tables after each one.
6. Add only parameter/result/effect facts that are independent of the current open identities.
7. Solve the current open set with the full fixed map and accumulated fact set.
8. Stop on a closed map, no progress, a conflict/shape failure, cancellation, or a bounded outcome.
9. Repeat from step 4 only if the solution made progress and parameters remain open.
10. After a unique overload wins, analyze the selected lambda bodies normally under the closed type.

The pass counter and solver depth counter measure different work. A lambda can contain a deep
nested generic expression without causing 128 inference passes, and many independent callbacks can
be analyzed in one pass without increasing recursion depth. Both remain bounded by their own
budgets. The type-specialization workspace limit is another independent cap.

| Stop condition | Candidate status | Why another pass is or is not useful |
|---|---|---|
| every parameter is closed | ready for constraints | no expected lambda shape remains open |
| map changed and some parameters remain open | replay | new closed types can contextualize another lambda |
| map unchanged and open set unchanged | deferred | no new context can be derived from the same facts |
| exact/lower/upper facts conflict | rejected | replay cannot repair a proven contradiction |
| invariant constructor shape fails | rejected | conversions cannot alter generic constructor identity |
| lambda body emitted a candidate diagnostic | rejected | that candidate's contextual body is invalid |
| replay counter reaches 128 with progress | resource-limited | bounded analysis cannot claim convergence |
| cancellation requested | canceled | restore transaction state, then propagate |

The implementation records whether each replay emitted a diagnostic before restoring the diagnostic
buffer. That boolean is candidate-local. It does not preserve speculative diagnostic objects in the
shared result list. The selected candidate's later normal analysis emits its real diagnostics at the
source expression.

The call's final `BodyCallBinding` contains the selected specialized signature and argument map.
It does not currently expose each candidate's complete evidence ledger or replay trace. Internal
candidate outcomes may be expanded later for IDE diagnostics, but the current implementation does
not promise a serialized candidate-debugging schema.
