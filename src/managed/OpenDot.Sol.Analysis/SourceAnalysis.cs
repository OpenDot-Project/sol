// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

/// <summary>Composes declaration, written-type, and lexical analyses over the same immutable source set.</summary>
public sealed class SourceAnalysis
{
    private SourceAnalysis(AnalysisSession declarations, TypeAnalysis types, LexicalAnalysis lexical, DefaultValueAnalysis defaults)
    {
        Declarations = declarations;
        Types = types;
        Lexical = lexical;
        Defaults = defaults;
        Diagnostics = declarations.Diagnostics.Concat(types.Diagnostics).Concat(lexical.Diagnostics).Concat(defaults.Diagnostics)
            .OrderBy(d => d.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Span.Start)
            .ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal)
            .Take(declarations.Options.DiagnosticLimit).ToImmutableArray();
    }

    public AnalysisSession Declarations { get; }
    public TypeAnalysis Types { get; }
    public LexicalAnalysis Lexical { get; }
    public DefaultValueAnalysis Defaults { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool HasErrors => Declarations.HasErrors || Types.HasErrors || Lexical.HasErrors || Defaults.HasErrors;

    public static SourceAnalysis Create(AnalysisSession declarations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        cancellationToken.ThrowIfCancellationRequested();
        var types = TypeAnalysis.Create(declarations, cancellationToken);
        var lexical = LexicalAnalysis.Create(declarations, types, cancellationToken);
        return new(declarations, types, lexical, DefaultValueAnalysis.Create(types, lexical, cancellationToken));
    }
}
