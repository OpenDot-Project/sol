// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative member visibility and candidate recovery; cross-binary metadata remains pending.
module source.spec.member_resolution.candidate_visibility

// Visibility during member candidate resolution
// =============================================
//
// Name lookup, accessibility, operation availability, and overload applicability are separate
// phases. A member can exist and own a name even when the current operation cannot access it. This
// contract defines recovery so an inaccessible or unavailable nearest member is not accidentally
// replaced with a farther declaration.

// Identities and access scopes
// ----------------------------
//
// A declaration's owner is the nominal declaration identity that contains the member. The current
// access scope is the lexical declaration context of the operation. A source path or simple type
// name does not establish owner identity. Partial declaration parts share the merged logical owner
// only after declaration analysis proves that identity.
//
// Public access is available wherever the declaration is imported and named. Private access is
// limited to the language-defined declaring scope. Protected access requires both a derived lexical
// caller and an allowed receiver when the caller differs from the owner. Internal access is limited
// to the declaring distribution. Distribution identity may come from the package/build metadata;
// absent such metadata across a binary boundary, access is unverified rather than assumed public.
//
// Nested declarations follow the explicit language visibility table. A containing declaration's
// accessibility constrains all child operations; a child modifier cannot widen the containing
// declaration's effective access.

// Lookup and filtering phases
// ---------------------------
//
// 1. Resolve the source receiver category and member name using lexical/module/member name rules.
// 2. Traverse constructed base and type-parameter-bound paths within the lookup budgets.
// 3. Apply path-sensitive same-name hiding and callable override recovery.
// 4. Classify the effective declaration group as missing, one value, overload set, ambiguous, or
//    incomplete/resource-limited.
// 5. For each candidate, check declaration visibility and operation-specific accessor visibility.
// 6. For accessible callable candidates, perform argument mapping, generic inference, constraints,
//    conversion ranking, checked effects, and ref/out validation.
// 7. Select one unique applicable candidate only after all candidates that could outrank it have
//    complete outcomes.
//
// A compiler can represent inaccessible candidates explicitly or discard them after retaining an
// access outcome. It cannot make an inaccessible declaration applicable. It also cannot restart a
// farther lookup as if the inaccessible nearest declaration never existed.

// Visibility by operation
// -----------------------
//
// | Member kind | Read | Write | Call | Ref/out |
// |---|---|---|---|---|
// | field/property | declaration + getter | declaration + setter/init | not a callable | declared location proof |
// | method | not a property value | not assignable | declaration access + receiver rule | requires explicit function location |
// | indexer | declaration + getter | declaration + setter | operation signature | ref-return proof |
// | nested type | declaration access | not writable | constructor access when constructed | not storage |
// | constructor | not a value read | not assignment | constructor access | not applicable |
// | companion/object member | declaration category rules | operation-specific | receiver category rules | explicit location only |
//
// This table is only a phase summary. The property, protected receiver, constructor, and operator
// contracts define the detailed operation rules.

// Nearest declaration and fallback
// ---------------------------------
//
// A path's nearest same-name declaration owns that name according to the declaration-kind hiding
// rule. If that declaration is inaccessible, lacks a getter, lacks a setter, has an incompatible
// call signature, or cannot be used with the receiver category, the compiler reports that operation
// failure. It does not silently use a farther same-named declaration on that same path.
//
// A separate unhidden path may still contribute a candidate. Path merging therefore preserves the
// surviving routes until the operation is classified. A candidate hidden on only one route is not
// globally removed if another retained route reaches it without the hider.
//
// A failed call to a property name does not reinterpret the property as a method. A failed property
// read does not reinterpret an overload group as a value. The compiler does not infer member kind
// from the syntax that happened to type-check first.

// Overload groups and inaccessible members
// -----------------------------------------
//
// An overload group contains callable declarations that remain after path-sensitive hiding and
// valid recovery suppression. Each candidate keeps its declaration owner, constructed receiver,
// generic owner substitution, source argument map, and access result.
//
// A candidate that fails accessibility cannot win or provide generic inference facts. A candidate
// with an inaccessible accessor cannot satisfy an operation requiring that accessor. However, a
// separate accessible overload with a different signature remains eligible.
//
// If all candidates are inaccessible, report the access failure associated with the best matching
// declaration rather than a misleading missing-member diagnostic. The selection of a diagnostic
// representative is deterministic and does not grant access.
//
// If an accessible candidate is complete but an inaccessible candidate could otherwise rank
// better, the inaccessible candidate still cannot win. Its access error is not a reason to defer a
// candidate that is semantically unavailable. If an accessible candidate is incomplete and could
// rank better than another accessible candidate, defer normally.

