// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>Separates the enclosing receiver from captured lambda locals.</summary>
public enum LambdaEnvironmentFieldKind
{
    Receiver,
    LocalCapture,
}

/// <summary>A candidate placement inferred from source facts, not an allocation decision.</summary>
public enum LambdaEnvironmentLifetimeCandidate
{
    /// <summary>No captured source state requires an environment field.</summary>
    None,
    Stack,
    Heap,
    Unknown,
}

/// <summary>Source-analysis gaps that prevent a fully classified lambda environment candidate.</summary>
public enum LambdaEnvironmentUncertainty
{
    BodyAnalysisIncomplete,
    CaptureTypeUnavailable,
    ReceiverTypeUnavailable,
    ReceiverLookupIncomplete,
    EscapeClassificationUnknown,
    EscapingBorrowedCapture,
    EscapingRefLikeReceiver,
}

/// <summary>
/// A source-level environment field candidate. Ordinal is deterministic ordering only; it is not
/// a byte offset, native field index, or ABI statement.
/// </summary>
public sealed record LambdaEnvironmentField(
    int Ordinal,
    LambdaEnvironmentFieldKind Kind,
    CallableEnvironmentFieldStorage Storage,
    ResolvedType? SemanticType,
    LocalSymbol? Local,
    LambdaCapture? Capture,
    LambdaReceiverCapture? Receiver)
{
    /// <summary>True when semantic analysis supplied a non-error type for the candidate.</summary>
    public bool HasSemanticType => SemanticType is not null;
}

/// <summary>
/// A deterministic, source-only inventory and lifetime candidate. It contains no memory offsets,
/// object header, GC map, callable pointer shape, or verified runtime layout.
/// </summary>
public sealed record LambdaEnvironmentPlan(
    AstLambda Lambda,
    LambdaEscape Escape,
    ImmutableArray<LambdaEnvironmentField> Fields,
    LambdaEnvironmentLifetimeCandidate LifetimeCandidate,
    ImmutableArray<LambdaEnvironmentUncertainty> Uncertainties)
{
    /// <summary>Whether source facts supplied enough information for the candidate classification.</summary>
    public bool CandidateFactsComplete => Uncertainties.IsEmpty;

    /// <summary>This source planner never establishes a native closure ABI.</summary>
    public bool HasVerifiedAbi => false;

    /// <summary>This source planner never establishes an emitted memory layout.</summary>
    public bool HasVerifiedLayout => false;
}

