// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Fir;

public sealed record FirCompilation(VerifiedFirProgram? Program, ImmutableArray<Diagnostic> Diagnostics)
{
    public bool HasErrors => Program is null;
    public static FirCompilation Create(IEnumerable<AstSnapshot> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = RuntimeCatalog.Create();
        var input = sources.ToImmutableArray();
        if (input.IsEmpty) { throw new ArgumentException("FIR compilation requires sources.", nameof(sources)); }
        var session = AnalysisSession.Create(input.AddRange(catalog.Files), cancellationToken: cancellationToken);
        var bodies = BodyAnalysis.Create(SourceAnalysis.Create(session, cancellationToken), cancellationToken);
        if (bodies.HasErrors) { return new(null, bodies.Diagnostics); }
        var builder = new FirBuilder(bodies, catalog, input, cancellationToken);
        var program = builder.Build();
        if (builder.Diagnostics.Count != 0) { return new(null, builder.Diagnostics.ToImmutableArray()); }
        var verification = FirVerifier.Verify(program, cancellationToken);
        return new(verification.Program, verification.Diagnostics);
    }
}
