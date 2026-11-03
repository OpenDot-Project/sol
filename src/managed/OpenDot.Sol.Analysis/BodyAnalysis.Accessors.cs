// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public sealed partial class BodyAnalysis
{
    private readonly Dictionary<(ResolvedType Owner, AstAccessor Accessor), AccessorBodyAnalysis> _accessorBodies = [];

    internal AccessorBodyAnalysis GetAccessorBody(ResolvedType owner, AstAccessor accessor, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        cancellation.ThrowIfCancellationRequested();
        ValidateClosedBodyArgument(owner, nameof(owner));
        if (!_nodes.Contains(accessor)) { throw new ArgumentException("The accessor belongs to another source analysis.", nameof(accessor)); }
        var file = Sources.Declarations.Files.Single(snapshot => snapshot.Nodes.Contains(accessor));
        AstNode? parent = file.GetParent(accessor);
        while (parent is not null && parent is not AstTypeDeclaration) { parent = file.GetParent(parent); }
        if (parent is not AstTypeDeclaration declaration || owner.Kind != ResolvedTypeKind.Nominal
            || owner.Declaration != Sources.Declarations.GetDeclaredSymbol(declaration)
            || !(declaration.Members.OfType<AstProperty>().Any(property => property.Accessors.Contains(accessor))
                || declaration.Members.OfType<AstIndexer>().Any(indexer => indexer.Accessors.Contains(accessor))))
        { throw new ArgumentException("An accessor body requires its canonical direct constructed owner.", nameof(owner)); }
        for (parent = file.GetParent(declaration); parent is not null; parent = file.GetParent(parent))
        {
            if (parent is AstTypeDeclaration { Arity: > 0 })
            { throw new ArgumentException("A nested accessor requires an explicit closed lexical owner context.", nameof(owner)); }
        }
        while (!Monitor.TryEnter(_genericBodyGate, millisecondsTimeout: 25)) { cancellation.ThrowIfCancellationRequested(); }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_accessorBodies.TryGetValue((owner, accessor), out var cached)) { return cached; }
            var builder = new BodyBindingBuilder(Sources, Types, cancellation)
            {
                ClosedMemberReturn = ResolveClosedMemberReturn, ClosedOwnerReturnTemplate = ResolveClosedOwnerReturnTemplate,
                ClosedLexicalReturn = ResolveClosedLexicalReturn, InterfaceSourceBody = GetBody,
                ConstructionSourceStatus = GetConstructionSourceStatus,
            };
            var result = builder.BuildAccessorBody(owner, accessor);
            cancellation.ThrowIfCancellationRequested();
            if (result.IsComplete) { _accessorBodies.Add((owner, accessor), result); }
            return result;
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }
}
