// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

/// <summary>Resolves written type references to session-owned identities. It does not type-check expressions.</summary>
public sealed class TypeAnalysis
{
    private readonly Dictionary<AstType, ResolvedType> _references;
    private readonly Dictionary<AstDeclaration, ImmutableArray<ResolvedType>> _parameters;

    private TypeAnalysis(TypeBindingBuilder builder)
    {
        Declarations = builder.Session;
        _references = builder.References;
        _parameters = builder.Parameters;
        Types = builder.Types.ToImmutableArray();
        Diagnostics = builder.Diagnostics.OrderBy(d => d.Source.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Start).ThenBy(d => d.Code, StringComparer.Ordinal).ToImmutableArray();
    }

    public AnalysisSession Declarations { get; }
    public ImmutableArray<ResolvedType> Types { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool HasErrors => Declarations.HasErrors || Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public static TypeAnalysis Create(AnalysisSession declarations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new TypeBindingBuilder(declarations, cancellationToken);
        builder.Build();
        return new(builder);
    }

    public ResolvedType GetType(AstType reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return _references.TryGetValue(reference, out var type) ? type
            : throw new ArgumentException("The type reference is not owned by this analysis.", nameof(reference));
    }

    public ImmutableArray<ResolvedType> GetTypeParameters(AstDeclaration owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _parameters.TryGetValue(owner, out var parameters) ? parameters
            : throw new ArgumentException("The declaration is not owned by this analysis.", nameof(owner));
    }

    public void ValidateOwnership(ResolvedType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Id < 0 || type.Id >= Types.Length || !ReferenceEquals(Types[type.Id], type))
        {
            throw new ArgumentException("The type belongs to another analysis.", nameof(type));
        }
    }
}
