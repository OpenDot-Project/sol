// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative accessor effects/visibility route; complete setter/indexer binding remains pending.
module source.spec.member_resolution.accessor_effects

// Accessor effects through inherited member paths
// ===============================================
//
// A property or indexer lookup can traverse generic base paths, merge partial declarations, and
// choose an operation-specific accessor. The selected getter/setter identity, constructed owner,
// access route, visibility, and checked effects must remain linked.

// Read route
// ----------
//
// For a property read, resolve receiver and member group, determine effective name ownership,
// select one property declaration, substitute its owner type arguments, locate its getter, check
// getter access, and add only its effects. Each step retains the source member token and property
// declaration symbol.
//
// An inherited getter from `Base<T>` through `Child: Base<String>` is specialized with String. The
// runtime receiver may still be Child. Generic owner substitution and virtual dispatch target are
// separate facts.
//
// If the property is delegated, the getter route includes the selected `getValue` protocol method
// and SolProperty descriptor. A property with no getter cannot be read by falling back to a field
// or invoking a setter.

// Write route
// -----------
//
// A write selects setter or permitted init accessor and its value parameter. Specialize the value
// type and thrown effects with the declaring constructed owner. Check setter visibility against the
// source access receiver. Add setter effects only after that setter is selected.
//
// A getter effect does not appear on simple assignment. A setter effect does not appear on read.
// A val with a getter is not writable merely because its result type is mutable. A var without a
// setter is not writable after declaration/constructor initialization rules are satisfied.

// Compound route
// --------------
//
// The compiler constructs an ordered operation record for read -> operator/conversion -> write.
// It stores one receiver evaluation and, for indexers, one evaluation per index expression. It
// records getter effects, selected operator effects, conversion effects, and setter effects in that
// order.
//
// If read throws, operator/write do not execute. If operator throws, write does not execute. If
// write throws, earlier side effects are retained. The language does not promise transaction
// rollback for a property update.
//
// Candidate exploration does not execute the operation sequence. It checks applicability and
// retains the selected operation plan for later verified FIR lowering.

// Effect set composition
// ----------------------
//
// Checked effects are collected from the actual selected accessor/operation. If multiple routes
// resolve to the same property declaration and constructed owner, duplicate lookup paths do not
// duplicate the effect. Distinct selected interface properties remain distinct and may cause
// ambiguity before effect composition.
//
// A catch removes only effects covered by the caught constructed exception type. If a getter's
// IOException is caught inside the enclosing expression boundary, it does not escape beyond that
// boundary. A setter's later IOException remains an effect even after an earlier getter effect was
// caught in a narrower scope.
//
// RuntimeException descendants are unchecked and do not require caller effect coverage. GC
// corruption/fail-fast process abort is not converted to a checked property exception.

// Accessor visibility route
// -------------------------
//
// First validate property declaration visibility and receiver category. Then validate the specific
// accessor modifier. An accessor may reduce exposure; it cannot widen the property. `private` and
// `protected` checks use the accessor's declaring owner but the access expression's actual static
// receiver type.
//
// An inherited declaration reached through two routes has one visibility identity if declaration
// and constructed owner match. A distinct same-name property on an unrelated route is not merged
// into its accessor list. It is a separate member conflict.
//
// If the nearest property is inaccessible, do not fall back to an accessible base getter/setter.
// The selected name owner and inaccessible operation remain explicit for diagnostics.

// Partial properties
// ------------------
//
// If the language permits accessors across partial type parts, symbol merging first creates one
// logical property identity with a deterministic accessor set. Duplicate getter/setter/init
// declarations are diagnosed. The first file found does not own the property by accident.
//
// A getter and setter from different modules or different distribution metadata identities cannot
// be joined solely by equal name. Module/type/property identity and partial rules must all match.
//
// Default accessor synthesis is explicit in the declaration model. A `val` can synthesize getter
// only. A `var` can synthesize getter and setter only if the exact language rule permits the backing
// storage. Delegate properties use their protocol accessors, not synthetic raw field storage.

// Indexer routing
// ---------------
//
// Indexer read maps index arguments, selects one getter, and checks its result. Indexer assignment
// maps indexes and the assigned value, then selects one setter. Compound access retains both
// candidate decisions under the same receiver/index snapshot.
//
// Named index arguments, defaults, or varargs apply only when supported by the indexer signature
// grammar. The ordinary method mapper cannot insert unsupported syntax into an indexer. Generic
// index type evidence and owner substitution remain candidate-local.
//
// Indexer `ref`/`out` is allowed only through an explicit ref-return contract with alias identity,
// mutability, and lifetime proof. Getter/setter pairs are not enough.

// Safe operation route
// --------------------
//
// For `receiver?.property`, resolve `property` on the non-null payload. Evaluate receiver once; on
// null, do not invoke getter and do not add getter effects. The result is nullable-lifted. A safe
// property value is not assignable storage.
//
// Safe indexer access skips index expressions on the null branch if required by the language's
// conditional evaluation rule. Safe compound update is invalid unless a precise conditional write
// contract exists. The compiler must not synthesize a setter call against a missing receiver.
//
// A safe call/property operation's static accessibility is checked against the non-null receiver
// type. Nullability is not an access bypass.

// Operation record
// ----------------
//
// The analysis record for one access contains:
//
// - source operation kind and evaluation order;
// - receiver expression/source type and narrowed non-null type;
// - selected property/indexer declaration and constructed owner;
// - all inheritance paths relevant to the selected candidate;
// - getter/setter/init symbol identities and independent access outcomes;
// - specialized value/index/result types and ref/out storage category;
// - selected delegate protocol and descriptor metadata, when present;
// - per-operation checked effects and source origins;
// - conversion/operator candidates for compound access;
// - diagnostics, resource status, and unverified proof obligations.
//
// A display string such as `getValue` is not a protocol binding. It must resolve to a declaration
// identity under the normal member/generic rules.

// Required effect matrix
// ----------------------
//
// - pure getter/pure setter;
// - checked getter/pure setter;
// - pure getter/checked setter;
// - getter and setter with different checked exception types;
// - getter catches its own effect before return;
// - compound getter/operator/setter each contribute distinct effects;
// - selected override narrows base getter effects;
// - protected getter allowed but setter denied;
// - duplicate diamond path to one getter contributes once;
// - two distinct interface getters remain ambiguous;
// - safe nullable read skips getter effect on null branch;
// - inaccessible accessor is not treated as effectless;
// - unresolved delegate method leaves effect result incomplete.

// Current implementation boundary
// -------------------------------
//
// BodyAnalysis checks some property getter/setter presence and accessor visibility, and adds
// accessible getter effects on reads. Complete setter effects, compound/indexer operation plans,
// partial property merge, delegated accessors, safe-call CFG, and verified FIR effect cleanup remain
// incomplete.
