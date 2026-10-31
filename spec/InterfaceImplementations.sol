// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative source selection; native interface dispatch/forwarding remains unsupported.
module source.spec.interface_implementations

// Group operations by canonical declaration, constructed owner, and specialized signature.
// Duplicate diamond routes to one implementation collapse. Independent bodies never win by
// declaration order. A concrete base-class implementation has no priority over an independent
// interface default. A conflicting type needs an explicit override even when no member is used.
// A more-specific interface override replaces an ancestor's implementation on all routes.
// A bodyless override reabstracts the operation; it does not expose the ancestor default again.
// A concrete type missing an operation is invalid. Abstract types can retain obligations.
//
// Every explicit implementation satisfies all matched contracts: generic arity, parameter types,
// passing modes, compatible return, visibility, checked effects, and @escaping. Inherited default
// arguments retain their declaration/source origins; implementation choice does not invent new
// defaults or choose between distinct default origins.
//
// Get and set are independent operations. Compatible accessors from different interfaces can
// combine when their property/storage value type agrees. Two providers of the same operation
// conflict. Indexer parameter signatures distinguish overloads. Index arguments use ordinary
// named/default/vararg/ref/out mapping and conversion ranking. Compound updates evaluate receiver
// and indices once, then getter, operator, setter. Each operation checks its own visibility/effects.
// Get/set pairs are not addressable ref/out storage.
//
// Interface by supplies a target per operation. Explicit overrides win; competing delegates or
// independent defaults require an explicit override. A delegate covers its own interface's default
// operations as well as abstract requirements. Inherited forwarding keeps its originating owner.
// The delegate expression initializes once per constructed receiver, including when explicit
// overrides replace all forwarding operations. Initialization effects and call effects are separate.
// Source selection retains target identity; unresolved construction/this-escape proof stays Deferred.
//
// super<I> is only an operation receiver. I must be an exact directly listed constructed base
// interface, not an indirect ancestor or another generic application of the same declaration.
// The selected method/getter/setter/indexer must reach a unique concrete interface body. The call
// remains a direct source-body selection, without renewed virtual dispatch. Visibility, arguments,
// substitutions, and checked effects still apply. Construction use is invalid, including stored
// property initializers, init blocks, and property/interface delegate initialization expressions.
// Runtime local initializers and accessor bodies remain ordinary operation call sites.
//
// SOL2236: competing implementation providers; SOL2237: missing required implementation;
// SOL2238: invalid qualified super; SOL2239: incompatible accessor value types.
// Existing override diagnostics retain their meaning. Invalid/deferred selection cannot certify
// caller/body/lambda completeness. Limit overflow cannot return a partial successful choice.
// Inheritance budgets: 128 depth, 8192 path visits, 128 routes per candidate. All identities belong
// to one source/type workspace; cancellation is observed before cache reuse and during traversal.
// Records are immutable internal side tables, not a new IR/public specialization API/CLI surface.
// Native interface dispatch, layout/vtables, forwarding codegen and receiver ABI remain excluded;
// otherwise valid source programs can still receive FIR SOL2400.

interface Read { val item: int { get = 1 } }
interface Write { var item: int { set {} } }
class Combined: Read, Write {}

interface Reader<T> { func read(value: T): T = value }
class Explicit: Reader<int> {
    override func read(value: int): int = super<Reader<int>>.read(value)
}