// Accessor-specific candidates
// ----------------------------
//
// A property declaration has one name identity and may expose separately accessible operations.
// A public property with a private getter and public setter is write-only outside its owner scope.
// A public getter/private setter is readable but not externally writable. Compound operations need
// both operations and fail if either is inaccessible.
//
// The diagnostic should name the requested accessor. It must not say the property is absent or
// erase its other accessible operation. An inaccessible setter does not poison an ordinary read;
// an inaccessible getter does not poison a simple assignment.
//
// Accessor effects are also operation-specific. A simple write adds setter effects but not getter
// effects. A read adds getter effects but not setter effects. A compound update adds both in the
// language-defined sequence. Effects from an inaccessible accessor are not added because it is not
// selected.

// Generic specialization and access
// ---------------------------------
//
// Member paths specialize declaration signatures with each constructed owner. Access checks use
// the owner declaration identity and source receiver type; generic type argument equality alone is
// not an access grant. A method-level parameter with the same spelling as an owner parameter stays
// independent.
//
// A generic constraint can expose members of multiple bounds. Each candidate keeps its source
// declaration owner. A bound that proves access to one candidate cannot authorize a different
// candidate from an unrelated interface bound. Ambiguous inherited declarations remain ambiguous
// after access filtering unless only one accessible applicable candidate is permitted by the
// explicit visibility rules.

// Callbacks, lambdas, and access scope
// ------------------------------------
//
// A lambda declared lexically inside a type body follows the closure/access contract for that
// lexical scope. Capturing a receiver does not widen declaration visibility. Passing a lambda to
// another function does not transfer the caller's protected/private permission to the callee.
//
// Candidate-local lambda analysis occurs after receiver name lookup but before final overload
// choice. It can infer generic method parameters; it cannot make an inaccessible member accessible,
// change the lexical caller type, or change the source receiver's static type.
//
// Nested function declarations, where supported by the grammar, have their own access context. A
// body-analysis implementation must use AST ownership/lexical scope, not nearest textual token, to
// decide which type contains an access expression.

// Diagnostic ownership matrix
// ---------------------------
//
// | Condition | Primary owner | Candidate behavior |
// |---|---|---|
// | no declaration on complete paths | name resolution | missing |
// | nearest declaration inaccessible | access analysis | unavailable; no same-path fallback |
// | protected caller is unrelated | access analysis | unavailable |
// | protected receiver too broad | access analysis | unavailable |
// | getter unavailable | property operation | reject read, preserve write |
// | setter unavailable | property operation | reject write, preserve read |
// | wrong call argument shape | candidate mapping | reject candidate |
// | generic facts conflict | candidate inference | reject candidate |
// | generic facts missing | candidate inference | deferred |
// | equal applicable overloads | overload ranking | ambiguous |
// | lookup path budget exceeded | member resolver | incomplete/resource-limited |
// | cancellation | analysis operation | restore and propagate |
//
// Candidate errors are combined only after the relevant candidate set is complete. A syntax or
// declaration error can coexist with a bound but must not be rewritten as an access success.

// Stable diagnostics
// ------------------
//
// Diagnostic identity includes the access operation kind and source span. Two accesses to one
// declaration may produce different diagnostics because one is a read and another is a write.
// Diagnostic ordering is deterministic by source position and stable declaration identity. It does
// not depend on traversal queue order, hash iteration, or which overload produced a speculative
// error first.
//
// Candidate-local diagnostics are buffered during inference. An unselected candidate's speculative
// lambda diagnostics are discarded or summarized as a bounded candidate reason. The selected
// access/call diagnostic is published at the source call or member span according to the diagnostic
// contract.

// Required invariants
// -------------------
//
// - Name lookup, hiding, visibility, accessor availability, and applicability remain distinct.
// - An inaccessible declaration does not become an ordinary missing name.
// - An inaccessible nearest member does not reveal a same-path base fallback.
// - Alternate unhidden paths remain candidates until reduction.
// - Access failures cannot contribute generic evidence or overload rank.
// - Getter and setter permission are independently checked.
// - Protected permission requires lexical derivation plus a permitted receiver.
// - Generic inference cannot change visibility or receiver identity.
// - Diagnostics use stable declaration and source identities.
// - A limit or cancellation cannot claim a complete accessible candidate set.

// Current implementation boundary
// -------------------------------
//
// The current analysis validates source-local visibility for methods/properties, checks protected
// receiver types, distinguishes getter/setter access and preserves inherited candidate paths. Full
// cross-distribution internal metadata, every declaration category, full interface conflict
// resolution, stable public candidate outcomes, and native dispatch remain pending.
