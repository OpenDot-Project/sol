// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative lambda result-bound contract; complete multi-exit/body inference remains pending.
module source.spec.generic_inference.lambda_result_bounds

// Lambda result bounds and completion
// ===================================
//
// A lambda result can be an input to generic inference only after the candidate has supplied a
// function shape. This document defines which body paths produce result evidence, which paths
// produce effects only, and when the result remains indeterminate.

// Result source precedence
// ------------------------
//
// Use a written contextual function result when it is closed. Use a candidate-substituted result
// when it is closed after ordinary evidence. If that result still contains open generic parameters,
// analyze the lambda's normal completion without pretending those open parameters are concrete.
//
// A named or positional lambda header controls parameter identity, not result type. A body expression
// can provide an independent result type. A typed local, selected call, property value, literal, or
// operator result can provide that fact only if its own source type is available without guessing
// from the open lambda result.
//
// The expected *call* result is a separate inference relation. It can add an upper bound to a
// generic call result while the lambda body adds a lower bound to the callback result. Both are
// checked against the final substitution; neither silently replaces the other.

// Expression body
// ---------------
//
// The last expression of an expression-body lambda is its normal result. Parentheses preserve the
// inner expression result. A trailing semicolon follows the grammar's statement rule and must not
// be interpreted differently merely because the lambda is generic.
//
// A lambda expression `{ value: T -> value }` has the parameter's effective type T as its result.
// If T is already fixed, that result can constrain a distinct R to T. If T is still open, the
// equation R = T is not a concrete type fact. Both parameters remain open unless another source
// supplies a seed or the inference solver explicitly proves a unique equality component.
//
// Numeric and string literals are contextual expressions. If the lambda result target is open,
// retain exact literal spelling and use the source default type only when the language's literal
// rule says the expression is independently typed. Do not round a `quad` token through `double`.
//
// `null` has nullable-bottom type and cannot alone select a non-null result parameter. With a known
// nullable target it is checked against that target. A candidate cannot use `null` to infer Object
// or an arbitrary nullable nominal type.

// Block body
// ----------
//
// The block's final expression supplies a result on each reachable normal completion. Explicit
// `return expression` supplies a result on its exit edge. A block with no reachable result-producing
// exit can satisfy `!` only when every path is proven non-completing; `()` is the result of a normal
// statement-only completion when the grammar defines that block result.
//
// A local declaration or assignment as the final source statement is not the assigned value. It
// produces the block's unit-like empty tuple `()` according to the block-result rule.
//
// An `if` expression contributes each reachable branch. A switch expression contributes every
// reachable arm. A try expression contributes try and catch normal exits; `finally` contributes
// effects and cleanup but never replaces the result. A defer body contributes cleanup effects but
// not a new result edge.
//
// A branch ending in `!` contributes no normal value. It cannot force nullable lifting, Object
// conversion, or an artificial bottom result for another reachable branch.

// Result join
// -----------
//
// For a set of reachable normal result types R1..Rn, choose a unique common result C only if all
// Ri can convert to C under the source conversion rules and C is most-specific among all valid
// candidates. The result join does not add numeric variable widening, generic variance, boxing of
// ref-like values, or a type selected by branch order.
//
// For a closed expected result, each branch is checked directly against that result. This allows a
// contextual literal conversion but does not widen an already-typed numeric local.
//
// For an open generic pattern such as `Box<T>`, compare every branch structurally. `Box<Derived>`
// and `Box<Base>` conflict under invariance even if their inner values have a common base. A
// constructed base projection may expose evidence only when the nominal declaration graph proves
// that path.
//
// Nullable results preserve payload identity. A non-null String branch and a null branch can meet
// at String? if the context/pattern asks for it. This does not infer a non-null T from only null.

