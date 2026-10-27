// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative checked-effect candidate contract; bounded relation-aware set matching is implemented.
module source.spec.generic_inference.effect_candidates

// Generic checked effects on callback candidates
// ===============================================
//
// A function value consists of ordered parameter types and modes, one result type, and an
// unordered set of checked effect types. Generic inference must preserve the distinction between
// ordered function shape and unordered effect membership.

// Effect identity and normalization
// ----------------------------------
//
// Effect identity is a constructed type identity in the current analysis session. `Failure<int>`
// and `Failure<String>` are distinct. Two source paths that construct the same `Failure<int>` are
// one set member. A type's printed name, file, line, or source order is not identity.
//
// Before solving, substitute every closed receiver/method argument, normalize nullable/type
// identity according to the type rules, deduplicate equal constructed effects, and retain stable
// source origins separately from set membership. Presentation sorting occurs after solving only.
//
// RuntimeException and its descendants are unchecked. They do not require a checked budget. If
// metadata records them descriptively, that metadata cannot make an unchecked failure checked or
// cause an otherwise valid caller to fail effect coverage.

// Function shape
// --------------
//
// Parameter count and order are exact. Each position contains one value/ref/out mode; vararg is a
// declaration call-mapping feature, not a function-value mode. Result identity is the final ordered
// component. The checked effect set is compared separately.
//
// A generic T in a function parameter or result position contributes only the relation permitted
// for that position. A generic E in `throws E` is an effect pattern, not an extra value parameter.
// A solver cannot find E by treating the final exception as a result type.
//
// Function conversion is checked after generic substitution. A callback with a narrower actual
// effect set can satisfy a wider permitted expected set. A callback with an actual checked effect
// outside the expected set is rejected. Generic inference cannot broaden an effect contract to
// avoid that rejection.

// Set matching algorithm
// ----------------------
//
// Let P be the formal effect set after fixed substitutions and A be the actual callback's checked
// effect set. The relation determines which side must be covered:
//
// - For an actual callback passed to a formal callback, every member of A must fit one distinct
//   member of P. Extra formal effect budgets may remain unused.
// - For a generic callback result checked against an expected function type, every source pattern
//   in P must fit one distinct budget in A. Extra expected budgets may remain unused.
// - For an exact relation, both sets are complete and their normalized member counts must match.
//
// A closed pattern is checked using the corresponding source effect conversion. An open pattern
// contributes directional structural evidence. A mapping is complete only when all required
// members have one-to-one assignments and every open pattern relevant to that assignment closes
// after substitution. An unmatched open formal pattern that remains unresolved cannot be ignored
// merely because the actual set fits the other fixed budgets; inference remains missing. Duplicate
// identical effects are removed before matching, and the algorithm never uses source order.
//
// Bounded body analysis explores the complete required-side assignment. It checks nested effect
// sets, type bounds, and declared Throwable constraints before publishing evidence. If more than
// one complete mapping remains, the candidate is ambiguous/deferred even when one mapping happens
// to be visited first.
//
// If there are no actual effects for one open input pattern, that variable is missing. An empty
// actual set does not infer `E = !`, Object, Throwable, RuntimeException, or a zero-member effect
// type. A fixed broad budget may accept an actual effect without inferring an unrelated open E; that
// candidate remains incomplete until E receives independent evidence.
//
// If there are two open patterns and one actual effect, a required parameter is missing. If two open
// patterns and two actual effects can pair in multiple ways, the candidate is ambiguous/deferred.
// Structural constructors and constraints may leave only one complete mapping; only that unique map
// is accepted. Source order cannot break a tie.
//
// A pattern such as `Failure<T>` can still be open because T is unresolved even if the outer set
// contains one item. The nested generic argument is solved under the same structural matching and
// invariance rules as a value argument. Matching only the outer exception declaration loses type
// safety.

// Generic examples
// ----------------
//
//     class ReadFailure(message: String): Exception(message)
//     func <E: Throwable> execute(block: () -> String throws E): String throws E = block()
//
//     func read(): String throws ReadFailure = "value"
//     val text = execute({ read() })
//
// The callback contributes exactly one checked effect. E can be inferred as ReadFailure, subject to
// the Throwable bound. The call itself carries that effect and its caller must catch or declare it.
//
//     class DiskFailure: ReadFailure("disk")
//     class NetworkFailure: ReadFailure("network")
//
//     func readDisk(): String throws DiskFailure
//     func readNetwork(): String throws NetworkFailure
//
//     func both(): String throws DiskFailure, NetworkFailure {
//         readDisk()
//         readNetwork()
//     }
//
// A callback containing both independent effects cannot infer one E when the formal set contains
// only `throws E`. It also cannot infer `E = ReadFailure` by widening automatically unless the
// function contract explicitly applies a permitted effect-subtyping rule. The checked set remains
// the actual set and the final function conversion is checked separately.
//
//     func <E: Throwable, F: Throwable> run(block: () -> () throws E, fallback: () -> () throws F)
//
// One callback's effect cannot be paired with E or F by declaration order. Independent value
// parameters, explicit type arguments, or expected function types may close one variable first; the
// remaining set problem is then solved against the residual patterns.

