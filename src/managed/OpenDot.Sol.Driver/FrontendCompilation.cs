// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Driver;

/// <summary>Parses an explicit source set and indexes logical module names independently of paths.</summary>
public sealed record FrontendCompilation(ImmutableArray<SyntaxTree> Trees,
    ImmutableDictionary<string, ImmutableArray<SyntaxTree>> Modules)
{
    public bool HasErrors => Trees.Any(tree => tree.HasErrors);
    public IEnumerable<Diagnostic> Diagnostics => Trees.SelectMany(tree => tree.Diagnostics);

    public ImmutableArray<AstSnapshot> CreateAst(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Trees.Select(tree => AstSnapshot.Create(PsiSnapshot.Create(tree, cancellationToken),
            cancellationToken)).ToImmutableArray();
    }

    public AnalysisSession AnalyzeDeclarations(AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return AnalysisSession.Create(CreateAst(cancellationToken), options, cancellationToken);
    }

    public SourceAnalysis AnalyzeSources(AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return SourceAnalysis.Create(AnalyzeDeclarations(options, cancellationToken), cancellationToken);
    }

    public CallAnalysis AnalyzeCalls(AnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        return CallAnalysis.Create(AnalyzeSources(options, cancellationToken), cancellationToken);
    }

    public static FrontendCompilation Parse(IEnumerable<SourceText> sources, int errorLimit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var trees = sources.Select(source => SyntaxTree.Parse(source, errorLimit,
            cancellationToken: cancellationToken)).ToImmutableArray();
        var modules = trees.Where(t => !string.IsNullOrEmpty(t.ModuleName))
            .GroupBy(t => t.ModuleName!, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.Ordinal);
        return new(trees, modules);
    }
}
