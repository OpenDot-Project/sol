// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public enum CallBindingStatus { Bound, NoApplicableCandidate, Ambiguous, Deferred, Unresolved }
public enum CallCandidateStatus { Applicable, Rejected, Deferred }

/// <summary>One source argument mapped to a parameter, retaining source evaluation order.</summary>
public sealed record CallArgumentBinding(int ArgumentIndex, int ParameterIndex, bool IsExpandedVararg, bool IsSpread)
{
    public bool IsArrayArgument { get; init; }
    public bool IsTrailingLambda { get; init; }
}

/// <summary>Evaluated only when omitted, after explicit arguments and in declaration order.</summary>
public sealed record CallDefaultArgument(int ParameterIndex, AstCallable DeclaredBy, AstNode Expression);

public sealed record CallCandidate(
    DeclarationSymbol Symbol,
    CallCandidateStatus Status,
    string? Reason,
    ImmutableArray<ResolvedType> TypeArguments,
    ImmutableArray<ResolvedType> ParameterTypes,
    ResolvedType? ReturnType,
    ImmutableArray<ResolvedType> Effects,
    ImmutableArray<CallArgumentBinding> Arguments,
    ImmutableArray<int> DefaultParameters,
    ImmutableArray<int> ConversionRanks,
    bool ExpandsVararg)
{
    public ImmutableArray<int> EmptyVarargParameters { get; init; } = [];
    public ImmutableArray<CallDefaultArgument> DefaultArguments { get; init; } = [];
}

/// <summary>Source call selection, without flow, exception coverage, or executable lowering.</summary>
public sealed record CallBinding(AstCall Call, CallBindingStatus Status, CallCandidate? Selected,
    ImmutableArray<CallCandidate> Candidates, string? Reason);
