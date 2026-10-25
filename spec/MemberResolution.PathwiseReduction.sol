// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative pathwise hiding and override recovery; default-interface merging remains open.
module source.spec.member_resolution.pathwise_reduction

// Pathwise member reduction
// =========================
//
// Member lookup first collects a complete bounded set of constructed declaration paths. Hiding and
// override recovery then reduce that set without losing an alternate route that does not pass
// through the hiding or overriding declaration. A single global “nearest depth wins” rule is
// insufficient for multiple interface bounds and diamond inheritance.

// Path and candidate identity
// ---------------------------
//
// A path is an ordered sequence of workspace-owned constructed type identities beginning at the
// source receiver and ending at a declaration owner. Every edge applies the current owner's generic
// substitution exactly once. A repeated spelling with another owner or argument identity is a
// different type. A repeated identity in one active path is a cycle and stops that path.
//
// The declaration candidate identity is the pair of declaration symbol and constructed declaring
// owner. One candidate may retain several distinct paths. Two declarations on one owner are not
// merged merely because their source signatures print the same. Two constructed owners are not
// merged because their names are equal.
//
// Path facts include the minimum depth, deterministic canonical display path, all bounded
// alternative paths, and whether any route begins at a type-parameter bound. A candidate is not
// complete if the traversal or path cap is exceeded.

// Collection precedes reduction
// -----------------------------
//
// 1. Validate that the receiver identity belongs to the active type-analysis workspace.
// 2. Reject non-nominal, bottom, error, primitive, and nullable receivers from nominal member walk.
// 3. Start a queue with the receiver path at depth zero.
// 4. Expand declared bases and every nominal type-parameter bound after substitution.
// 5. Collect all declarations with the requested source name at each visited constructed owner.
// 6. Record each exact route; deduplicate an exact repeated route, not a different route to the same
//    owner.
// 7. Enforce traversal and per-candidate path budgets before claiming the lookup is complete.
// 8. Perform name hiding, callable override recovery, and final classification on the collected
//    candidates.
//
// Collection does not evaluate getters, call methods, select operators, or execute constructors.
// Candidate reduction cannot use a partially collected set after a resource-limit result.

// Per-route name hiding
// ---------------------
//
// A declaration at owner D is hidden on path P when an earlier owner on P declares a name-owning
// declaration according to the member category rules. Hiding is a property of the route, not only
// of declaration depth.
//
// A candidate with several paths is fully hidden only if every retained route is hidden. If one
// route remains unhidden, retain the candidate and preserve the surviving route in its path set.
// This prevents a same-named declaration on one interface branch from erasing a declaration
// reachable through an unrelated branch.
//
// Hiding must distinguish callable and non-callable categories. A property, nested type, or other
// value declaration can own a name against a base callable group according to the name-category
// contract. A derived method can hide a base property on its route. At one effective owner, a
// method/value mixture is an ambiguity; source declaration order cannot decide it.
//
// Inaccessible declarations still participate in name ownership unless the accessibility contract
// explicitly says otherwise. An inaccessible nearest member cannot expose an arbitrary base
// fallback. A missing getter is not permission to bind to another same-named base property.

// Path-specific examples
// ----------------------
//
//     interface Root { func label(): String }
//     interface Hidden: Root { val label: String }
//     interface Direct: Root { }
//     interface Combined: Hidden, Direct { }
//
// For `Combined`, one path to Root passes through Hidden and one does not. The method is hidden on
// the Hidden route, but the Direct route remains a valid inherited callable route. The result must
// retain enough path data to diagnose any incompatible duplicate interface contract separately.
//
//     interface Root { val item: String }
//     interface Left: Root { func item(): String }
//     interface Right: Root { }
//     interface Diamond: Left, Right { }
//
// The property candidate is hidden on the Left route and visible on the Right route. Global minimum
// depth alone must not drop the only surviving property route. The method group is collected and
// classified independently; any combined ambiguity follows the declared source name category.
//
//     open class Base { open func render(value: int): String = "base" }
//     class Derived: Base { override func render(value: int): String = "derived" }
//
// The override may replace the inherited callable on the path `Derived -> Base`. It cannot suppress
// a distinct overload such as `render(value: String)` from Base.

