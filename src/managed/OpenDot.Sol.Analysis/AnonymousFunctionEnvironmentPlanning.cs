// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>Distinguishes source receiver requirements from captured local declarations.</summary>
public enum AnonymousFunctionEnvironmentFieldKind
{
    Receiver,
    LocalCapture,
}

/// <summary>A lifetime placement to consider; this is not a proved allocation decision.</summary>
public enum AnonymousFunctionEnvironmentLifetimeCandidate
{
    /// <summary>No captured source state requires an environment field.</summary>
    None,
    Stack,
    Heap,
    Unknown,
}

/// <summary>Why source facts were not sufficient to classify an environment candidate.</summary>
public enum AnonymousFunctionEnvironmentUncertainty
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
/// An ordered source field candidate. Ordinal is only a deterministic source-plan order; it is
/// neither a byte offset nor an ABI field index.
/// </summary>
public sealed record AnonymousFunctionEnvironmentField(
    int Ordinal,
    AnonymousFunctionEnvironmentFieldKind Kind,
    CallableEnvironmentFieldStorage Storage,
    ResolvedType? SemanticType,
    LocalSymbol? Local,
    AnonymousFunctionCapture? Capture,
    AnonymousFunctionReceiverCapture? Receiver)
{
    /// <summary>True when source analysis supplied a semantic type for this candidate field.</summary>
    public bool HasSemanticType => SemanticType is not null;
}

/// <summary>
/// A source-only environment inventory and lifetime candidate. It deliberately carries no
/// offsets, alignment, object header, GC map, function pointer shape, or verified ABI claim.
/// </summary>
public sealed record AnonymousFunctionEnvironmentPlan(
    AstAnonymousFunction Function,
    AnonymousFunctionEscape Escape,
    ImmutableArray<AnonymousFunctionEnvironmentField> Fields,
    AnonymousFunctionEnvironmentLifetimeCandidate LifetimeCandidate,
    ImmutableArray<AnonymousFunctionEnvironmentUncertainty> Uncertainties)
{
    /// <summary>Whether the source candidate has no known fact gaps.</summary>
    public bool CandidateFactsComplete => Uncertainties.IsEmpty;

    /// <summary>This source planner never proves a native closure ABI.</summary>
    public bool HasVerifiedAbi => false;

    /// <summary>This source planner never assigns or proves an emitted memory layout.</summary>
    public bool HasVerifiedLayout => false;
}

