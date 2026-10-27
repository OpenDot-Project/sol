// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative candidate-local callback inference; BodyAnalysis implements a bounded subset.
module source.spec.generic_inference.lambda_candidates

// Candidate-local lambda inference
// ================================
//
// A lambda is a source expression whose type depends on a call candidate. It is not a function
// value with an unknown set of ordinary type arguments. The candidate supplies a contextual
// function shape; the lambda body then contributes only facts that can be proved under that shape.
// This contract defines the order, ownership, and recovery behavior of that analysis.

// Terms
// -----
//
// `formal` is the callable parameter type after receiver substitution and any already-fixed
// explicit or ordinary-argument substitutions. `open` is the declaration-owned set of generic
// parameters that still lacks a unique solution. `candidate context` is the temporary type/flow/
// effect state used to inspect one lambda against one formal. `selected context` is the state that
// survives only after one overload has won.
//
// A candidate context is not a child lexical scope that can be merged with another candidate. It is
// an isolated analysis transaction. The same lambda source node may be visited repeatedly with
// different formal function types; node identity alone is therefore not a valid cache key.

// Required call phases
// --------------------
//
// 1. Resolve the lexical target, receiver category, visible declaration group, and constructed
//    receiver path. A lambda never turns a local shadow into a module or member qualifier.
// 2. Map all ordinary, named, default, vararg, spread, and trailing-lambda arguments to parameters.
//    A mapping failure rejects only the candidate whose signature caused that failure.
// 3. Apply receiver and explicit type arguments. Receiver-owned and method-owned parameters retain
//    distinct identities even when both declarations use the same spelling.
// 4. Collect independently typed argument facts, exact ref/out facts, expected call-result bounds,
//    array facts, and typed-lambda parameter facts. Omitted defaults provide no inference evidence.
// 5. Solve the evidence that exists before contextual lambda typing. A candidate with a conflicting
//    closed bound is rejected without evaluating any lambda body.
// 6. For a candidate with remaining open parameters, substitute all solved parameters and replay
//    each mapped lambda from the same incoming flow snapshot under that candidate's function type.
// 7. Collect lambda parameter, body result, and supported checked-effect evidence from the replay.
// 8. Solve again using the original argument evidence plus this candidate's lambda evidence.
// 9. Check constraints, final parameter conversions, result conversion, effects, ref/out storage,
//    and all candidate-specific diagnostics against the closed substitution.
// 10. Compare complete candidates. Re-evaluate the winning lambda in the selected context so that
//     its expressions, calls, diagnostics, and flow facts are published exactly once.
//
// A future implementation may pipeline or memoize these phases, but it must preserve the same
// candidate ownership and observable result. It must not execute source expressions while ranking.

// Context created for one candidate
// ---------------------------------
//
// The contextual function signature contains:
//
// - ordered parameter types after receiver and candidate substitutions;
// - one passing mode per parameter, excluding vararg expansion metadata;
// - one result type, which may temporarily contain open inference parameters;
// - the checked effect set after fixed substitution, also possibly containing open parameters.
//
// The context also carries the candidate's source argument-to-formal mapping, expected result type,
// original definite-assignment set, stable narrowed types, captured-local facts, and a bounded
// recursion/candidate budget. A candidate cannot borrow another candidate's local symbol map.
//
// If the formal is not a function type, the lambda is not a function-typed argument for that
// candidate. The compiler does not wrap it in an implicit closure to make the shape fit.
//
// If arity differs, reject the candidate before entering the body. For a named lambda, declared
// parameter count must equal formal arity. For a positional lambda, every referenced `$n` must be
// within arity and the minimum positional arity must be met. A headerless lambda has arity zero.
// There is no implicit `it` parameter.

// Parameter evidence
// ------------------
//
// A lambda parameter with an explicit type annotation contributes an exact compatibility
// constraint for the formal parameter at the same index. `ref`, `out`, and value modes also match
// exactly. An explicit lambda annotation does not create a user conversion at the boundary.
//
// An untyped named parameter receives the candidate's formal parameter type. It contributes no
// independent lower bound: using that same expected type as evidence would merely infer a generic
// parameter from itself. A positional `$n` receives the same contextual type and contributes no
// evidence by occurrence alone.
//
// If a formal parameter type still contains an open `T`, the body can use T symbolically. The
// binder may collect separate, closed evidence from a written lambda parameter or a result
// expression. It must not insert `T` as an exact self-bound and call that a solution.
//
// A typed parameter that disagrees with a fixed formal type makes that candidate inapplicable. A
// typed parameter that meets an open formal type adds an exact constraint. The constraint is checked
// again after the solver closes the parameter so that partial substitutions cannot bypass equality.

