// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative accessor candidate selection; full indexer/ref-return lowering remains pending.
module source.spec.member_resolution.accessor_candidates

// Property and indexer accessor candidate selection
// =================================================
//
// A property declaration is a member group with operation-specific accessor candidates. Reading,
// assigning, compound-updating, initializing, and taking a ref return are different source
// operations. Lookup must retain that distinction through visibility, generic substitution,
// diagnostics, and checked effects.

// Property read
// -------------
//
// Resolve the property declaration, substitute its constructed owner, require one getter, check
// getter visibility, and return the getter's specialized result type. Add only getter effects to
// the caller. A missing getter is not a missing property if a setter exists.
//
// A read does not invoke the setter, even on a `var` property. A write-only property is not a
// readable default value. Method-group conversion is separate; a read of a method name does not
// arbitrarily choose one overload as a function value.

// Simple assignment
// -----------------
//
// Resolve one setter or init accessor permitted at that construction/use site. Check its input
// parameter against the assigned expression under the normal conversion rules. Evaluate the
// receiver and right-hand expression once according to source order. Add only setter effects.
//
// A simple assignment does not invoke the getter. If no writable accessor exists, report an
// assignment/accessor diagnostic. Do not reinterpret the property as a public base field or
// temporary storage.

// Compound assignment and increment
// ----------------------------------
//
// A compound assignment normally performs one getter/read, one binary or unary operator selection,
// and one setter/write. The receiver and index arguments are evaluated once. The old value is read
// once. The result is converted to the setter parameter once. Accessor/operator effects are added in
// actual operation order.
//
// If the getter succeeds and the operator fails, the setter is not invoked. If the setter fails,
// getter and operator side effects remain; the compiler does not promise rollback.
//
// An overloaded compound operator contract may define a direct location operation; only that
// explicit operator rule can change the sequence. The compiler cannot call both direct and expanded
// forms for one source expression.

// Independent visibility
// ----------------------
//
// A property has declaration visibility. Getter, setter, and init can have narrower effective
// access according to the accessor modifier rules. An accessor cannot widen an inaccessible
// property. An unmodified accessor inherits its property's effective visibility.
//
// A read checks only the getter used. A write checks only the setter/init used. A compound operation
// checks both. The diagnostic identifies the inaccessible operation and preserves the selected
// property identity.
//
// A private getter does not block a public setter. A private setter does not block a public getter.
// An inaccessible nearest accessor does not reopen a same-named base accessor.

// Generic owner and method substitution
// --------------------------------------
//
// A property type, getter effect, setter parameter, and setter effect all receive the constructed
// owner substitution. Accessor-local generic methods, where permitted, use independent method-owned
// parameter identities. A property name does not supply missing generic evidence.
//
// If one partial type part declares the getter and another declares the setter, partial merge first
// establishes one logical property identity. Duplicate/conflicting accessors are declaration
// errors, not source-order selection. A getter from one unrelated interface path and setter from
// another can combine when their value types agree, as specified in InterfaceImplementations.sol.
// Independent providers of the same accessor require explicit override.

// Indexers
// --------
//
// An indexer is selected from the receiver's indexer declaration group using its declared index
// parameters and operation. The receiver is evaluated once, followed by index arguments in source
// order. Named/default/vararg mapping is available only where the indexer grammar exposes those
// forms.
//
// A read selects the getter; assignment selects the setter; compound update selects both and the
// operator. Each stage specializes generic owner parameters and checks its own effect/visibility.
// An invalid index conversion rejects that indexer candidate without falling back to a property
// named `this` or to an inherited array index operation.
//
// Ref/out binding requires a declared ref-returning location with a complete lifetime and alias
// proof. Getter-plus-setter is not addressability. A temporary result or copied struct is not the
// original receiver's storage.

// Safe property/indexer access
// ----------------------------
//
// Safe access evaluates the receiver once and performs the accessor sequence only on the non-null
// path. It lifts the result to nullable when appropriate and is not writable storage. Null receiver
// skips index expressions, right-hand assignment values where the grammar defines conditional
// evaluation, defaults, and invocation according to the safe-operation contract.
//
// Visibility and generic inference are checked on the non-null constructed receiver. Safe access
// does not grant protected/private visibility or turn a nullable array into an ordinary array.

// Delegated properties
// --------------------
//
// A delegated property resolves provideDelegate during initialization and getValue/setValue on
// access. Each selected protocol method identity, constructed generic type, descriptor, and effect
// set is retained. No runtime lookup by method-name string is permitted.
//
// A delegated getter's effect appears on read; setter effect appears on write; provider effect
// appears during initialization. A failed getter does not invoke a setter, and a failed setter does
// not erase provider metadata. Descriptor identity is stable for the logical property and owner.

// Member path and candidate ranking
// ---------------------------------
//
// Property candidates are collected with all constructed base paths. Duplicate same-property
// diamonds collapse only when declaration and constructed owner identities agree. Distinct
// declarations remain ambiguous until conflict policy resolves them. Overload ranking is not used
// to choose between two independent value declarations.
//
// A hiding property can own a name on one route. A second unhidden route remains available according
// to path-sensitive name merge. Access failure is applied after effective name ownership and does
// not select a farther path as fallback.

// Checked effect composition
// --------------------------
//
// Accessor and operator effects are attached to the individual operation facts. A property read
// with IOException followed by a pure conversion still throws IOException. A write with a checked
// setter effect adds that effect even if the value conversion is pure. A compound update unions the
// selected getter, operator/conversion, and setter effects.
//
// If a later operation is unreachable because an earlier operation is bottom/throwing, it does not
// contribute a normal continuation. Cleanup effects still follow the cleanup contract.

// Operation matrix
// ----------------
//
// | Operation | Getter | Setter/init | Addressable |
// |-----------|--------|-------------|-------------|
// | read | required | not used | no |
// | simple write | not used | required | no |
// | compound update | required | required | no |
// | prefix/postfix update | required | required | no |
// | constructor initialization | initializer/init rule | selected init | no |
// | `ref property` | not sufficient | not sufficient | no |
// | `out property` | not sufficient | not sufficient | no |
// | declared ref return | selected ref getter | separate | conditionally |
// | delegated read | getValue | not used | no |
// | delegated write | not used | setValue | no |
//
// This table is source operation policy. It does not prescribe a C++ ABI slot or guarantee that
// native lowering has been implemented.

// Required positive/negative matrix
// ---------------------------------
//
// - getter-only val read and invalid write;
// - setter-only var write and invalid read;
// - public getter/private setter and private getter/public setter;
// - protected access through derived and base-typed receivers;
// - generic property on `Base<T>` through `Derived<String>`;
// - independent getter/setter checked effects;
// - compound getter/operator/setter order and no duplicate receiver evaluation;
// - indexer read/write/update with multiple index arguments;
// - partial declarations split across source files;
// - duplicate accessor declarations and interface conflict;
// - delegated provider/getter/setter selection and descriptor identity;
// - safe nullable receiver with skipped index/arguments;
// - ref/out property rejection and explicit ref-return lifetime proof;
// - cancellation/resource limit during inherited accessor lookup.

// Current implementation boundary
// -------------------------------
//
// BodyAnalysis now distinguishes property getter/setter existence, checks accessor visibility, and
// adds only getter effects on reads and setter effects on writes. Source interface operation
// selection, compound effects and index argument mapping are implemented; complete non-interface
// indexers, delegated FIR, construction proofs, and ref-return alias proof remain incomplete.
