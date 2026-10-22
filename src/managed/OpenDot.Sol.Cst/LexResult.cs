// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Cst;

public sealed record LexResult(SourceText Source, ImmutableArray<SyntaxToken> Tokens,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
