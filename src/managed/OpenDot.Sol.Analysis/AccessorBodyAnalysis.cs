// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

// A source accessor has its own callable identity even when its signature is inherited from a
// property/indexer. Rebinding it for a closed owner must not replace ordinary template bindings.
internal sealed class AccessorBodyAnalysis
{
    internal AccessorBodyAnalysis(BodyBindingBuilder builder, ResolvedType owner, BodySummary summary)
    {
        Owner = owner; Summary = summary;
        Expressions = builder.Expressions.ToImmutableDictionary();
        Calls = builder.Calls.ToImmutableDictionary();
        Operators = builder.OperatorBindings.ToImmutableDictionary();
        LocalTypes = builder.LocalTypes.ToImmutableDictionary();
        LambdaBodies = builder.LambdaBodies.ToImmutableDictionary();
        AnonymousFunctionBodies = builder.AnonymousFunctionBodies.ToImmutableDictionary();
        InterfaceUses = builder.InterfaceUses.ToImmutableDictionary();
        ConstructionProofs = builder.ConstructionProofs.ToImmutableDictionary();
        Defaults = builder.ConstructionDefaults.ToImmutableDictionary();
        Diagnostics = builder.Diagnostics.Distinct().ToImmutableArray();
        UnverifiedNodes = builder.Unverified.ToImmutableHashSet();
    }

    internal ResolvedType Owner { get; }
    internal BodySummary Summary { get; }
    internal ImmutableDictionary<AstExpression, ExpressionBinding> Expressions { get; }
    internal ImmutableDictionary<AstCall, BodyCallBinding> Calls { get; }
    internal ImmutableDictionary<AstNode, BodyOperatorBinding> Operators { get; }
    internal ImmutableDictionary<LocalSymbol, ResolvedType> LocalTypes { get; }
    internal ImmutableDictionary<AstLambda, LambdaBodySummary> LambdaBodies { get; }
    internal ImmutableDictionary<AstAnonymousFunction, AnonymousFunctionBodySummary> AnonymousFunctionBodies { get; }
    internal ImmutableDictionary<(AstNode Site, InterfaceOperationKind Kind), InterfaceOperationUse> InterfaceUses { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor? Constructor), ConstructionProof> ConstructionProofs { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor Constructor), ConstructorDefaultAnalysis> Defaults { get; }
    internal ImmutableArray<Diagnostic> Diagnostics { get; }
    internal ImmutableHashSet<AstNode> UnverifiedNodes { get; }
    internal bool IsComplete => Summary.Status == BodyBindingStatus.Bound && Diagnostics.IsEmpty && UnverifiedNodes.IsEmpty;
}
