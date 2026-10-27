// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative bounded lambda-evidence fixed point; BodyAnalysis replays at most 128 passes.
module source.spec.generic_inference.lambda_fixed_point

// Candidate-local lambda-evidence fixed point
// ============================================
//
// This contract defines how generic call inference combines several context-dependent lambdas.
// One lambda can close a type parameter that provides the expected type for another lambda. The
// compiler must account for that dependency without making lambda results depend on source order.
//
// Vocabulary
// ----------
//
// A candidate is one callable declaration with a concrete receiver path and one source argument
// mapping. Its method parameters and receiver parameters have distinct declaration identities.
// Its substitution maps only those identities; matching a printed name is never sufficient.
//
// `fixed` contains receiver, explicit, and already inferred type arguments. `open` contains the
// still-unsolved generic parameter identities for this candidate. `ordinary facts` are bounds from
// independently typed values, ref/out locations, expected result, and array literals. `lambda
// facts` are bounds established by contextual analysis of one lambda under one candidate function
// shape. `round` is one pass over the mapped lambdas using the same substitution snapshot.
//
// A fixed point is reached when another complete round adds no substitution and leaves the same
// ordered set of open parameters. A closed substitution is a fixed point with no open parameters.
// Reaching a fixed point does not imply applicability until all constraints and conversions pass.

// Candidate setup
// ---------------
//
// 1. Resolve the target and collect its complete visible declaration group.
// 2. Map positional, named, default, vararg, spread, and trailing-lambda arguments for each target.
// 3. Reject a candidate whose mapping, function arity, ref/out mode, or explicit type-argument
//    count is invalid. A rejected candidate contributes no evidence to a sibling candidate.
// 4. Apply receiver and explicit arguments to a fresh candidate substitution.
// 5. Collect ordinary argument, expected-result, and supported array facts.
// 6. Solve those facts once before entering any lambda body.
// 7. Reject a candidate with conflicting evidence or an incompatible invariant shape.
// 8. Begin lambda replay only if one or more candidate-owned parameters remain open.
//
// The source argument map is immutable during inference. Reordering the internal candidate array
// cannot change which source argument supplies evidence to a formal parameter.

// Round snapshot
// --------------
//
// Each round records the ordered open set and the complete substitution before it visits lambdas.
// Every lambda analysis in that round reads that same entry map. Evidence discovered by the first
// lambda is not visible to the second lambda until the round has finished and the solver has run.
// This batch rule prevents accidental dependence on argument enumeration order.
//
// For each mapped lambda:
//
// - substitute all closed parameters in its formal function type;
// - preserve open parameter identities symbolically in the remaining parameter, result, and effect
//   positions;
// - verify lambda arity and passing modes before checking the body;
// - analyze the body in a private flow/effect/diagnostic transaction;
// - record only independent parameter, result, and supported effect facts;
// - discard all speculative expression, call, body, local-type, recursion, diagnostic, and flow
//   records after extracting the immutable type/evidence result.
//
// Candidate speculation does not execute runtime code. It does not publish a getter call, closure,
// array, assignment, out variable, or selected nested call into the final `BodyAnalysis` tables.

// Evidence added by one round
// ---------------------------
//
// An explicit lambda parameter annotation yields exact evidence for the corresponding open formal
// parameter type. Value/ref/out modes must match. An unannotated parameter receives the substituted
// expected type, but cannot use that same expected type as independent evidence.
//
// A body result contributes an input/lower bound only when the result type is independently known
// and no longer contains a parameter in the current open set. A result that still contains open T
// cannot close T by self-reference. A body with incompatible normal-result branches rejects the
// candidate; the solver cannot hide a body error by choosing whichever branch has a convenient
// conversion.
//
// The lambda's checked effects are an unordered set. Multiple open effect patterns may be inferred
// when all fixed effects match exactly and one complete assignment satisfies every structural
// relation and owner constraint. Ambiguous mappings, nested function-type sets, and unmatched fixed
// effects contribute no guessed bound. They remain unresolved or reject the candidate according to
// final effect applicability.
//
// Evidence facts preserve their source argument index, lambda parameter/result/effect origin, and
// bound relation. Replaying a source lambda does not overwrite ordinary facts or facts from a
// previous round. The solver deduplicates identical type/relation facts while preserving distinct
// origins for conflict explanation.

