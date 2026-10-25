// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative protected callable receiver contract; complete cross-binary access remains pending.
module source.spec.member_resolution.protected_calls

// Protected access to callable members
// ====================================
//
// Protected accessibility has two independent checks: the lexical caller must be in the declaring
// type or a derived type, and a derived caller must use a permitted static receiver category. The
// declaring owner found by member lookup is not necessarily the type of the source receiver
// expression.

// Declaring owner
// ---------------
//
// Resolve the declaration's containing constructed type using the member path and owner
// substitution. A source method override may be declared on a derived type while its inherited
// signature originates in a generic base. Access checks refer to the declaration that actually
// supplies the selected member contract.
//
// Compare derived relation by constructed base edges and declaration identity. A class with the
// same printed name in another module or distribution is not a derived type. An unresolved base
// path makes protected access incomplete rather than permissive.

// Caller type
// -----------
//
// The caller type is the lexical containing type of the call/access operation, not the type of an
// enclosing lambda parameter and not an imported qualifier. A lambda declared inside a derived
// instance body uses the enclosing access context only if capture/access rules explicitly preserve
// that lexical context.
//
// A top-level function has no derived caller type. A type expression or module qualifier is not an
// implicit derived instance. An object/companion declaration follows its own nominal inheritance
// hierarchy.

// Static receiver type
// --------------------
//
// For a call `receiver.method()`, test the static type of `receiver` after a valid flow narrowing or
// safe-call non-null projection. Do not substitute the selected member's declaring type for this
// receiver. Doing so would make every inherited protected call appear to have the base type and
// would lose the derived receiver restriction.
//
// In a derived class Car extending Vehicle:
//
// - `this.inspect()` is permitted;
// - `otherCar.inspect()` is permitted when `otherCar` is statically Car or a subtype;
// - `otherVehicle.inspect()` is not permitted merely because it may hold a Car at runtime;
// - `super.inspect()` is permitted as the explicit base route;
// - `Vehicle.inspect()` is not an instance call and does not fabricate a receiver.
//
// A runtime type test can narrow a stable base-typed value to Car inside its true branch. That
// narrowed static type can satisfy the receiver rule while the proof remains valid. Mutation or
// escaping capture can invalidate the proof according to flow rules.

// Generic receiver substitution
// -----------------------------
//
// For `open class Base<T>` and `class Derived: Base<String>`, a protected member inherited from
// Base is specialized with String for signature checking. The access receiver is still the
// Derived expression type. Owner substitution and receiver accessibility are separate maps.
//
// For `class Derived<T>: Base<T>`, both maps preserve the same declaration-owned T only where the
// constructed path proves identity. A method parameter also named T remains independent.
//
// A protected method on a constrained type parameter is accessible only when the bound/member
// metadata proves the declaring owner and the effective receiver restriction. A matching member
// name on an unrelated bound is not evidence of access.

// Safe calls and member groups
// ----------------------------
//
// A safe call on a nullable receiver tests accessibility on the non-null branch and preserves the
// selected static receiver payload type. Nullability does not grant access. The receiver expression
// is evaluated once; explicit/default arguments and target invocation are skipped on the null path.
//
// The overload group is formed before candidate-specific access and generic inference. An
// inaccessible candidate is not converted into a missing member, but it also cannot win or provide
// a generic substitution. A hidden or inaccessible declaration does not expose a same-named base
// method as a fallback.
//
// Candidate-local lambda evidence cannot change receiver access. An inferred generic T in a lambda
// result does not make the call receiver more derived.

// Accessor parity
// ---------------
//
// Protected property getter and setter use the same lexical caller and static receiver rule as a
// method. A read checks the getter. A write checks the setter. A compound update checks both. A
// protected getter failure does not make a public setter inaccessible, and a protected setter
// failure does not invalidate an otherwise public read.
//
// An indexer receiver uses the source receiver type; index argument types do not affect protected
// permission. A property is never converted to ref/out storage by a derived receiver.

// Dispatch is not access
// ----------------------
//
// Access validation determines whether source code may refer to the declaration. Virtual/interface
// dispatch determines the runtime implementation after access is valid. A runtime override cannot
// grant access to a protected base method that the source receiver rule rejects.
//
// `super.method()` selects explicit base dispatch according to the language rule and cannot be
// rewritten as ordinary virtual dispatch if that would recurse. Dispatch lowering is performed only
// after the selected callable, receiver, and access proof are in verified FIR.
//
// Access checks do not inspect CLR reflection, vtable slots, native symbol visibility, or the
// eventual object's dynamic type. Those are implementation details and cannot change source
// accessibility.

// Diagnostic ownership
// --------------------
//
// - caller not in the declaring/derived hierarchy: SOL2214 on the call/member;
// - caller is derived but static receiver is too broad: SOL2214 on the operation;
// - receiver is nullable without flow/safe access: SOL2243, independently of visibility;
// - name group is ambiguous before selection: SOL2212 or SOL2222 according to operation stage;
// - lookup path is incomplete: SOL2290/deferred, never success;
// - no declaration exists after complete traversal: SOL2215;
// - a public property has inaccessible getter: getter access diagnostic on read;
// - a public property has inaccessible setter: setter access diagnostic on write.
//
// Diagnostic order is stable by source path/span/code. The first candidate in sorted output is not
// the one that determines access or source validity.

// Required matrix
// ---------------
//
// - same declaring type through this and a second same-type instance;
// - derived type through this, derived receiver, subtype receiver, base receiver, sibling receiver;
// - explicit super, generic base specialization, and two unrelated interface routes;
// - public/protected/private/internal member across one and multiple distributions;
// - inherited method, override body, overloaded method, and method hidden by property;
// - property getter, setter, init, compound assignment, increment, ref/out, and indexer;
// - nullable receiver before, inside, and after stable narrowing;
// - mutation and mutable closure invalidation between narrowing and call;
// - type expression, object expression, companion, and module-qualified call;
// - inaccessible candidate next to an applicable public overload;
// - same source compiled with a reordered source-file set.

// Current implementation boundary
// -------------------------------
//
// BodyAnalysis now checks protected callable candidates using the source access receiver separately
// from the constructed declaring owner, and checks property/accessor access in member reads/writes.
// Full indexer/ref-return behavior, distribution metadata, every flow narrowing rule, serialized
// access evidence, and runtime dispatch remain incomplete.