/// <summary>
/// Combines lambda capture, receiver, escape, and resolved-local-type facts without making a
/// code-generation or GC-layout decision.
/// </summary>
public static class LambdaEnvironmentPlanner
{
    /// <summary>Plans all lambdas in source-path and source-span order.</summary>
    public static ImmutableArray<LambdaEnvironmentPlan> Create(
        BodyAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();

        return analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstLambda>()
            .OrderBy(lambda => lambda.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(lambda => lambda.Origin.Span.Start)
            .Select(lambda => Create(analysis, lambda, cancellationToken))
            .ToImmutableArray();
    }

    /// <summary>Plans one lambda owned by the supplied body-analysis snapshot.</summary>
    public static LambdaEnvironmentPlan Create(
        BodyAnalysis analysis, AstLambda lambda, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(lambda);
        cancellationToken.ThrowIfCancellationRequested();

        var lexical = analysis.Sources.Lexical;
        var escape = lexical.GetLambdaEscape(lambda);
        var captures = lexical.GetLambdaCaptures(lambda)
            .OrderBy(capture => capture.Local.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(capture => capture.Local.Origin.Span.Start)
            .ThenBy(capture => capture.Local.Id)
            .ToArray();
        var receivers = lexical.GetLambdaReceiverCaptures(lambda)
            .OrderBy(receiver => receiver.Owner.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(receiver => receiver.Owner.Origin.Span.Start)
            .ThenBy(receiver => receiver.Owner.Name, StringComparer.Ordinal)
            .ToArray();

        var uncertainties = ImmutableArray.CreateBuilder<LambdaEnvironmentUncertainty>();
        if (analysis.GetLambdaBody(lambda) is not { IsComplete: true })
        { uncertainties.Add(LambdaEnvironmentUncertainty.BodyAnalysisIncomplete); }
        if (escape.Disposition == LambdaEscapeDisposition.Unknown)
        { uncertainties.Add(LambdaEnvironmentUncertainty.EscapeClassificationUnknown); }

        var fields = ImmutableArray.CreateBuilder<LambdaEnvironmentField>(receivers.Length + captures.Length);
        foreach (var receiver in receivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiverType = ReceiverType(analysis, receiver.Owner);
            if (receiverType is null)
            { uncertainties.Add(LambdaEnvironmentUncertainty.ReceiverTypeUnavailable); }
            if (!receiver.IsComplete)
            { uncertainties.Add(LambdaEnvironmentUncertainty.ReceiverLookupIncomplete); }
            fields.Add(new(fields.Count, LambdaEnvironmentFieldKind.Receiver,
                CallableEnvironmentFieldStorage.ReceiverCapture, receiverType, null, null, receiver));
        }

        foreach (var capture in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = analysis.GetLocalType(capture.Local);
            if (type is null)
            { uncertainties.Add(LambdaEnvironmentUncertainty.CaptureTypeUnavailable); }
            fields.Add(new(fields.Count, LambdaEnvironmentFieldKind.LocalCapture,
                EnvironmentStorage(capture.Storage), type, capture.Local, capture, null));
        }

        bool borrowed = fields.Any(field => field.Storage == CallableEnvironmentFieldStorage.BorrowedLocation);
        if (escape.Disposition == LambdaEscapeDisposition.MayEscape && borrowed)
        { uncertainties.Add(LambdaEnvironmentUncertainty.EscapingBorrowedCapture); }
        if (escape.Disposition == LambdaEscapeDisposition.MayEscape
            && fields.Any(field => field.Kind == LambdaEnvironmentFieldKind.Receiver
                && field.SemanticType?.IsRefLike == true))
        { uncertainties.Add(LambdaEnvironmentUncertainty.EscapingRefLikeReceiver); }

        var uniqueUncertainties = uncertainties.Distinct().Order().ToImmutableArray();
        var lifetime = uniqueUncertainties.Length != 0
            ? LambdaEnvironmentLifetimeCandidate.Unknown
            : fields.Count == 0 ? LambdaEnvironmentLifetimeCandidate.None
            : escape.Disposition switch
            {
                LambdaEscapeDisposition.ProvenNonEscaping => LambdaEnvironmentLifetimeCandidate.Stack,
                LambdaEscapeDisposition.MayEscape => LambdaEnvironmentLifetimeCandidate.Heap,
                _ => LambdaEnvironmentLifetimeCandidate.Unknown,
            };

        return new(lambda, escape, fields.ToImmutable(), lifetime, uniqueUncertainties);
    }

    private static ResolvedType? ReceiverType(BodyAnalysis analysis, AstTypeDeclaration owner)
    {
        var symbol = analysis.Sources.Declarations.GetDeclaredSymbol(owner);
        if (symbol is null) { return null; }
        try
        {
            return analysis.Types.Nominal(symbol, analysis.Sources.Types.GetTypeParameters(owner));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (TypeSpecializationException)
        {
            return null;
        }
    }

    private static CallableEnvironmentFieldStorage EnvironmentStorage(LambdaCaptureStorage storage) =>
        storage switch
        {
            LambdaCaptureStorage.ValueCopy => CallableEnvironmentFieldStorage.ValueCopy,
            LambdaCaptureStorage.SharedMutableCell => CallableEnvironmentFieldStorage.SharedMutableCell,
            LambdaCaptureStorage.BorrowedLocation => CallableEnvironmentFieldStorage.BorrowedLocation,
            LambdaCaptureStorage.NestedCallableEnvironment => CallableEnvironmentFieldStorage.NestedCallableEnvironment,
            _ => throw new System.ArgumentOutOfRangeException(nameof(storage)),
        };
}
