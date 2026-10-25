// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative inherited overload/override recovery; interface default conflict policy is pending.
module source.spec.member_resolution.override_recovery

// Inherited overload groups and override recovery
// ================================================
//
// Member lookup collects a declaration group; override validation and call selection consume that
// group under different rules. A recovery choice used to reduce diagnostics is not proof that the
// declaration is valid.

// Signature identity
// ------------------
//
// Compare function parameter count, ordered specialized parameter types, and passing modes after
// substituting each declaration's constructed owner. For generic methods, alpha-map type
// parameters by owner/ordinal position. A method's `T` is not identified by its spelling.
//
// Return type, parameter names, defaults, effect list, visibility, and source file order are not
// overload identity. Each has a separate compatibility rule. An override cannot create a new
// overload merely by changing return type or default value.
//
// An overload with a different parameter type remains in the inherited group unless an explicit
// hiding rule applies. Base `convert(String)` and derived `convert(int)` can both be candidates.

// Owner substitution
// ------------------
//
// For `Base<T>.read(value: T)` inherited by `Child: Base<String>`, the base formal becomes String.
// The call binder checks the derived declaration and inherited declaration using their own owner
// paths. It does not replace `T` globally in the source AST.
//
// A method-owned R on Base<T> remains open after T becomes String. Explicit or inferred method
// arguments close R independently. If a derived generic type declares another type parameter named
// T, the owner and method maps remain separate.
//
// An override's constructed base path can be reached through a generic interface diamond. Repeated
// paths to the same constructed base declaration do not create duplicate override requirements;
// conflicting constructed base signatures remain visible for declaration diagnostics.

// Valid override
// --------------
//
// A valid override requires an overridable base member, matching parameter/mode signature,
// permitted return covariance/conversion, non-reduced accessibility, and a checked effect set that
// does not widen the base budget. Final members cannot be overridden. A new same-signature member
// without `override` is diagnosed.
//
// Interface requirements use the declared implementation/default-member policy. A class member
// with the same spelling is not automatically a valid implementation if its signature or
// accessibility does not match.
//
// A hidden base non-callable declaration is not a method overload. Same-name type/property/function
// conflicts are classified before call overload selection.

// Recovery suppression
// --------------------
//
// When a same-signature derived member appears before a base member along a constructed path, the
// call binder may suppress the base recovery candidate to avoid reporting both as independent
// overloads. It still records the derived declaration's own override error.
//
// Suppression applies only to that matching signature and reachable path. It does not remove
// unrelated overloads, same-depth interface declarations, or candidates reached through unrelated
// bounds. A declaration from another interface branch is not “more derived” because its depth is
// numerically smaller in another route.
//
// Recovery must never make `BodyAnalysis.IsComplete` true when the declaration is invalid. The
// selected call result can be useful for further diagnostics while retaining an invalid/deferred
// declaration status.

// Call ranking
// ------------
//
// After visibility, mapping, inference, constraints, and applicability, compare conversion vectors
// per source argument. Candidate A dominates B only if A is no worse for every compared argument
// and strictly better for at least one. Incomparable vectors remain ambiguous.
//
// After equal conversion vectors, ordinary arity precedes vararg expansion; fewer defaults precede
// more defaults; an exact non-generic signature can precede a generic signature. No depth/file/
// source-order shortcut silently selects a winner. Any further specificity rule must be explicitly
// defined by the language and serialized in analysis results.

// Deferred recovery candidate
// ---------------------------
//
// A candidate may be rejected only on proven incompatibility. Missing inference evidence,
// inaccessible metadata, unresolved constraints, unavailable lambda context, incomplete user
// conversions, or path-resource exhaustion are not arbitrary maximum conversion ranks.
//
// A deferred candidate blocks a known winner when it might be applicable and could have a better
// conversion vector. It does not block when a proven exact-type/constraint mismatch makes it
// strictly worse or inapplicable.
//
// If a declaration-level error allows bounded recovery, expose both facts: the call's recovery
// binding and the invalid declaration status. Do not rebrand the declaration as a valid override.

// Named/default/vararg interaction
// -------------------------------
//
// Each inherited method retains its own parameter names and default origins under its declaration
// contract. Override defaults come from the base contract; they are not redeclared or selected
// according to whichever override is chosen at runtime.
//
// Named argument mapping occurs against the statically selected overload contract. A default does
// not contribute generic evidence. A middle vararg maps according to the declaration's parameter
// sequence and cannot be moved to the end to simplify overload ranking.
//
// Ref/out parameter mode is part of the signature. A value overload is not applicable to a ref
// argument just because an implicit conversion exists. `out var` remains uncreated until the
// candidate set has one selected result.

// Receiver and accessibility
// --------------------------
//
// The selected declaration's visibility is checked after member lookup. Protected access uses the
// original expression receiver's static type, not the declaring base owner. Private nearest-name
// ownership prevents retrying a public base overload. Cross-distribution internal metadata must
// be validated before an external candidate is considered complete.

// Inspection record
// -----------------
//
// For each candidate, tooling needs stable records for:
//
// - declaration identity and constructed declaring owner;
// - all inheritance paths and minimum path depth;
// - whether it is inherited, direct, bound-derived, hidden, or override-suppressed;
// - specialized parameter, result, mode, and checked-effect identities;
// - mapping for positional, named, default, vararg, spread, and trailing lambda arguments;
// - explicit type arguments and each lower/exact/expected/lambda/effect evidence origin;
// - constraint status, conversions, accessibility, and rejection/defer reason;
// - final applicability status and source diagnostic origins.
//
// Stable debug presentation is not a public binary cache schema unless the API version explicitly
// says so. Type identities are session scoped and source file paths are sanitized/relative.

// Required cases
// --------------
//
// - direct override and inherited call through one generic base;
// - same method spelling with distinct base overloads;
// - same-signature method missing `override`;
// - final target, return mismatch, reduced accessibility, and widened checked effect;
// - method parameter names differ while types match;
// - defaults differ while base contract remains selected;
// - generic owner T and method T share spelling but not identity;
// - repeated diamond path and conflicting constructed base path;
// - unrelated interface defaults with same signature;
// - hidden property/function groups and failed fallback;
// - inaccessible protected candidate beside a public candidate;
// - deferred candidate that blocks versus one proven worse;
// - candidate ranking after literal, lambda, vararg, and expected-result inference.

// Current implementation boundary
// -------------------------------
//
// The resolver currently suppresses same-signature override recovery candidates along retained
// paths and keeps distinct inherited overloads. Complete interface default selection, full
// declaration validation for generic return/effect substitution, structured public candidate
// reasons, and runtime direct/virtual/interface dispatch remain open.
