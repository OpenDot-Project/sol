// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: implemented bounded source-analysis contract; no generic owner FIR/native admission.
module source.spec.generic_member_body_calls

// Selected generic owner calls require both a source signature and a closed body proof.
// The original source receiver and constructed declaring owner are separate facts. A source
// receiver Leaf<Widget> may project through Middle<Widget> to Root<Widget>. That projection must
// retain its distinct routes and canonical owner identity before the method body is rebound.
// An ambiguous, missing, incomplete, or resource-limited projection supplies no body proof.
//
// A method's inferred return may change when its owner arguments close. The binder must use the
// closed result when checking an enclosing call or expected result; it must not substitute the
// return type of an overload chosen only in the original open body. Closed owner arguments and
// closed method arguments are distinct ordered vectors applied to the original AST.
//
// Return inference is requested during source binding for implemented top-level generic class,
// struct, ref struct, and interface methods. A recursive inferred signature cannot assume its own
// result. Repeated active instances and the 128-instance return-inference stack bound leave an
// explicit proof gap. A return annotation independently supplies the recursive signature.
// A source inference workspace permits at most 1024 uncached return-body/template attempts;
// successful cache hits do not consume another attempt. Failed repeated requests consume work too.
// During method argument inference, an owner-closed result template may keep the method's own
// parameter identities open. It supplies inference evidence only; constraints are rechecked after
// argument solving, and this template is not a successful closed body view.
// Recursive callee signatures use their original parameter graph and the callee's new vectors;
// a caller substitution cannot replace a callee parameter before its own argument vector applies.
//
// After source selection, selected generic owner calls are checked automatically. Member calls
// appearing after closed free-function specialization are checked before that view is cached as
// successful. This phase checks already selected source targets; it does not select a different
// overload to make an invalid closed body pass.
// Generic free-function dependencies, including eligible stateless object methods, participate in
// this same graph. A matching free-function signature alone cannot certify a generic member body.
// Locally bound free-function snapshots are cached separately from dependency-checked copies;
// transitive failures cannot mutate local facts or enter the successful checked-view cache.
// Generic local functions needing an enclosing lexical instance retain an explicit proof gap.
//
// The body dependency graph is keyed by original method identity, canonical constructed declaring
// owner identity, and ordered method type identities. Repeated source call sites can share the
// same instance while retaining separate origins, argument mappings, and projection paths.
// Free-function instances omit the receiver-owner component and keep their source declaration
// identity plus ordered method arguments; stateless object methods retain their declaration owner.
//
// Local body statuses and dependency statuses compose monotonically. Invalid dominates Deferred,
// which dominates Bound. An invalid reachable dependency invalidates its callers; a missing,
// open, unsupported, or incomplete dependency prevents a locally bound caller from being complete.
// The complete finite graph is discovered before status propagation. A cycle with independently
// annotated signatures and entirely bound local bodies/outgoing dependencies may be Bound. This
// proves source analysis of the group; it is not a termination proof.
//
// Expanding dependency chains are bounded to 128 distinct levels, 1024 canonical instances, and
// 8192 generic-call dependency edges, including repeated call sites that share a target instance.
// Revisiting an existing instance is an edge, not a new instance. Type-changing recursion that
// creates a new constructed owner/method vector consumes the budget. Exhaustion returns Deferred
// evidence and may not expose a truncated graph as complete.
//
// A non-Bound call is retained in UnverifiedNodes. Its status propagates to containing expression
// and callable summaries; enclosing lambda and anonymous-function complete flags are cleared.
// Published arrays and lookup queries must agree, preserve source ordering, and remain unchanged
// after publication. A sibling outside the affected source subtree keeps its independent facts.
// Specialized body views update their own snapshots rather than mutating the open source view.
//
// Original closed-body diagnostics retain source origins and the normal diagnostic limit. Invalid
// and deferred specialized free-function views are not stored in the successful body cache.
// Cancellation must interrupt work and propagate; it must not publish a completed query result.
//
// The source body's selected signature must agree with the rebound body before proof is Bound.
// An unresolved result discrepancy stays Deferred. Nested generic owners and missing member bodies
// remain explicit gaps. None of these results enable generic owner layout, receiver ABI, dispatch,
// runtime metadata, GC maps/barriers, AOT/ORC emission, or exported artifact ABI. SOL2400 remains the
// independent generic owner/member FIR/native boundary.