// Body result evidence
// --------------------
//
// When the formal result is closed, the body is analyzed under that expected result and every
// normal completion must produce a convertible value. When the formal result contains an open
// generic parameter, the body is analyzed without using the unresolved result as a guessed target.
// Its independently determined result contributes an input/lower constraint to the formal result
// pattern. The selected candidate later rechecks the body against the specialized result.
//
//     func <T, R> map(value: T, transform: (T) -> R): R = transform(value)
//
//     val length: int = map("road", { text: String -> text.byteLength })
//
// The ordinary argument fixes T. The typed parameter must equal the contextual String. The body
// result provides R = int. The result must still satisfy `int` as the final function and call
// result; inference itself does not prove the map body or produce executable code.
//
//     func <T, R> project(value: T, transform: (T) -> R): R = transform(value)
//
//     val same: String = project("road", { $0 })
//
// T is fixed from `value`. `$0` reads that contextual T and can solve R to the same closed identity.
// If T is still open and `$0` is the only occurrence, R receives no concrete evidence. The
// equation R = T is retained as unresolved rather than treating one open variable as a concrete
// type.
//
// Expression-body lambda result inference uses the final expression. An assignment, declaration,
// loop, or other statement-only node yields `()` when that is the block contract. An explicit
// return contributes its expression type on that return edge. `!` contributes no normal-return
// edge. Every reachable normal exit participates in the result join.
//
// A body with two result branches must have one unique common result type under the language's
// conversion rules. The compiler cannot infer from only the first branch. An unreachable branch
// contributes no value constraint but remains syntax/name checked.
//
// A body whose result depends on unavailable operator, indexer, property, or nested-lambda binding
// is incomplete. That uncertainty is candidate-local. It cannot be silently erased into `()` or
// Object.

// Function-shape relation
// -----------------------
//
// Lambda parameter count and passing modes are exact shape checks. Written lambda parameter types
// are exact against the corresponding specialized formal type. Body result types contribute only
// when independently known. Any implicit function conversion is applied after type arguments are
// solved and follows the function conversion contract; generic nominal types remain invariant.
//
// The inference relation is structural, not source-name based. It uses declaration-owner and
// ordinal identities, array/fixed-array constructors, tuple position, nullability, function modes,
// result positions, and constructed nominal projections. A type parameter named `T` in a surrounding
// method is not equal to a different method's own parameter named `T`.
//
// A function type's checked effect component is a set. The number and identity of effects are not
// inferred by zipping two arrays in parser/source order. A parameter mode is ordered by position;
// an effect set is not.

// Checked-effect evidence
// -----------------------
//
// A lambda's actual checked effects are collected from every reachable body operation after nested
// catches remove covered effects. RuntimeException and its descendants do not require a checked
// budget. An effect set on the formal function type is checked after candidate substitution.
//
// After fixed effects match, the candidate may infer multiple open effect parameters only when a
// bounded unordered search finds exactly one complete one-to-one mapping to actual checked effects.
// Matching uses exact constructed identity and the inferred arguments must satisfy Throwable and
// every declared generic constraint.
//
// If fixed effects do not match, the candidate is inapplicable. If no complete mapping exists,
// multiple mappings survive, or nested function-type effect matching is required, the candidate
// remains indeterminate. Sorting by qualified name, source order, hash order, or diagnostic order is
// never a tie-break.
//
// A lambda with no checked effects cannot infer a required effect parameter as RuntimeException,
// Throwable, or an empty type. The type parameter remains missing unless another valid source gives
// a unique effect identity.
//
// When a fixed expected function contract permits an effect, the lambda's actual effects must be
// convertible to that budget. A lambda cannot widen its expected callback contract, erase an
// IOException, or replace a failure with `throws Throwable` unless the conversion rule permits it.
// The enclosing function separately catches or declares effects escaping the selected call.

// Candidate isolation transaction
// -------------------------------
//
// The temporary analysis must restore or isolate all mutable state after each candidate:
//
// - expression type and status entries;
// - selected call/member/constructor records;
// - inferred body summaries and active recursion markers;
// - local inferred types, assignment facts, nullable narrowing, and write invalidations;
// - checked effects and cleanup/exit snapshots;
// - diagnostic de-duplication state, diagnostics, and unverified-node locations;
// - analysis depth and candidate-local generic substitution.
//
// A practical implementation can fork persistent maps or snapshot and restore private mutable
// tables. It must never truncate user-provided pre-existing diagnostics or replace an earlier
// source binding with a candidate's speculative result.
//
// Candidate exploration is not runtime evaluation. Receiver and ordinary argument expressions
// are evaluated once in source order after selection. Defaults evaluate only for omitted parameters
// after explicit arguments. Lambda bodies are not executed by inference. The selected body is
// analyzed as source; eventual runtime invocation occurs only when generated program control reaches
// the callback.
//
// Cancellation interrupts candidate replay promptly. It does not publish a partial lambda type,
// report a source mismatch, or allow a different candidate to win from an incomplete candidate set.
// A candidate or whole call whose unresolved callback could change ranking remains Deferred.

