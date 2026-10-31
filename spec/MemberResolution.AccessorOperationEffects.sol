// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative accessor operation/effect contract; execution lowering remains pending.
module source.spec.member_resolution.accessor_operation_effects

// Operation-specific property and indexer effects
// ================================================
//
// A property is not one indivisible read/write function. Each source operation selects the
// accessor or accessor sequence it actually needs. Visibility, checked effects, receiver
// evaluation, and failure recovery are computed for that operation.

// Accessor declaration model
// --------------------------
//
// A property declaration has one logical owner and type. It may declare a getter, setter, init
// accessor, or no explicit accessors. If accessors are omitted, the property contract supplies the
// language-defined implicit operations for that property kind and mutability. A setter parameter
// receives the property value type after constructed-owner substitution.
//
// Getter result type must equal the specialized property type. Setter/init input must accept the
// property type according to the declaration validation rules. Accessor effects are declared at
// each operation and are checked against the containing callable's checked-effect budget when that
// operation is used.
//
// An accessor's narrower visibility applies only to that operation. An accessor cannot widen the
// property's effective visibility. An unmodified accessor inherits the property's visibility.
//
// Operation plan
// --------------
//
// | Source operation | Required operations | Effects unioned | Writable storage? |
// |---|---|---|---:|
// | value read | getter | getter effects | no |
// | simple `p = value` | setter or allowed init | setter effects | no |
// | `p += value` expanded form | getter, operator, setter | all three in evaluation order | no |
// | `p++` expanded form | getter, increment operator, setter | all three | no |
// | `ref p` / `out p` | declared ref-returning location | location effects if specified | only with address proof |
// | safe `receiver?.p` read | getter on non-null branch | getter effects on that branch | no |
// | safe `receiver?.p = value` | conditional write if grammar permits | setter effects on non-null branch | no |
// | indexed read `x[i]` | indexer getter | getter effects | no |
// | indexed assignment `x[i] = v` | indexer setter | setter effects | no |
// | indexed compound update | getter, operator, setter | all selected effects | no |

// Read selection
// --------------
//
// Evaluate the receiver once, select one property identity, require the getter, check declaration
// and getter visibility, specialize the property/getter types, and add getter effects. Do not call
// the setter merely because the property is mutable. Do not treat a write-only property as a value.
//
// A getter failure prevents a successful value binding. A getter with a checked exception adds
// that effect to the read site. The containing callable must declare or handle it under the
// exception specification. An unchecked RuntimeException subtype is not added to the checked
// effect set.
//
// A delegated property read additionally includes the selected delegation getter's effects. The
// property and delegate operations are both represented in the bound expression facts. If delegate
// protocol resolution is incomplete, the read is unverified rather than silently effect-free.

// Write selection
// ---------------
//
// Evaluate the receiver exactly once. Resolve the property and setter/init accessor, verify that
// the operation is permitted at this declaration/use site, evaluate the right-hand expression in
// source order, convert it to the specialized setter input, and add setter effects.
//
// A simple assignment does not invoke the getter and therefore cannot inherit getter effects or a
// getter failure. A missing or inaccessible setter is a write failure. It does not make the
// property readable or expose a same-named inherited field.
//
// An `init` accessor is usable only at its language-defined initialization sites. A constructor's
// own backing-field initialization rule does not imply that all callers can invoke init.

// Compound operation sequencing
// -----------------------------
//
// For the expanded compound form, the observable operation order is:
//
// 1. Evaluate receiver once.
// 2. Evaluate index arguments, if any, once from left to right.
// 3. Invoke getter once and produce the old property value.
// 4. Evaluate the right operand once in source order.
// 5. Select and invoke one applicable operator.
// 6. Convert the operator result to the specialized setter input.
// 7. Invoke setter once.
//
// The language may define a direct compound operator for a location. Only that explicit contract
// replaces the expanded sequence. Implementations may not invoke both the direct and expanded form.
//
// A failure at step 3 prevents later steps. A failure at step 5 prevents the setter. A failure at
// step 7 occurs after getter and operator effects; the language does not promise to roll them back.
// Static analysis records possible effects without executing any operation.

// Effect and exception composition
// ---------------------------------
//
// Each selected accessor contributes only its own declared effect set. The receiver, index
// expressions, right operand, operator, and accessor effects are combined in source evaluation
// order. Duplicate checked type identities may be normalized in the set, but source origins remain
// available for diagnostics and flow.
//
// A getter effect does not automatically apply to a simple write. A setter effect does not apply
// to a read. A compound operation uses both. An indexer key expression's effects are independent of
// the indexer getter/setter effects.
//
// If an accessor is inaccessible, it cannot be selected to satisfy the operation. An inaccessible
// getter does not invalidate an independently accessible setter. An inaccessible setter does not
// invalidate an independently accessible getter. A compound operation requires both and fails if
// either required accessor is inaccessible.