// Override recovery by path
// -------------------------
//
// Compare method parameter count, method type-parameter arity, passing modes, and parameter types
// after each candidate's own constructed-owner substitution. Alpha-rename method type parameters
// by ordinal before comparing. Return type, names, defaults, checked effects, and source order are
// not overload identity.
//
// An inherited callable candidate can be suppressed by a more-derived same-signature recovery
// candidate only when every retained path to that inherited candidate contains the derived owner
// before the inherited owner. If only one route passes through the derived declaration, suppressing
// the inherited candidate globally would delete a distinct surviving route.
//
// Suppression is recovery for continued analysis. It does not validate `override`, openness,
// accessibility, return covariance, or effect compatibility. The declaration validator reports
// invalid overrides independently. An invalid most-derived declaration does not silently make the
// language binding successful.
//
// A same-depth declaration never suppresses another solely by matching its signature. Equal-depth
// interface declarations remain distinct unless a dedicated interface merge rule proves they are
// one declaration identity. Ambiguity recovery must preserve all owners and route summaries.

// Override path matrix
// --------------------
//
// | Base candidate routes | Derived same-signature owner | Reduction |
// |---|---|---|
// | every route crosses derived owner first | one valid matching candidate | suppress base recovery candidate |
// | some routes cross derived owner | same declaration | preserve candidate for alternate route |
// | no route contains derived owner | same signature elsewhere | do not suppress |
// | route crosses different constructed derived owner | same printed signature | do not assume identity |
// | one path has a different parameter mode | apparently same name/arity | keep both candidates |
// | owner substitution changes a parameter | same declaration source text | compare substituted identities |
// | method type parameters use different names | same ordinal and shape | alpha-compare by ordinal |
// | return type alone differs | same parameters | not an overload; validate override result |
// | checked effects alone differ | same parameters | not an overload; validate effect narrowing |
// | source span order differs | same candidate identity | result remains unchanged |

// Complexity budgets
// ------------------
//
// Lookup has a depth cap of 128, a path-state visit cap of 8192, and a cap of 128 retained paths
// per declaration/constructed-owner candidate. A path key is an ordered identity sequence, not a
// display name. A broader path-state budget may fail before a candidate reaches its individual cap.
//
// Candidate pairwise reduction is bounded by the collected candidate count. Implementations should
// avoid repeated full scans where a deterministic index by source name, owner, and signature is
// sufficient. An optimization may not change visible candidates or turn a limit into Missing.
//
// Cancellation is checked before queue expansion and between candidate comparisons. Cancellation
// propagates and returns no result claiming completeness. It is not reported as a missing member.

// Lookup status
// -------------
//
// | Status | Meaning | Consumer action |
// |---|---|---|
// | Missing | Complete walk found no visible name candidate | Emit ordinary missing-member diagnostic |
// | Single | One non-callable candidate remains | Bind it and check operation-specific access |
// | OverloadSet | A callable group remains | Apply generic inference and overload ranking |
// | Ambiguous | Several incompatible effective declarations remain | Report ambiguity with bounded identities |
// | LimitExceeded | The path set is incomplete | Defer the source location and report resource limit |
//
// A consumer cannot treat Ambiguous as Single by picking the first result. It cannot treat
// LimitExceeded as Missing. If accessibility or accessor availability later rejects the nearest
// candidate, the consumer does not restart lookup at a farther owner unless the path reduction
// contract left that route eligible.

// Determinism
// -----------
//
// Candidate identities, path aggregation, and source diagnostics are stable when files or base
// declarations are enumerated in another order. Canonical path sorting exists only for inspection
// and reproducible serialization. It never controls hiding, override suppression, or overload
// priority.
//
// Stable ordering uses inheritance depth, qualified declaration identity, specialized signature,
// and workspace-owned constructed type identity. Any hash-based working set is converted to this
// order before publication. Numeric type IDs from another analysis instance are invalid keys.

// Required invariants
// -------------------
//
// - Adding an exact duplicate route changes neither the candidate set nor its status.
// - Adding a different route to one constructed owner preserves that route until reduction.
// - One hidden route cannot erase an unhidden route to the same candidate identity.
// - A base overload with a different specialized signature survives a derived override.
// - A same-name property cannot disappear solely because one unrelated interface branch has a method.
// - A hidden route cannot reappear as a fallback after a failed call on that same route.
// - An invalid override does not become a valid declaration due to call recovery suppression.
// - Resource exhaustion and cancellation never return a complete partial candidate set.
// - Diagnostic ordering and selected results are independent of source file enumeration.
//
// Current implementation boundary
// -------------------------------
//
// The current resolver records constructed paths, path-sensitive name hiding, and path-aware
// same-signature recovery under the stated bounds. Interface default conflict resolution, full
// generic method specialization, explicit conflict declarations, metadata across binaries, and
// stable public serialization of path provenance remain pending.
