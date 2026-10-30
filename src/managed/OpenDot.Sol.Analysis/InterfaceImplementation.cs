// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal enum InterfaceOperationKind { Method, Get, Set }
internal sealed record InterfaceOperation(ResolvedMemberCandidate Member, InterfaceOperationKind Kind,
    AstCallable? Body, ResolvedType? ValueType, ImmutableArray<ResolvedType> ParameterTypes,
    ImmutableArray<ResolvedType> Effects, string Signature, bool HasBody)
{
    internal AstExpression? DelegateExpression { get; init; }
    internal ResolvedType? DelegateInterface { get; init; }
    internal ResolvedType? DelegateTargetType { get; init; }
    internal ResolvedType? DelegateOwner { get; init; }
    internal ImmutableArray<ResolvedType> InitializationEffects { get; init; } = [];
    internal BodyBindingStatus ProofStatus { get; init; } = BodyBindingStatus.Bound;
    internal AstParameter? PrimaryProperty { get; init; }
    internal ResolvedType? PrimaryOwner { get; init; }
    internal AstNode? StorageSource { get; init; }
    internal ResolvedType Owner => PrimaryOwner ?? Member.DeclaringType;
    internal AstNode SourceOrigin => StorageSource ?? (AstNode?)DelegateExpression ?? Body ?? (AstNode)Member.Symbol.Parts[0];
}
internal sealed record InterfaceImplementation(ResolvedType Receiver, string Signature,
    ImmutableArray<InterfaceOperation> Contracts, InterfaceOperation? Implementation,
    BodyBindingStatus Status, string? Reason);
internal sealed record InterfaceOperationUse(InterfaceImplementation Selection, bool IsQualifiedSuper);
