// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Cst;

public sealed record SyntaxTrivia(TriviaKind Kind, TextSpan Span);

/// <summary>A lossless token; all ranges belong to the containing lex result's source.</summary>
public sealed record SyntaxToken(SyntaxKind Kind, TextSpan Span, ImmutableArray<SyntaxTrivia> LeadingTrivia)
{
    public TextSpan FullSpan => TextSpan.FromBounds(
        LeadingTrivia.IsEmpty ? Span.Start : LeadingTrivia[0].Span.Start, Span.End);
}