// Early returns and reachable normal paths
// ----------------------------------------
//
// Every explicit return is type-checked at its own source origin. The function result is the unique
// join of all reachable return and tail-expression paths. An unreachable return after `!` is
// diagnosed by control-flow policy but is not a reachable result bound.
//
// A return inside a nested lambda belongs to that nested lambda. It cannot become evidence for the
// outer lambda or containing function. A return inside `finally` is forbidden by the cleanup
// contract and cannot override the pending result.
//
// A `break` or `continue` does not produce a function result. A loop's result follows the explicit
// loop-expression rule, not the last body expression by accident.

// Nested callback results
// -----------------------
//
// An outer lambda may call a generic function with an expected result derived from the outer
// candidate. That nested call can provide a result only when its own overload candidate is complete
// and its substitution is finite. The nested call's chosen result contributes to the outer lambda
// result; a deferred nested overload makes the outer candidate incomplete.
//
// Expected context flows from the nearest enclosing operation. It does not flow backward across
// arbitrary callers, sibling branches, or an unresolved overload outside the lambda.
//
// A recursive group such as two functions that infer each other's result without a concrete seed
// is a cycle. It requires an annotation or a cycle diagnostic; repeated respecialization cannot
// generate an infinite sequence of increasingly specialized types.

// Result diagnostics and recovery
// -------------------------------
//
// A wrong closed lambda result is a candidate-specific conversion rejection. If all overloads fail
// only because of that result, retain the lambda source span and expected result in the call's
// diagnostic data. Do not report an unrelated missing receiver.
//
// A result conflict between multiple body exits identifies the incompatible exits and their
// source origins. A missing result parameter reports no evidence and remains Deferred if it could
// alter overload selection. A conversion failure is not MissingEvidence.
//
// A parser recovery node or unsupported expression does not become `()`. The candidate remains
// incomplete. Diagnostics may be capped, but the candidate's completeness bit is independent of
// visible message count.

// Result evidence matrix
// ----------------------
//
// | Body form | Formal result | Evidence/action |
// |-----------|---------------|-----------------|
// | `x` | closed R | check x converts to R |
// | `x` | open R | use x's independent type as lower evidence |
// | `$0` with closed parameter | open R | use the parameter identity as result evidence |
// | `$0` with open parameter T | open R | preserve R = T; do not call it concrete |
// | `null` | nullable closed target | check null against target |
// | `null` | open non-null R | no concrete evidence |
// | branches with one unique base | open R | contribute the unique common candidate |
// | invariant generic branch mismatch | open R | reject as incompatible shape |
// | all reachable paths throw | `!` | no normal-result evidence; collect effects |
// | one path returns, one path throws | R | result comes from normal path only |
// | only statements complete normally | `()` | empty tuple result under block rule |
// | unavailable nested call | any | Deferred; no fallback result |
//
// This table describes source semantics. It does not say every corresponding CFG/FIR rule exists.

// Ref-like result values
// ----------------------
//
// A ref-like result can be returned only where the lifetime/result contract permits it. Generic
// inference may identify the ref-like type but cannot prove that the underlying borrow remains
// alive. `allows ref struct` admits a generic argument only; it does not erase array, closure,
// property, delegate, or heap-storage restrictions.
//
// A lambda result that captures a stack value cannot be silently converted into a heap delegate.
// If callback representation/lifetime is unresolved, the candidate remains incomplete.

// Required checks
// ---------------
//
// - ordinary body result plus explicit return;
// - multiple compatible and incompatible return paths;
// - one bottom branch and one value branch;
// - one null branch and one non-null branch;
// - nullable array result versus nullable element result;
// - tuple result with independent generic positions;
// - nested generic nominal result with invariant conflict;
// - expected result upper bound conflicting with lambda lower bound;
// - overload candidate receives a different result target;
// - nested generic call with result/effect evidence;
// - recursive lambda/call cycle without a result seed;
// - ref-like result and escaping capture;
// - result inference with statement-only and empty-tuple body;
// - complete absence of normal result due to bottom/throw.

// Current implementation boundary
// -------------------------------
//
// Candidate-local body analysis can defer an unresolved contextual result and use a closed lambda
// body result as generic evidence. Full branch joins, nested-lambda cycles, universal ref-like
// return proofs, and FIR result verification remain outside the current body-analysis completeness
// claim.
