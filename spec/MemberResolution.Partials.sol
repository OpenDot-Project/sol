// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative partial/member identity contract; full cross-file body validation is pending.
module source.spec.member_resolution.partials

// Partial declarations and member identity
// =========================================
//
// Member lookup uses the merged nominal declaration symbol. A partial type's source file, file
// order, and directory path do not define a separate runtime type or member owner. The merge key
// is module identity, nominal nesting, name, generic arity, and distribution identity.

// Shared generic parameters
// -------------------------
// Compatible partial parts share one ordered type-parameter identity sequence. The parameter at
// ordinal zero in one part is the same owner parameter at ordinal zero in another compatible part.
// Spelling agreement is validated but is not the identity key. Constraints from all compatible
// declarations combine subject to the partial-type consistency contract.
//
//     partial class Cache<T: Object> { val first: T }
//     partial class Cache<T> { func get(): T = first }
//
// `get` reads the same T-owned `first` property when the merged Cache is viewed as Cache<String>.
// It does not create a second Cache<T>, and the member's source part does not affect substitution.

// Member set merge
// ----------------
// All partial parts contribute member declarations to one type symbol. Distinct overload signatures
// form one overload group. Duplicate signatures remain declaration errors even if each copy occurs
// in a different part. Different parameter names/defaults do not make duplicate signatures unique.
//
// A property/field/name collision across parts is validated against the member category rules.
// Multiple primary constructors, multiple companion objects, incompatible base lists, or a part
// lacking `partial` are errors. Recovery may keep all origin spans for diagnostics but cannot
// publish one arbitrary winner as a valid symbol.

// Partial base substitutions
// --------------------------
// A compatible base contract declared on a partial part participates once in the merged type. If
// repeated parts write the same constructed base identity, it is deduplicated. If they write
// incompatible constructed applications, the partial declaration is invalid and lookup retains
// enough evidence to report the conflict.
//
//     partial class Child<T>: Root<T> { }
//     partial class Child<T> { func value(): T }
//
// Child<String> reaches Root<String>; the method's T resolves to String. Reversing the two source
// files changes neither member identity nor the constructed path.

// Nested partial declarations
// ---------------------------
// A nested type's identity includes its merged containing type identity. The nested declaration
// inherits the outer substitution through its owner chain, but it owns a separate type-parameter
// list. Identically named outer/nested parameters remain distinct by owner and ordinal.
//
//     partial class Outer<T> {
//         partial class Inner<T> { func pair(outer: Outer<T>): T }
//     }
//
// This sample must be interpreted with actual lexical type binding. If the inner T shadows the
// outer T by language scope, the type in Outer<T> refers to the outer owner while the result T is
// the inner owner. Parameter names alone cannot build the substitution map.

// Member origin and diagnostics
// -----------------------------
// A merged symbol retains every source declaration part and member origin. A duplicate member
// diagnostic cites both declarations in deterministic source order. A use-site lookup identifies
// the canonical merged symbol but still points diagnostics at the originating accessor, method
// body, or property declaration that failed validation.
//
// JSON/IDE APIs use source-file indexes and spans owned by the AnalysisSession. They cannot assume
// one source path per module or serialize absolute private working paths. A source snapshot from a
// different session is rejected even if its module/name/signature text is identical.

// Lookup consistency requirements
// -------------------------------
// Moving a compatible partial part, reversing file enumeration, splitting a valid overload group
// across files, moving an unrelated member, or renaming its directory while retaining the explicit
// module name preserve semantic identity. Changing module/nesting/name/arity/distribution, generic
// parameter ordinal/bound, or adding a duplicate signature changes or invalidates the identity.
//
// The compiler must not use first-seen part or file path as a hidden tie-break. The merged type
// symbol is the sole owner for receiver substitution and inherited lookup. A partial declaration
// in a separate binary cannot merge through a source-only session; distribution metadata and
// explicit source inputs determine that boundary.

// Implementation boundary
// ------------------------
// Compatible partial composition exists in the declaration analyzer. This document extends its
// required behavior into member lookup, generic method inference, accessibility, and later
// monomorphization. Cross-distribution partial merging is not implied.
