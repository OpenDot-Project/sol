// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative member storage/borrow contract; complete escape proof remains future work.
module source.spec.member_resolution.storage

// Member location and storage identity
// ====================================
//
// A member expression can denote a value, property operation, method group, ref-return location,
// array element, fixed-array slot, or indexer access. The expression category must be carried into
// assignment, ref/out, compound update, and escape checking. A value type alone does not prove that
// the expression is an addressable location.

// Local and field locations
// -------------------------
// A mutable local with an assigned exact type is a location. A field can be a location only when
// its receiver and mutability rules permit writes. An immutable val remains non-writable even if
// its referenced object is mutable. A field on an immutable value receiver may require a copy or be
// read-only according to the struct receiver contract; it cannot silently mutate a temporary.

// Property access
// ---------------
// A property invokes a getter/setter, not a raw address. `ref property` and `out property` are
// rejected. Compound update uses a stabilized receiver and invokes getter, operator, setter once.
// If the receiver or index contains a side effect, it is evaluated once and its value/location is
// retained across the read and write. This is necessary for semantics even when the property body
// itself is pure.

//     class Counter {
//         var value: int
//             get = readCounter()
//             set(next) { writeCounter(next) }
//     }
//
//     counter.value += amount
//
// This operation calls getter, addition, setter. It is not lowered as a pointer to an internal
// field unless source analysis proves that the declaration is an ordinary field rather than an
// accessor property.

// Indexer access
// --------------
// An indexer read/write is a call-like operation with index arguments and a receiver. Stable
// receiver/index temporaries preserve left-to-right order. Ref/out cannot use an indexer unless
// the declaration has a separately validated ref-return contract. An array index is a managed
// storage location only after bounds and lifetime checks have been attached to its expression.

// Ref/out argument
// ----------------
// The source markers `ref` and `out` must agree with the formal parameter mode. Ref requires an
// assigned writable location before entry. Out may begin unassigned but must be assigned on every
// normal return from the callee. The location type is exact and is never converted, boxed, copied,
// or made nullable to fit the formal.
//
// `out var name` creates a call-scoped local only after overload resolution picks one candidate. Its
// type is the selected exact formal type. The local is definitely assigned only on the normal call
// continuation. If the call throws a checked exception, exceptional flow does not assume the
// callee completed a required assignment.
//
// A property, safe member, temporary cast, numeric literal, method result, and readonly field do
// not become ref/out storage through an implicit temporary.

// Ref struct members
// ------------------
// A member whose constructed receiver or result is ref-like retains its stack/buffer provenance.
// It cannot be boxed, stored in a managed array, captured by an escaping closure, returned across a
// shorter-lived region, or passed to a generic API that lacks the required anti-constraint.
// Constrained dispatch may call a nominal interface member without creating an interface box, but
// the receiver's address and lifetime remain explicit.

// Safe calls and storage
// ---------------------
// Safe access is a conditional value computation. It cannot form a writable location because the
// receiver may be absent and there is no storage slot on the null branch. `value?.property = x`,
// `ref value?.field`, and `out value?.index[i]` are invalid. The null branch skips argument/index
// evaluation, while the non-null branch performs the ordinary operation.

// Captures and escape
// ------------------
// A lambda that captures a local location captures the language-defined cell or value according
// to val/var semantics. A captured ref/out pointer cannot escape past the caller's borrow scope.
// Generic inference of a ref-like argument does not discharge this lifetime proof. Member lookup
// knows which declaration is selected; flow/borrow analysis proves whether the reference can live.

// Required facts in a future verified FIR
// --------------------------------------
// For a member location, FIR must carry:
//
// - expression category and selected declaration/accessor;
// - constructed receiver type and dispatch kind;
// - storage identity or explicit “not storage” value category;
// - read/write mutability and getter/setter effect sets;
// - receiver/index evaluation temporaries and source order;
// - ref/out passing mode and exact storage type;
// - borrow origin, lifetime region, and escape decision;
// - nullable branch/flow fact for safe access;
// - checked effects and exceptional continuation.
//
// A BodyAnalysis `IsWritable` bit alone is not sufficient to generate a pointer or prove escape
// safety. Unsupported location categories remain unverified and cannot reach LLVM codegen.
