// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Psi;

/// <summary>Indexes explicit syntax snapshots by logical module name; does not resolve imports or merge symbols.</summary>
public sealed class PsiWorkspace
{
    private PsiWorkspace(ImmutableArray<PsiSnapshot> files)
    {
        Files = files;
        Modules = files.Where(f => !string.IsNullOrEmpty(f.Root.ModuleName)).GroupBy(f => f.Root.ModuleName!, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Select(f => f.Root).ToImmutableArray(), StringComparer.Ordinal);
    }

    public ImmutableArray<PsiSnapshot> Files { get; }
    public ImmutableDictionary<string, ImmutableArray<PsiFile>> Modules { get; }

    public static PsiWorkspace Create(IEnumerable<SyntaxTree> trees, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trees);
        cancellationToken.ThrowIfCancellationRequested();
        return new(trees.Select(tree => PsiSnapshot.Create(tree, cancellationToken)).ToImmutableArray());
    }
}