// Multiple callback arguments
// ---------------------------
//
// Each callback parameter is contextualized from the same generic map accumulated by earlier
// evidence phases. A lambda result may constrain parameters used by another callback, so the
// implementation may need a bounded solve/replay fixed-point process. Every iteration must add
// evidence monotonically and stop when no substitution changes.
//
// Replaying an unchanged lambda under an unchanged formal signature may reuse an immutable
// candidate-local result. A cache key includes the lambda node identity, expected function identity,
// generic substitution, flow snapshot identity, and analysis options. It does not include display
// names alone.
//
// If two lambdas provide `R = String` and `R = int` for one result parameter, lower-bound joining
// follows the unique most-specific common-type rule. If the function parameter occurrence imposes
// an exact relation, the exact identity must agree with that join. The candidate rejects conflicting
// evidence instead of preferring the first callback.
//
// A candidate must collect evidence from every mapped lambda before closing its type map. It may
// not infer R from one callback, publish it globally, and use the resulting R to reinterpret a
// different candidate's lambda.

// Named, default, vararg, and trailing-lambda mapping
// --------------------------------------------------
//
// The call mapper owns all source-to-formal decisions. A trailing lambda maps only to the final
// eligible function-valued parameter. Named arguments retain their formal index even when source
// order differs. A vararg element is a separate source item; a named vararg array is one array
// argument and is invariant in its element type.
//
// Defaults do not run during candidate replay. Their types and constraints are checked after a
// candidate has a closed substitution. A default expression cannot inspect a speculative lambda's
// inferred return and feed that result backward into generic inference.
//
// Explicit generic arguments fix their type parameter before callback contextualization. If an
// explicit result type is incompatible with the callback body, that candidate fails; inference does
// not replace the written type argument.
//
// `ref` and `out` callback parameters preserve their exact modes and location constraints. A
// candidate-specific lambda context cannot turn a property into storage, invent a temporary for
// `out`, or extend a ref-like capture beyond its source lifetime.

// Candidate outcomes
// ------------------
//
// The call analyzer distinguishes the following outcomes for each overload:
//
// | Outcome | Required evidence | Selection behavior |
// |---------|-------------------|--------------------|
// | Rejected | Proven shape, bound, access, conversion, or effect failure | Does not block another candidate |
// | Applicable | Closed substitution and complete contextual checks | Participates in ranking |
// | Missing | One or more parameters have no evidence | Deferred if it may change the winner |
// | Conflict | Evidence exists but no unique compatible type exists | Rejected with source origins |
// | Incompatible shape | Nested type/function/array structure cannot match | Rejected with the formal origin |
// | Resource limit | A fixed analysis budget was exceeded | No partial candidate set may win |
// | Canceled | User or owner canceled analysis | Propagates; never becomes source invalidity |
//
// A deferred candidate blocks a unique known winner whenever the unresolved conversion vector
// could be as good or better. It does not block a winner only when a proven bound makes it strictly
// worse. “Unknown” is not equivalent to a maximum conversion rank.
//
// Equal conversion vectors still follow non-vararg, fewer-default, then exact non-generic ordering.
// Function return or effect shape is not overload identity. Source order never decides a tie.

// Required positive cases
// -----------------------
//
// - infer a result parameter from a named typed lambda after an ordinary input fixes its parameter;
// - infer a result parameter from `$0` after another argument fixes the positional parameter type;
// - infer an owner parameter from a constructed receiver and a distinct method parameter from the
//   callback result;
// - use two lower results with one unique common base where the callback signature permits it;
// - infer multiple open checked effects when exactly one complete constructed-type mapping remains;
// - map a trailing lambda to a function parameter after a middle vararg without changing order;
// - use an expected call result as an independent upper bound for a generic callback result;
// - recheck the chosen lambda against its closed result and effect contract;
// - retain source origins for every parameter, body-result, and effect fact.

// Required negative cases
// -----------------------
//
// - infer from an untyped parameter by using its own unresolved contextual type;
// - infer `it` from a headerless lambda;
// - accept wrong parameter count, ref/out mode, or written parameter type;
// - infer from an omitted default or execute a default during candidate ranking;
// - treat a callback result `T` as concrete evidence for the same open T;
// - choose one of two incompatible or incomparable callback results by candidate/source order;
// - pair multiple checked effects by their source order;
// - carry a rejected candidate's nullable refinement, inferred local, or diagnostics to another;
// - discard a deferred candidate that could outrank a known candidate;
// - convert cancellation or a resource limit into successful incomplete analysis.

// Implementation boundary
// ------------------------
//
// BodyAnalysis currently performs candidate-local replay for a bounded set of generic calls whose
// unresolved type facts occur in callback parameters/results/effects. For top-level callback effect
// sets it supports bounded unordered matching when exactly one complete assignment survives. It
// also performs bounded array-literal evidence collection for call candidates. Nested function-type
// effect sets remain deferred. The direct CallAnalysis API has no enclosing flow context and remains
// narrower.
//
// These source-analysis facts do not prove generic bodies for every substitution. Complete body
// specialization, flow joins, borrow/escape proof, verified FIR, code generation, and runtime
// callback invocation remain separate implementation obligations. No `BodyAnalysis.IsComplete`
// result from this subset claims the entire Sol language specification is implemented.
