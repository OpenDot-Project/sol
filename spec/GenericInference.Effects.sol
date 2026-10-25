// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative generic effect inference contract; set-aware inference is pending.
module source.spec.generic_inference.effects

// Generic parameters and checked effect sets
// ==========================================
//
// A callable type contains ordered value parameter types/modes, one result type, and a set of
// checked effects. Effect ordering in source text does not carry meaning. RuntimeException types
// are unchecked and do not impose a required checked-effect budget.

// Substitution
// ------------
// When a generic method is selected, substitute owner/method type arguments into each declared
// effect type. Deduplicate equal constructed identities and retain deterministic presentation
// order. The resulting effect set participates in call-body checked exception analysis.
//
//     class Failure<T>(val payload: T): Exception("failure")
//     func <T> read(value: T): T throws Failure<T> = value
//
// The selected call's effect is `Failure<InferredT>`. It cannot be left as the open source `T` or
// replaced by base Throwable without the declaration's effect conversion rule.

// Effect inference evidence
// -------------------------
// A type parameter occurring in an effect has one of these cases:
//
// 1. It is also solved from an ordinary parameter/result position. Substitution closes the effect.
// 2. It occurs only in an expected callable effect set. A set-aware matcher may infer it when there
//    is one unique mapping between constructed effect patterns and expected effect identities.
// 3. It occurs only in the actual lambda body's effect set. Candidate-local contextual lambda
//    checking may contribute an upper/lower relation under the declared function contract.
// 4. It occurs in multiple unordered effects with more than one possible pairing. The candidate is
//    ambiguous/deferred; list order cannot decide.
//
// The current implementation handles case 1 when the variable also occurs in supported structural
// parameter/result positions. Cases 2–4 remain deferred until a dedicated unordered-set solver and
// candidate-local lambda store are available.

// Checked budgets
// ---------------
// An actual callback may have a narrower checked budget than the expected function type. A callback
// that may throw IOException cannot be assigned to a no-throws function parameter. A generic effect
// argument cannot erase an actual checked failure. RuntimeException and its subtypes are excluded
// from mandatory checked budgets but can remain in descriptive metadata if the language API asks
// for it.
//
//     class ReadFailure: Exception { }
//     func load(): String throws ReadFailure
//
//     val narrow: () -> String = { load() }
//
// This callback is invalid if `ReadFailure` escapes the lambda. A valid expected type includes
// `throws ReadFailure`, and its caller must handle or declare the effect.

// Effect constraints and overloads
// -------------------------------
// Effects do not create overload identity. Two methods differing only by `throws` cannot be
// overloaded. An override can preserve or narrow a base effect budget but cannot widen it. This
// validation occurs after the owner/method substitutions are known.
//
// Effect constraints are not a preference score. An overload with fewer declared effects is not
// automatically preferred when another candidate has a better parameter conversion vector. The
// ordinary source overload ranking runs over parameter arguments; checked effect compatibility
// filters candidates and contributes to body safety.

// Catch and rethrow
// -----------------
// Catch analysis removes only effects covered by the caught constructed exception type. A generic
// effect `E` narrowed by `is` or catch pattern carries its refined identity into the exceptional
// continuation. The generic inference solver does not prove that a caller catches E. That is a
// separate flow/effect obligation.

// Deterministic set matching
// --------------------------
// Any future effect matcher must compare canonical constructed identities without depending on
// source order. Duplicate identical effects normalize to one element. Two different projections
// that construct the same exception type are one effect; different generic arguments remain
// distinct. The matcher returns a unique map, no map, multiple maps, or an indeterminate result.
//
// Sorting exception names and zipping positions is not a semantic algorithm. A fixed sorting order
// can be used for JSON and diagnostics after the set is solved.

// Body and backend boundary
// -------------------------
// Generic effect substitution is necessary but not sufficient to prove a generic body safe. Each
// monomorphized instance must verify calls, catch regions, cleanup/defer effects, virtual dispatch,
// and lambda conversion under its concrete effect set. Until that proof exists, source call binding
// is not verified FIR and cannot authorize native EH lowering.
