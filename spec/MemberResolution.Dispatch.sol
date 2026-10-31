// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative dispatch target contract; interface/native lowering is still pending.
module source.spec.member_resolution.dispatch

// Selected declaration versus runtime dispatch
// ============================================
//
// Source lookup selects a declaration contract. Later lowering chooses the dispatch mechanism
// required by that contract: direct static target, virtual slot, interface slot, object singleton,
// closure, constructor, operator, delegate protocol, or intrinsic. A selected symbol is not itself
// executable code and does not prove that the runtime has a corresponding dispatch table.

// Static contract and override body
// ---------------------------------
//
// The statically selected declaration determines parameter names, default expressions, parameter
// passing modes, checked effect budget, and generic parameter identity. Runtime virtual dispatch
// may select a more-derived body. These contracts are connected by override validation; runtime
// dispatch cannot change caller-side argument mapping.
//
//     open class Base {
//         open func read(key: String, fallback: String = "none"): String
//     }
//
//     class Derived: Base {
//         override func read(key: String, fallback: String): String
//     }
//
//     func use(value: Base): String = value.read(key = "id")
//
// The caller uses `fallback` from Base even if the runtime object is Derived. The call site binds
// the Base signature and the dispatch slot identifies the most-derived validated override.

// Direct call
// -----------
// A direct call is permitted when the declaration is non-virtual/final or the source rule fixes an
// exact module/companion target. It preserves the receiver and arguments in source evaluation order.
// Direct dispatch does not bypass access, generic constraints, null checks, checked effects, or
// initialization rules.

// Virtual call
// ------------
// A virtual call selects the declared slot from the static receiver type. The constructed generic
// arguments and method argument tuple are part of the slot identity. A derived implementation must
// match the substituted parameter signature and valid result/effect contract. Null receiver
// handling occurs before dispatch.

// Interface dispatch
// ------------------
// An interface call uses the resolved interface slot and constructed arguments. Multiple interface
// defaults with the same unresolved signature are a declaration conflict; runtime table order is
// not a conflict resolution rule. Explicit override or an explicit implementation declaration must
// resolve the contract before code generation.

// Generic dispatch
// ----------------
// Owner arguments are fixed from the constructed receiver. Method type arguments are fixed by
// explicit syntax or candidate-local inference. Both ordered sequences are carried to the call
// target. A generic source call cannot be lowered to an erased method if the language contract
// requires monomorphized code and metadata.
//
// Reusing one machine body for two constructed type tuples requires a separate specialization
// proof. Reference representation similarity is not sufficient when nullability, ref-like
// restrictions, layouts, effects, or generic constraints differ.

// Object and companion
// --------------------
// Object access resolves the singleton object declaration and its initialization state. First
// access may trigger initialization effects. Companion access resolves the companion singleton,
// not an implicit class instance. The runtime initialization cycle/failure contract is shared with
// other object declarations and does not change ordinary lookup identity.

// Constructor and initialization target
// --------------------------------------
// Constructor overload resolution supplies the constructed owner type and selected constructor
// contract. Before publication, required fields and primary-constructor properties must be
// initialized. A constructor cannot dispatch through an uninitialized `this` to a virtual member
// that observes incomplete state. Constructor delegation is one explicit target and is checked
// against cycle/ordering rules.

// Operator and delegation targets
// -------------------------------
// User operators and `by` protocol operations store exact selected declarations in analysis
// metadata. The lowering emits the selected call plus required null checks, evaluation temporaries,
// effects, and writes. The backend does not repeat name lookup using strings.
//
// Checked operator selection, nullable lifting, `true`/`false` truth contracts, compound assignment,
// and property descriptor dispatch retain their source-level pairing rules. A backend cannot
// select an alternate overload after code generation has begun.

// Failure boundary
// ----------------
// If a source target has no supported runtime slot or lowering path, the compiler reports that
// backend limitation at the selected call. It cannot substitute a direct call, object reflection,
// CLR method, or string-based dynamic dispatch to fabricate success. `BodyAnalysis` binding remains
// separate from verified FIR and LLVM lowering.
