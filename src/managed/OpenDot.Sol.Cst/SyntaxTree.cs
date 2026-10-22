// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Cst;

public sealed record SyntaxTree(SourceText Source, SyntaxNode Root, ImmutableArray<SyntaxToken> Tokens,
    ImmutableArray<Diagnostic> Diagnostics, string? ModuleName)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public static SyntaxTree Parse(SourceText source, int errorLimit = 100, int maxDepth = 128,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        return new Parser(Lexer.Lex(source, errorLimit, cancellationToken), errorLimit,
            Math.Min(maxDepth, 256), cancellationToken).Parse();
    }
}