// Actual lambda effect collection
// -------------------------------
//
// A lambda body contributes checked effects from calls, property getters, setters, operators,
// constructors, delegated accessors, cleanup blocks, and nested selected callbacks. Each source
// operation contributes only the effect of the accessor/target actually selected.
//
// A catch removes an effect only when its constructed catch type covers that effect. Catch order,
// reachability, pattern refinement, and rethrow identity are checked by flow analysis before the
// candidate publishes a closed effect fact.
//
// An unavailable call target, ambiguous overload, missing accessor, or unresolved nested lambda
// makes the candidate effect set incomplete. It is not the empty set. A body that cannot be proven
// effect-free cannot satisfy an empty effect budget by default.
//
// Return expressions, `throw`, and bottom `!` are orthogonal to the effect set. A checked throw has
// no normal result but adds its checked type on the exceptional edge. A caught throw does not leak
// to the outer callback unless rethrown or produced elsewhere.

// Candidate applicability
// -----------------------
//
// Effect compatibility filters candidates after argument mapping and type substitutions. It does
// not create a separate effect-only overload tie-break. Two overloads differing only by `throws`
// are declaration errors; effect order cannot distinguish them.
//
// An inaccessible protected getter or a missing member cannot be counted as an effectless
// callback. A candidate that cannot resolve the operation is incomplete. If that incompleteness
// could change the winner, the call remains Deferred.
//
// Candidate-local replay collects lambda effects with the same receiver/member/call rules used for
// ordinary body analysis. A rejected candidate discards both its function type and its effect
// facts. A selected candidate rechecks the lambda against its final effect budget and adds the
// selected call's declared effects to the enclosing body exactly once.
//
// An actual callback may have a strict subset of the expected checked effects if function-type
// conversion permits it. Extra declared effects that the lambda body never throws are not fabricated
// as actual effects; however the function value's declared effect type remains part of its static
// contract when the value is already typed.

// Ref/out and capture interaction
// -------------------------------
//
// Effect inference does not alter a callback's parameter modes. A checked effect emitted after an
// `out` assignment is still an exceptional exit; definite assignment is guaranteed only on normal
// return unless the language gives a stronger rule. Ref/out storage is invalidated/narrowed only
// according to the call and capture flow contract.
//
// A lambda cannot capture caller ref/out storage or a ref-like value if the capture may outlive its
// valid lifetime. An inferred E cannot be used to erase this restriction by turning the callback
// into a heap-safe Object or by wrapping the callback in a delegate without an explicit conversion.

// Override and ABI consequences
// -----------------------------
//
// An override may retain or narrow its base checked-effect contract; it may not introduce a new
// unrelated checked effect. This check uses owner substitutions and exact constructed effect
// identities. Generic method parameters in the override are alpha-matched by owner and ordinal.
//
// Public metadata stores the normalized effect set with the function signature. A cache, FIR node,
// callback wrapper, AOT adapter, and ORC entry all use the same closed effect identity. The native
// ABI does not transport an arbitrary CLR exception object. Sol exception identity and cleanup
// effects remain guest-side metadata and runtime values.
//
// The effect set is not a C++ exception specification and is not derived from LLVM function type
// text. The compiler emits native EH and root cleanup from verified FIR after source effect proof.

// Required positive matrix
// ------------------------
//
// - one fixed effect and one matching actual;
// - multiple open patterns with one complete constructed-type assignment after exact fixed matches;
// - nested `Failure<T>` where T is fixed by an ordinary callback result;
// - nested `Failure<T>` where T is inferred from the exact effect identity;
// - unchecked RuntimeException alongside one checked actual;
// - caught actual effect and a second uncaught effect;
// - duplicate source paths that construct the same effect type;
// - reversed declaration order with identical semantic effect sets;
// - callback with a narrower allowed effect set;
// - selected call effect added once after candidate-local replay.

// Required negative/deferred matrix
// ---------------------------------
//
// - zero actual effect for one open E;
// - two actual effects for one open E when no widening rule applies;
// - two open effect variables with multiple valid pairings;
// - nested generic argument conflict inside the effect type;
// - a checked effect not covered by the callback budget;
// - a parameter mode mismatch hidden by matching result/effect types;
// - an unresolved nested call treated as an empty effect set;
// - source-order pairing of reordered `throws` lists;
// - candidate A's caught effect reused in candidate B;
// - resource exhaustion transformed into no-effects success.

// Current boundary
// ----------------
//
// GenericInferenceSolver recognizes a unique complete unordered assignment for multiple open
// effect patterns in a function-type set. When matching a constructed effect identity recursively
// exposes another function-type effect set, that set uses the same bounded matcher and search
// budget. Ambiguous or incomplete nested mappings remain open. Complete flow/catch replay,
// separate-binary effect metadata, generic body specialization, and verified FIR effect coverage
// also remain open. A source binding is not evidence that runtime exception unwinding or native
// cleanup has been implemented.
//
// A matching attempt is bounded to 256 distinct effects and 4096 visited search states. Closed
// independent constraints may reject an assignment prefix before the full tuple is chosen. If the
// state budget is exhausted before uniqueness is proved, report SOL2290 and leave the call
// unselected; do not reinterpret exhaustion as no effects or as a unique mapping.