// Visibility table
// ----------------
//
// | Property | Getter | Setter | Read | Write | Compound |
// |---|---|---|---|---|---|
// | public | inherited public | inherited public | allowed | allowed | allowed |
// | public | private | public | only owner scope | allowed | owner scope only |
// | public | public | private | allowed | only owner scope | owner scope only |
// | protected | protected | protected | protected receiver rule | protected receiver rule | both required |
// | private | any | any | owner scope only | owner scope only | owner scope only |
// | inaccessible declaration | public accessor | public accessor | rejected | rejected | rejected |
//
// The exact modifier grammar and accessor inheritance rules are declared in the object contract.
// This table does not allow an accessor to widen its parent declaration.

// Indexer operations
// ------------------
//
// An indexer uses the same operation distinction as a property but includes its index parameter
// map. Candidate selection checks index argument names, order, defaults, vararg rules, modes, and
// generic substitution before selecting the getter or setter signature.
//
// A read converts each index argument against the getter's index parameter. A write converts index
// arguments and then the right-hand value against the setter value parameter. A compound operation
// selects a getter and setter with compatible index identity plus one operator. It cannot use the
// getter from one unrelated interface candidate and setter from another unless the interface merge
// contract explicitly establishes one logical indexer.
//
// Receiver and index arguments are not reevaluated while comparing candidates. Generic inference
// and overload ranking inspect immutable source/type facts; only the selected operation publishes
// its call bindings.

// Safe access
// -----------
//
// Safe access evaluates a nullable receiver once. If it is null, the getter/setter/operator chain
// is skipped according to the assignment grammar. The skipped path contributes no accessor effect.
// The non-null path performs the operation-specific access checks and effect accumulation.
//
// A safe read lifts its non-null result to the declared nullable result shape. It does not create
// writable storage. A safe write is permitted only if the grammar explicitly defines conditional
// assignment; otherwise the compiler rejects it. A safe operation cannot be used as ref/out.
//
// Nullable element and nullable container shapes remain distinct. Lifting the property result does
// not add nullability to each element of an array result.

// Ref/out and addressability
// --------------------------
//
// A normal getter followed by a setter is not a location. A copied struct property value, computed
// property, safe access result, or indexer temporary cannot satisfy ref/out. The declaration must
// expose an explicit ref-returning accessor under a lifetime/alias contract that proves the
// location remains valid for the call.
//
// Access to a ref-returning property checks declaration and accessor visibility, receiver lifetime,
// exact storage type, mutability, aliasing restrictions, and ref-like escape constraints. A failed
// proof remains unverified or produces the dedicated ref-safety diagnostic.

// Generic specialization
// ----------------------
//
// Owner type parameters are substituted through the constructed member path for the property type,
// accessor input/result, indexer parameters, and accessor effects. Method generic parameters are
// distinct declaration identities. A property does not become a generic method because its getter
// body invokes a generic function.
//
// For a partial property, declaration merge first establishes one symbol and validates that there
// is at most one compatible getter and setter. Conflicting duplicate accessors are declaration
// errors, not candidates selected by file order. Generic substitution occurs after partial identity
// merge so every part shares the same owner map.

// Diagnostics and recovery
// ------------------------
//
// | Failure | Operation site | Recovery |
// |---|---|---|
// | missing getter | read/compound | Reject read; do not call setter |
// | missing setter | write/compound | Reject write; do not synthesize storage |
// | getter inaccessible | read/compound | Reject read portion, retain other candidates |
// | setter inaccessible | write/compound | Reject write portion, retain readable result |
// | incompatible getter/setter types | declaration or selected op | Report contract failure |
// | getter checked effect undeclared | read/compound | Report effect failure at use site |
// | setter checked effect undeclared | write/compound | Report effect failure at use site |
// | conflicting inherited properties | lookup | Preserve ambiguity; no source-order choice |
// | path/resource limit | lookup | Mark operation unverified |
// | cancellation | any phase | Restore speculation and propagate cancellation |
//
// Diagnostics should name the actual failed operation and accessor. They should not produce a
// misleading “no property” result when a property exists but the requested accessor is unavailable.

// Required invariants
// -------------------
//
// - Read, write, compound, ref/out, and safe access have distinct selected accessor sets.
// - A simple write does not execute or charge a getter.
// - A read does not execute or charge a setter.
// - Compound sequencing and possible effects follow source evaluation order.
// - Receiver and index expressions are bound/published once for the selected candidate.
// - Getter/setter visibility is checked independently without widening the property.
// - Accessor effects are included only when that accessor is selected.
// - Generic owner substitution reaches signatures and effect types.
// - Getter-plus-setter does not imply an addressable ref/out location.
// - Accessor ambiguity, cancellation, and resource exhaustion never select by declaration order.

// Current implementation boundary
// -------------------------------
//
// BodyAnalysis currently validates property getter/setter availability, protected/private accessor
// visibility, and getter/delegated-getter effects for supported expressions. It does not execute
// getters or setters, lower compound updates, implement full indexers/ref returns, or prove all
// initialization and borrow cases. Those remain later FIR/runtime obligations.
