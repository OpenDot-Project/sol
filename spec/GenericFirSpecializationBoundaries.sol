// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative unsupported boundaries and completion criteria.
module source.spec.generic_fir_specialization

// Unsupported boundaries, conformance fixtures, and completion criteria

// 19. Unsupported boundaries
// --------------------------
//
// The following remain unsupported until separate contracts and verifier/lowering rules exist:
//
// - generic nominal type layout or generic member, constructor, property, operator, or accessor
//   specialization;
// - specialization from an open type argument, an unconstrained caller type parameter, or a
//   runtime/reified type token;
// - passing a generic function as a value, closure conversion, indirect generic calls, and dynamic
//   or virtual dispatch that requires runtime generic selection;
// - arbitrary class/struct/object construction, field layout, interface object allocation, boxing,
//   and unboxing;
// - ref struct/byref-like instance types, heap storage of such values, capture, and general borrow
//   propagation across specialized calls;
// - nullable native values, nested arrays, fixed arrays, function values, tuples in storage, and
//   unsupported nominal representations;
// - generic C ABI exports, stable generic `.solib` exports, external generic module linking, and
//   cross-version generic code caches;
// - user-defined conversions, operators, extension dispatch, or constraint dispatch that lacks a
//   verified concrete lowering;
// - source `catch`, `finally`, `defer`, checked exception unwinding, and generic effect escape that
//   the current native runtime cannot represent;
// - reflection/reified generic metadata, runtime type tests requiring generic descriptors, and
//   generic allocation through unverified runtime helpers;
// - `half` and `quad` native specialization under the current bootstrap verifier;
// - unsupported default/vararg body lowering, even when call binding can map such arguments;
// - generic specialization in released Sol 0.0.1, which is the initial CST frontend release.
//
// A future implementation may expand this list only by updating the corresponding source,
// verifier, runtime, ABI, artifact, and conformance contracts. A successful test for one type or
// artifact does not silently generalize the supported boundary to all source types.

// 20. Conformance fixtures
// ------------------------
//
// The normative source-level scenarios and resource stress fixtures are in
// [GenericFirSpecializationLimits.sol](GenericFirSpecializationLimits.sol). The independent
// call/FIR expectation matrix is in [GenericFirSpecializationCases.sol](GenericFirSpecializationCases.sol).
// Parsing either file does not establish implementation or passing test status.

// 21. Implementation completion criteria
// ----------------------------------------
//
// This feature is implemented only when all of the following are true:
//
// 1. Closed substitution and key equality use canonical owner/type identities and pass collision,
//    nullable-normalization, owner-capture, foreign-workspace, and permutation tests.
// 2. Generic candidates enter specialization only after unique source overload selection; losing,
//    deferred, ambiguous, and rejected candidates produce no successful body instance.
// 3. Generic bodies are rebound/rechecked per substitution; no abstract-T proof is reused where the
//    concrete type changes applicability, flow, effects, storage, capture, or ABI.
// 4. The specialization worklist closes direct/mutual recursion, detects type growth through exact
//    keys, honors all independent hard budgets, propagates cancellation, and remains deterministic.
// 5. The independent verifier rejects every unresolved parameter, invalid constraint, foreign
//    identity, missing edge, unsupported ABI shape, corrupt origin, and incomplete body before
//    minting verified FIR.
// 6. The LLVM backend consumes only that verified closed graph through LLVM 23.1.3 C++ APIs and
//    emits no textual-LLVM-input compilation path.
// 7. AOT, ORC, and any admitted artifact are transactionally tested for correct runtime behavior,
//    import/link boundary, source origins, cache invalidation, failure behavior, and output stability.
// 8. Positive and negative conformance fixtures exercise the normative matrix above, including
//    constraints, overloads, named/ref/out mapping, checked effects, recursion, resource limits,
//    cancellation, and unsupported ABI types.
// 9. Public status/release documentation describes the exact verified boundary and does not call
//    generic body specialization available before the native and verifier gates pass.
//
// Passing the current nongeneric native suite, generic inference tests, FIR verifier tests, a single
// identity function, or a successful LLVM module verification is individually insufficient. The
// feature is complete only when the source-selected specialization graph is closed, independently
// verified, lowered, and validated for each claimed artifact/target boundary.
