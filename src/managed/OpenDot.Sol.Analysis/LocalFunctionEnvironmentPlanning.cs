// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>The source fact represented by one local named-function environment field.</summary>
public enum LocalFunctionEnvironmentFieldKind
{
    Receiver,
    LocalCapture,
}

/// <summary>A lifetime placement candidate, not an allocation decision.</summary>
public enum LocalFunctionEnvironmentLifetimeCandidate
{
    /// <summary>No captured source state requires an environment field.</summary>
    None,
    Stack,
    Heap,
    Unknown,
}

/// <summary>Why local-function source facts do not establish a complete environment candidate.</summary>
public enum LocalFunctionEnvironmentUncertainty
{
    BodyAnalysisIncomplete,
    CaptureTypeUnavailable,
    ReceiverTypeUnavailable,
    ReceiverLookupIncomplete,
    EscapeClassificationUnknown,
    EscapingBorrowedCapture,
    EscapingRefLikeCapture,
    EscapingRefLikeReceiver,
}

/// <summary>
/// A resolved source capture and a candidate representation class. Ordinal is deterministic
/// inventory order; it is not a memory offset or ABI field index.
/// </summary>
public sealed record LocalFunctionEnvironmentField(
    int Ordinal,
    LocalFunctionEnvironmentFieldKind Kind,
    CallableEnvironmentFieldStorage Storage,
    ResolvedType? SemanticType,
    LocalSymbol? Local,
    LocalFunctionCapture? Capture,
    LocalFunctionReceiverCapture? Receiver)
{
    /// <summary>True when body analysis supplied a non-error semantic type for this field.</summary>
    public bool HasSemanticType => SemanticType is not null;
}

/// <summary>
/// Deterministic source fields and a lifetime candidate for one local named function. It contains
/// no memory offsets, object headers, code-pointer representation, GC map, or verified layout.
/// </summary>
public sealed record LocalFunctionEnvironmentPlan(
    AstFunction Function,
    LocalSymbol FunctionLocal,
    LocalFunctionEscape Escape,
    ImmutableArray<LocalFunctionEnvironmentField> Fields,
    LocalFunctionEnvironmentLifetimeCandidate LifetimeCandidate,
    ImmutableArray<LocalFunctionEnvironmentUncertainty> Uncertainties)
{
    /// <summary>Whether all source facts used by this candidate are complete.</summary>
    public bool CandidateFactsComplete => Uncertainties.IsEmpty;

    /// <summary>This source planner never proves a native closure ABI.</summary>
    public bool HasVerifiedAbi => false;

    /// <summary>This source planner never assigns or proves an emitted memory layout.</summary>
    public bool HasVerifiedLayout => false;
}

/// <summary>
/// Combines resolved local captures, local-function escape uses, body summaries, and local types.
/// This source-only plan does not authorize FIR or native lowering.
/// </summary>
public static class LocalFunctionEnvironmentPlanner
{
    /// <summary>Plans every local named function in stable source order.</summary>
    public static ImmutableArray<LocalFunctionEnvironmentPlan> Create(BodyAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        var escapes = LocalFunctionEscapeAnalysis.Create(analysis.Sources.Lexical, cancellationToken);
        return escapes.Escapes.Select(escape => Create(analysis, escape, cancellationToken))
            .ToImmutableArray();
    }

    /// <summary>Plans one local named function owned by the body-analysis snapshot.</summary>
    public static LocalFunctionEnvironmentPlan Create(BodyAnalysis analysis, AstFunction function,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(function);
        cancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        return Create(analysis, analysis.Sources.Lexical.GetLocalFunctionEscape(function), cancellationToken);
    }

