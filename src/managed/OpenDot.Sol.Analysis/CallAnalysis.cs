// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

/// <summary>
/// Binds direct module functions with written signatures. Unsupported receiver, inference, and
/// conversion paths remain explicitly deferred. This optional analysis is not a verified FIR.
/// </summary>
public sealed class CallAnalysis
{
    private readonly Dictionary<AstCall, CallBinding> _bindings;

    private CallAnalysis(CallBindingBuilder builder)
    {
        Sources = builder.Sources;
        Types = builder.Types;
        _bindings = builder.Bindings;
        Bindings = Sources.Declarations.Files.SelectMany(f => f.Nodes.OfType<AstCall>()
            .OrderBy(c => c.Origin.Span.Start).ThenBy(c => c.Origin.Span.Length)).Select(c => _bindings[c]).ToImmutableArray();
        Diagnostics = builder.Diagnostics.OrderBy(d => d.Source.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Start).ThenBy(d => d.Code, StringComparer.Ordinal).ToImmutableArray();
    }

    public SourceAnalysis Sources { get; }
    public TypeSpecialization Types { get; }
    public ImmutableArray<CallBinding> Bindings { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool HasErrors => Sources.HasErrors || !Diagnostics.IsEmpty;
    public bool IsComplete => !Sources.HasErrors && Bindings.All(b => b.Status == CallBindingStatus.Bound);

    public static CallAnalysis Create(SourceAnalysis sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new CallBindingBuilder(sources, cancellationToken);
        builder.Build();
        return new(builder);
    }

    public CallBinding GetBinding(AstCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _bindings.TryGetValue(call, out var binding) ? binding
            : throw new ArgumentException("The call is not owned by this analysis.", nameof(call));
    }
}