// Solver transition
// -----------------
//
// Let S be the candidate substitution at round entry, O the open parameter set, and E the ordered
// evidence accumulated before the round. Analyze every eligible lambda against S and collect L.
// Run the structural solver on O, S, and E union L. The resulting substitution S' must extend S:
//
// - every parameter already closed by S has the same type in S';
// - no closed parameter becomes open again;
// - a parameter can become closed only from a unique supported solution;
// - a conflict never chooses a winner by source order, type ID, or declaration order;
// - an incompatible nested shape never becomes an implicit conversion.
//
// If S' is solved, proceed to final candidate checks. If S' is missing evidence but S' differs from
// S, run another complete round using S'. If the map and open set are unchanged, the candidate has
// reached an unresolved fixed point and is deferred. If the final permitted round still changes
// the map, report the inference resource limit and keep the call unverified.

// Termination and resource behavior
// ----------------------------------
//
// The implementation may perform no more than 128 complete lambda-evidence rounds per candidate.
// This cap is independent of the solver's 128-level structural depth cap, 4096-node graph cap, and
// 256 distinct-facts-per-parameter cap. A nested expression analysis also obeys its own body-depth
// budget. These bounds compose; none is replaced by a larger implicit retry loop.
//
// Cancellation is checked before every round, lambda, nested expression, solver pass, and graph
// expansion. Cancellation propagates after speculative state is restored. It is not a source error
// and does not leave a partially published candidate.
//
// If a resource cap is reached, the resolver returns a bounded/incomplete outcome. It must not
// report “no applicable overload” as if all candidates were examined, use Object as a fallback,
// stop after the first overload, or keep an earlier partial substitution as successful.

// Round outcome table
// -------------------
//
// | Entry map | New facts | Solver result | Required action |
// |---|---|---|---|
// | no open parameters | none | already closed | Skip lambda inference; validate the closed formal |
// | open T | exact T=String | solved | Continue with String |
// | open T | lower String | solved | Continue with the unique supported solution |
// | open T | no independent fact | missing | Defer if no progress is possible |
// | open T | T=String and T=Int exact | conflict | Reject this candidate |
// | open T | `Box<T>` versus incompatible Box shape | shape failure | Reject this candidate |
// | open T | one effect pattern, one unmatched actual | solved | Close that effect parameter |
// | open E,F | two symmetric patterns, two actual effects | multiple complete mappings | Keep unresolved |
// | open E,F | two constructed patterns, one valid assignment | unique complete mapping | Close both parameters |
// | open T | new lambda body diagnostic | invalid candidate | Restore all speculative state |
// | open T | substitution changed | missing remains | Start another whole round |
// | open T | substitution unchanged | open set unchanged | Defer at the fixed point |
// | open T | 128th round still changes map | resource limit | Report SOL2290 and defer |
// | any | cancellation | canceled | Restore state and propagate cancellation |

// Cross-lambda dependency example
// -------------------------------
//
//     func <T> combine(first: (T) -> T, second: (T) -> T): T = second(first(default<T>()))
//
// A call with two explicitly typed lambdas may close T from either lambda. Both are analyzed in
// the first round against the same open-T snapshot. If the first body returns a value whose type
// closes T, the second body receives that type on the next complete round. Reversing the source
// argument order cannot produce a different substitution.
//
//     func use(): String = combine(
//         { value: String -> value },
//         { value -> value }
//     )
//
// The first lambda supplies exact parameter and result evidence. The second is then replayed with
// a String parameter. It adds no self-evidence. The call is applicable only after both final lambda
// bodies conform to `(String) -> String`.
//
//     func inconsistent() = combine(
//         { value: String -> value },
//         { value: int -> value }
//     )
//
// Exact String and int facts conflict. The call does not choose the first lambda, accept a common
// Object conversion, or reinterpret the typed parameter annotations as independent overloads.