/// <summary>
/// Combines lexical captures, receiver requirements, escape classification, and resolved local
/// types into deterministic environment candidates. It does not authorize code generation.
/// </summary>
public static class AnonymousFunctionEnvironmentPlanner
{
    /// <summary>Plans every anonymous function in the body-analysis snapshot in source order.</summary>
    public static ImmutableArray<AnonymousFunctionEnvironmentPlan> Create(
        BodyAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();

        return analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstAnonymousFunction>()
            .OrderBy(function => function.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(function => function.Origin.Span.Start)
            .Select(function => Create(analysis, function, cancellationToken))
            .ToImmutableArray();
    }

    /// <summary>Plans one function owned by the supplied body-analysis snapshot.</summary>
    public static AnonymousFunctionEnvironmentPlan Create(
        BodyAnalysis analysis, AstAnonymousFunction function, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(function);
        cancellationToken.ThrowIfCancellationRequested();

        var lexical = analysis.Sources.Lexical;
        var escape = lexical.GetAnonymousFunctionEscape(function);
        var captures = lexical.GetAnonymousFunctionCaptures(function)
            .OrderBy(capture => capture.Local.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(capture => capture.Local.Origin.Span.Start)
            .ThenBy(capture => capture.Local.Id)
            .ToArray();
        var receivers = lexical.GetAnonymousFunctionReceiverCaptures(function)
            .OrderBy(receiver => receiver.Owner.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(receiver => receiver.Owner.Origin.Span.Start)
            .ThenBy(receiver => receiver.Owner.Name, StringComparer.Ordinal)
            .ToArray();

        var uncertainties = ImmutableArray.CreateBuilder<AnonymousFunctionEnvironmentUncertainty>();
        if (analysis.GetAnonymousFunctionBody(function) is not { IsComplete: true })
        { uncertainties.Add(AnonymousFunctionEnvironmentUncertainty.BodyAnalysisIncomplete); }
        if (escape.Disposition == AnonymousFunctionEscapeDisposition.Unknown)
        { uncertainties.Add(AnonymousFunctionEnvironmentUncertainty.EscapeClassificationUnknown); }

        var fields = ImmutableArray.CreateBuilder<AnonymousFunctionEnvironmentField>(receivers.Length + captures.Length);
        foreach (var receiver in receivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiverType = ReceiverType(analysis, receiver.Owner);
            if (receiverType is null)
            { uncertainties.Add(AnonymousFunctionEnvironmentUncertainty.ReceiverTypeUnavailable); }
            if (!receiver.IsComplete)
            { uncertainties.Add(AnonymousFunctionEnvironmentUncertainty.ReceiverLookupIncomplete); }
            fields.Add(new(fields.Count, AnonymousFunctionEnvironmentFieldKind.Receiver,
                CallableEnvironmentFieldStorage.ReceiverCapture, receiverType, null, null, receiver));
        }

        foreach (var capture in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = analysis.GetLocalType(capture.Local);
            if (type is null)
            { uncertainties.Add(AnonymousFunctionEnvironmentUncertainty.CaptureTypeUnavailable); }
            fields.Add(new(fields.Count, AnonymousFunctionEnvironmentFieldKind.LocalCapture,
                EnvironmentStorage(capture.Storage), type, capture.Local, capture, null));
        }

        bool borrowed = fields.Any(field => field.Storage == CallableEnvironmentFieldStorage.BorrowedLocation);
        if (escape.Disposition == AnonymousFunctionEscapeDisposition.MayEscape && borrowed)
        { uncertainties.Add(AnonymousFunctionEnvironmentUncertainty.EscapingBorrowedCapture); }
        if (escape.Disposition == AnonymousFunctionEscapeDisposition.MayEscape
            && fields.Any(field => field.Kind == AnonymousFunctionEnvironmentFieldKind.Receiver
                && field.SemanticType?.IsRefLike == true))
        { uncertainties.Add(AnonymousFunctionEnvironmentUncertainty.EscapingRefLikeReceiver); }

        var uniqueUncertainties = uncertainties.Distinct().Order().ToImmutableArray();
        var lifetime = uniqueUncertainties.Length != 0
            ? AnonymousFunctionEnvironmentLifetimeCandidate.Unknown
            : fields.Count == 0 ? AnonymousFunctionEnvironmentLifetimeCandidate.None
            : escape.Disposition switch
            {
                AnonymousFunctionEscapeDisposition.ProvenNonEscaping => AnonymousFunctionEnvironmentLifetimeCandidate.Stack,
                AnonymousFunctionEscapeDisposition.MayEscape => AnonymousFunctionEnvironmentLifetimeCandidate.Heap,
                _ => AnonymousFunctionEnvironmentLifetimeCandidate.Unknown,
            };

        return new(function, escape, fields.ToImmutable(), lifetime, uniqueUncertainties);
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

    private static CallableEnvironmentFieldStorage EnvironmentStorage(AnonymousFunctionCaptureStorage storage) =>
        storage switch
        {
            AnonymousFunctionCaptureStorage.ValueCopy => CallableEnvironmentFieldStorage.ValueCopy,
            AnonymousFunctionCaptureStorage.SharedMutableCell => CallableEnvironmentFieldStorage.SharedMutableCell,
            AnonymousFunctionCaptureStorage.BorrowedLocation => CallableEnvironmentFieldStorage.BorrowedLocation,
            AnonymousFunctionCaptureStorage.NestedCallableEnvironment => CallableEnvironmentFieldStorage.NestedCallableEnvironment,
            _ => throw new System.ArgumentOutOfRangeException(nameof(storage)),
        };
}
