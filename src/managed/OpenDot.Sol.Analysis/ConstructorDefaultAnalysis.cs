// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

// Defaults run in the caller before allocation. Their closed source bindings must not be mixed
// with constructor-body bindings, or with another call selecting a different default subset.
internal sealed class ConstructorDefaultAnalysis
{
    internal ConstructorDefaultAnalysis(BodyBindingBuilder builder, AstNode site, ResolvedType owner,
        AstConstructor constructor, ImmutableArray<int> parameters, BodyBindingStatus status,
        bool normal, ImmutableArray<ResolvedType> effects)
    {
        Site = site; Owner = owner; Constructor = constructor; Parameters = parameters;
        Status = status; CanCompleteNormally = normal; Effects = effects;
        Expressions = builder.Expressions.ToImmutableDictionary();
        Calls = builder.Calls.ToImmutableDictionary();
        Operators = builder.OperatorBindings.ToImmutableDictionary();
        LambdaBodies = builder.LambdaBodies.ToImmutableDictionary();
        AnonymousFunctionBodies = builder.AnonymousFunctionBodies.ToImmutableDictionary();
        InterfaceUses = builder.InterfaceUses.ToImmutableDictionary();
        ConstructionProofs = builder.ConstructionProofs.ToImmutableDictionary();
        Defaults = builder.ConstructionDefaults.ToImmutableDictionary();
        Edges = builder.ConstructionEdges.ToImmutableDictionary();
        Diagnostics = builder.Diagnostics.Distinct().ToImmutableArray();
        UnverifiedNodes = builder.Unverified.ToImmutableHashSet();
    }

    internal AstNode Site { get; }
    internal ResolvedType Owner { get; }
    internal AstConstructor Constructor { get; }
    internal ImmutableArray<int> Parameters { get; }
    internal BodyBindingStatus Status { get; }
    internal bool CanCompleteNormally { get; }
    internal ImmutableArray<ResolvedType> Effects { get; }
    internal ImmutableDictionary<AstExpression, ExpressionBinding> Expressions { get; }
    internal ImmutableDictionary<AstCall, BodyCallBinding> Calls { get; }
    internal ImmutableDictionary<AstNode, BodyOperatorBinding> Operators { get; }
    internal ImmutableDictionary<AstLambda, LambdaBodySummary> LambdaBodies { get; }
    internal ImmutableDictionary<AstAnonymousFunction, AnonymousFunctionBodySummary> AnonymousFunctionBodies { get; }
    internal ImmutableDictionary<(AstNode Site, InterfaceOperationKind Kind), InterfaceOperationUse> InterfaceUses { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor? Constructor), ConstructionProof> ConstructionProofs { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor Constructor), ConstructorDefaultAnalysis> Defaults { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor? Constructor), ConstructionProof> Edges { get; }
    internal ImmutableArray<Diagnostic> Diagnostics { get; }
    internal ImmutableHashSet<AstNode> UnverifiedNodes { get; }
}