// Cross-round effect example
// --------------------------
//
//     func <E: Throwable> run(action: () -> () throws E, fallback: (E) -> ()): ()
//
// A lambda that throws one concrete checked exception can close E. The fallback lambda then gets
// its contextual parameter type in a later pass. If the first lambda throws two unrelated checked
// exception types, E has no unique solution unless the language's common-type rule yields one
// unique allowed Throwable subtype. A candidate may not infer E from only the first thrown branch.
//
// An unchecked RuntimeException does not create a checked-effect fact. It remains outside the
// function effect set under the checked/unchecked exception hierarchy.

// Positional and headerless lambdas
// ---------------------------------
//
// A positional lambda `{ $0 + $1 }` requires a contextual function arity. If its expected parameter
// types depend on open T, the lambda is replayed after each substitution update. The compiler does
// not introduce implicit `it`, invent an arity, or derive a type from operator spelling alone.
//
// A headerless lambda `{ 42 }` has zero parameters. Its result literal can use a closed expected
// target after inference, but the literal does not produce an arbitrary type parameter solution if
// no independent contextual or default literal rule applies.

// Named and trailing arguments
// ----------------------------
//
// Argument mapping is fixed before evidence rounds. A named lambda uses the exact mapped formal
// even when the source argument order differs from declaration order. A trailing lambda maps only
// to the final eligible functional parameter. The map cannot change between rounds as a side effect
// of newly inferred types.
//
// A defaulted functional parameter contributes no lambda evidence when omitted. Its default
// expression is not evaluated speculatively. A vararg lambda is analyzed against the mapped element
// function type; a spread value must already provide an array with a compatible invariant element
// function type.

// Final checks and selected replay
// -------------------------------
//
// After the substitution closes, check owner and method bounds, exact ref/out compatibility,
// ordinary parameter conversions, array element conversions, result conversion, and the candidate's
// declared checked-effect budget. Failure at this stage rejects only that candidate.
//
// Compare the complete applicable and potentially better deferred candidate set. If exactly one
// candidate wins, analyze its source lambdas once more in the selected body context using the final
// function signature. Publish those selected expressions, nested calls, effects, diagnostics, and
// flow changes in source evaluation order. Do not publish the speculative records from inference.
//
// If two candidates remain equally good, report ambiguity. A lambda's result type from one
// ambiguous candidate is not the source expression's final type. If another candidate remains
// potentially better but incomplete, defer selection instead of arbitrarily accepting the known
// candidate.

// Recovery matrix
// ----------------
//
// | Condition | Candidate state | Call-level consequence |
// |---|---|---|
// | One closed candidate and no competing deferred candidate | applicable | Select and replay winner |
// | Two closed candidates with equal ranks | applicable pair | Report ambiguity |
// | One closed candidate plus a potentially better deferred one | mixed | Defer the call |
// | A candidate has a lambda body error | rejected | Preserve other candidates |
// | A candidate has a lambda shape error | rejected | Preserve other candidates |
// | A candidate has conflicting exact facts | rejected | Preserve other candidates |
// | A candidate has missing evidence at a stable fixed point | deferred | Do not invent a type |
// | A candidate exhausts a fixed-point budget | resource limited | Keep analysis incomplete |
// | All candidates are rejected | none applicable | Emit the primary no-applicable diagnostic |
// | One candidate wins but selected replay fails | invalid selected body | Publish source diagnostic |

// Required invariants
// -------------------
//
// - Candidate order does not change a candidate's substitution, diagnostics, or completion state.
// - Lambda argument enumeration order does not change round-entry substitutions.
// - The substitution extends monotonically; closed type arguments are immutable.
// - An open type parameter cannot be solved by evidence that is the same open parameter.
// - Every bound keeps the source argument and sub-expression origin that created it.
// - A rejected candidate cannot mutate the source flow state used by another candidate.
// - A deferred candidate cannot be assigned a fake conversion rank.
// - Fixed-point or graph exhaustion cannot produce a complete candidate set.
// - Only the selected replay publishes ordinary body-analysis bindings.
// - Runtime evaluation occurs once, after analysis, in the language-defined source order.

// Implementation boundary
// ------------------------
//
// The current body binder performs candidate-local snapshot/restore and bounded lambda replay for
// the supported expression forms. Its fixed-point loop has a 128-pass cap. The loop is not FIR,
// monomorphized body verification, code generation, runtime closure construction, or a proof that
// every lambda grammar form has been implemented.