    private static LocalFunctionEnvironmentPlan Create(BodyAnalysis analysis, LocalFunctionEscape escape,
        CancellationToken cancellationToken)
    {
        var captures = analysis.Sources.Lexical.GetLocalFunctionCaptures(escape.Function)
            .OrderBy(capture => capture.Local.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(capture => capture.Local.Origin.Span.Start)
            .ThenBy(capture => capture.Local.Id)
            .ToArray();
        var receivers = analysis.Sources.Lexical.GetLocalFunctionReceiverCaptures(escape.Function)
            .OrderBy(receiver => receiver.Owner.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(receiver => receiver.Owner.Origin.Span.Start)
            .ToArray();
        var uncertainties = ImmutableArray.CreateBuilder<LocalFunctionEnvironmentUncertainty>();
        if (analysis.GetBody(escape.Function) is not { Status: BodyBindingStatus.Bound })
        { uncertainties.Add(LocalFunctionEnvironmentUncertainty.BodyAnalysisIncomplete); }
        if (escape.Disposition == LocalFunctionEscapeDisposition.Unknown)
        { uncertainties.Add(LocalFunctionEnvironmentUncertainty.EscapeClassificationUnknown); }

        var fields = ImmutableArray.CreateBuilder<LocalFunctionEnvironmentField>(receivers.Length + captures.Length);
        foreach (var receiver in receivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiverType = ReceiverType(analysis, receiver.Owner);
            if (receiverType is null)
            { uncertainties.Add(LocalFunctionEnvironmentUncertainty.ReceiverTypeUnavailable); }
            if (!receiver.IsComplete)
            { uncertainties.Add(LocalFunctionEnvironmentUncertainty.ReceiverLookupIncomplete); }
            fields.Add(new(fields.Count, LocalFunctionEnvironmentFieldKind.Receiver,
                CallableEnvironmentFieldStorage.ReceiverCapture, receiverType, null, null, receiver));
        }
        foreach (var capture in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = analysis.GetLocalType(capture.Local);
            if (type is null)
            { uncertainties.Add(LocalFunctionEnvironmentUncertainty.CaptureTypeUnavailable); }
            fields.Add(new(fields.Count, LocalFunctionEnvironmentFieldKind.LocalCapture,
                EnvironmentStorage(capture.Local), type, capture.Local, capture, null));
        }

        if (escape.Disposition == LocalFunctionEscapeDisposition.MayEscape
            && fields.Any(field => field.Storage == CallableEnvironmentFieldStorage.BorrowedLocation))
        { uncertainties.Add(LocalFunctionEnvironmentUncertainty.EscapingBorrowedCapture); }
        if (escape.Disposition == LocalFunctionEscapeDisposition.MayEscape
            && fields.Any(field => field.Kind == LocalFunctionEnvironmentFieldKind.LocalCapture
                && field.SemanticType?.IsRefLike == true))
        { uncertainties.Add(LocalFunctionEnvironmentUncertainty.EscapingRefLikeCapture); }
        if (escape.Disposition == LocalFunctionEscapeDisposition.MayEscape
            && fields.Any(field => field.Kind == LocalFunctionEnvironmentFieldKind.Receiver
                && field.SemanticType?.IsRefLike == true))
        { uncertainties.Add(LocalFunctionEnvironmentUncertainty.EscapingRefLikeReceiver); }

        var uniqueUncertainties = uncertainties.Distinct().Order().ToImmutableArray();
        var lifetime = uniqueUncertainties.Length != 0
            ? LocalFunctionEnvironmentLifetimeCandidate.Unknown
            : fields.Count == 0 ? LocalFunctionEnvironmentLifetimeCandidate.None
            : escape.Disposition switch
            {
                LocalFunctionEscapeDisposition.ProvenNonEscaping => LocalFunctionEnvironmentLifetimeCandidate.Stack,
                LocalFunctionEscapeDisposition.MayEscape => LocalFunctionEnvironmentLifetimeCandidate.Heap,
                _ => LocalFunctionEnvironmentLifetimeCandidate.Unknown,
            };

        return new(escape.Function, escape.Local, escape, fields.ToImmutable(), lifetime, uniqueUncertainties);
    }

    private static CallableEnvironmentFieldStorage EnvironmentStorage(LocalSymbol local)
    {
        if (local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out)
        { return CallableEnvironmentFieldStorage.BorrowedLocation; }
        if (local.Kind == LocalSymbolKind.Function)
        { return CallableEnvironmentFieldStorage.NestedCallableEnvironment; }
        return local.IsMutable ? CallableEnvironmentFieldStorage.SharedMutableCell
            : CallableEnvironmentFieldStorage.ValueCopy;
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
}
