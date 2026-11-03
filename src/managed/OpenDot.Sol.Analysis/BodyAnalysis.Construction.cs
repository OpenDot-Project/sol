// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

public sealed partial class BodyAnalysis
{
    private readonly Dictionary<(ResolvedType Owner, AstConstructor? Constructor), ConstructorBodyAnalysis> _constructorBodies = [];

    internal ConstructorBodyAnalysis GetConstructorBody(ResolvedType owner, AstConstructor? constructor,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateClosedBodyArgument(owner, nameof(owner));
        if (owner.Kind != ResolvedTypeKind.Nominal || owner.Declaration?.Parts[0] is not AstTypeDeclaration declaration
            || declaration.SyntaxKind != NodeKind.ClassDeclaration || !_nodes.Contains(declaration)
            || constructor is not null && (!_nodes.Contains(constructor) || !owner.Declaration.Parts.OfType<AstTypeDeclaration>()
                .Any(part => part.PrimaryConstructor == constructor || part.Members.Contains(constructor))))
        { throw new ArgumentException("A constructor body requires its canonical source owner and constructor.", nameof(owner)); }
        if (constructor is null && owner.Declaration.Parts.OfType<AstTypeDeclaration>()
            .Any(part => part.PrimaryConstructor is not null || part.Members.OfType<AstConstructor>().Any()))
        { throw new ArgumentException("A type with declared constructors has no implicit constructor.", nameof(constructor)); }
        while (!Monitor.TryEnter(_genericBodyGate, millisecondsTimeout: 25)) { cancellation.ThrowIfCancellationRequested(); }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_constructorBodies.TryGetValue((owner, constructor), out var cached)) { return cached; }
            var builder = new BodyBindingBuilder(Sources, Types, cancellation)
            {
                ClosedMemberReturn = ResolveClosedMemberReturn, ClosedOwnerReturnTemplate = ResolveClosedOwnerReturnTemplate,
                ClosedLexicalReturn = ResolveClosedLexicalReturn, InterfaceSourceBody = GetBody,
                ConstructionSourceStatus = GetConstructionSourceStatus,
            };
            var result = builder.BuildConstructorBody(owner, constructor);
            cancellation.ThrowIfCancellationRequested();
            if (result.IsComplete) { _constructorBodies.Add((owner, constructor), result); }
            return result;
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }

    private BodyBindingStatus? GetConstructionSourceStatus(ResolvedType owner, AstConstructor? constructor)
    {
        Types.ValidateOwnership(owner);
        return ConstructionProofs.Values.Any(proof => proof.Selection.Owner.Declaration == owner.Declaration
            && proof.Selection.Constructor == constructor
            && (proof.Selection.Site == constructor || constructor is null && proof.Selection.Site is AstTypeDeclaration)
            && proof.Status == BodyBindingStatus.Invalid) ? BodyBindingStatus.Invalid : null;
    }
}
