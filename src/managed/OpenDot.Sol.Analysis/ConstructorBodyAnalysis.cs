// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

// An immutable source view for one exact constructed owner and constructor. It uses the existing
// construction interpreter and overload mapper; it is neither FIR nor an ordinary-body cache entry.
internal sealed class ConstructorBodyAnalysis
{
    internal ConstructorBodyAnalysis(BodyBindingBuilder builder, ConstructionProof proof)
    {
        Proof = proof;
        Expressions = builder.Expressions.ToImmutableDictionary();
        Calls = builder.Calls.ToImmutableDictionary();
        Operators = builder.OperatorBindings.ToImmutableDictionary();
        LocalTypes = builder.LocalTypes.ToImmutableDictionary();
        LambdaBodies = builder.LambdaBodies.ToImmutableDictionary();
        AnonymousFunctionBodies = builder.AnonymousFunctionBodies.ToImmutableDictionary();
        InterfaceUses = builder.InterfaceUses.ToImmutableDictionary();
        ConstructionProofs = builder.ConstructionProofs.ToImmutableDictionary();
        Defaults = builder.ConstructionDefaults.ToImmutableDictionary();
        Edges = builder.ConstructionEdges.ToImmutableDictionary();
        Delegates = builder.DelegateContracts;
        Diagnostics = builder.Diagnostics.Distinct().ToImmutableArray();
        UnverifiedNodes = builder.Unverified.ToImmutableHashSet();
    }

    internal ConstructionProof Proof { get; }
    internal ImmutableDictionary<AstExpression, ExpressionBinding> Expressions { get; }
    internal ImmutableDictionary<AstCall, BodyCallBinding> Calls { get; }
    internal ImmutableDictionary<AstNode, BodyOperatorBinding> Operators { get; }
    internal ImmutableDictionary<LocalSymbol, ResolvedType> LocalTypes { get; }
    internal ImmutableDictionary<AstLambda, LambdaBodySummary> LambdaBodies { get; }
    internal ImmutableDictionary<AstAnonymousFunction, AnonymousFunctionBodySummary> AnonymousFunctionBodies { get; }
    internal ImmutableDictionary<(AstNode Site, InterfaceOperationKind Kind), InterfaceOperationUse> InterfaceUses { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor? Constructor), ConstructionProof> ConstructionProofs { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor Constructor), ConstructorDefaultAnalysis> Defaults { get; }
    internal ImmutableDictionary<(AstNode Site, int Owner, AstConstructor? Constructor), ConstructionProof> Edges { get; }
    internal ImmutableArray<DelegateContract> Delegates { get; }
    internal ImmutableArray<Diagnostic> Diagnostics { get; }
    internal ImmutableHashSet<AstNode> UnverifiedNodes { get; }
    internal bool IsComplete => Proof.Status == BodyBindingStatus.Bound && Diagnostics.IsEmpty && UnverifiedNodes.IsEmpty;
}
