// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

public sealed partial class BodyAnalysis
{
    private readonly HashSet<GenericBodyKey> _activeMemberReturns = [];
    private readonly Dictionary<GenericBodyKey, ResolvedType> _closedOwnerReturnTemplates = [];
    private int _memberReturnInferenceSteps;

    private ResolvedType? ResolveClosedOwnerReturnTemplate(AstFunction function, ResolvedType owner,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Sources.HasErrors || owner.IsError || ContainsTypeParameter(owner)) { return null; }
        var declaration = GenericMethodSourcePolicy.DirectOwner(function, Sources);
        if (declaration is null || function.Body.Kind == AstBodyKind.Missing
            || declaration.SyntaxKind is not (NodeKind.ClassDeclaration or NodeKind.StructDeclaration
                or NodeKind.RefStructDeclaration or NodeKind.InterfaceDeclaration)) { return null; }
        var file = BodySourceIndex.Get(Sources).Files[declaration];
        for (var parent = file.GetParent(declaration); parent is not null; parent = file.GetParent(parent))
        { if (parent is AstTypeDeclaration) { return null; } }
        var key = new GenericBodyKey(function, TypeIds(owner.Arguments), "return-template");
        while (!Monitor.TryEnter(_genericBodyGate, 25)) { cancellation.ThrowIfCancellationRequested(); }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_closedOwnerReturnTemplates.TryGetValue(key, out var prior)) { return prior; }
            if (_memberReturnInferenceSteps >= 1024 || _activeMemberReturns.Count >= 128
                || !_activeMemberReturns.Add(key)) { return null; }
            _memberReturnInferenceSteps++;
            try
            {
                var builder = new BodyBindingBuilder(Sources, Types, cancellation)
                { ClosedMemberReturn = ResolveClosedMemberReturn, ClosedOwnerReturnTemplate = ResolveClosedOwnerReturnTemplate };
                var body = builder.BuildGenericMemberBody(function, owner,
                    Sources.Types.GetTypeParameters(function), returnTemplateOnly: true);
                cancellation.ThrowIfCancellationRequested();
                if (body.Summary.Status != BodyBindingStatus.Bound || !body.Diagnostics.IsEmpty
                    || !body.UnverifiedNodes.IsEmpty || body.Summary.ReturnType is not { } result) { return null; }
                _closedOwnerReturnTemplates.Add(key, result);
                return result;
            }
            catch (TypeSpecializationException) { return null; }
            finally { _activeMemberReturns.Remove(key); }
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }

    private ResolvedType? ResolveClosedMemberReturn(AstFunction function, ResolvedType owner,
        ImmutableArray<ResolvedType> arguments, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Sources.HasErrors || owner.IsError || arguments.Any(type => type.IsError)) { return null; }
        var declaration = GenericMethodSourcePolicy.DirectOwner(function, Sources);
        if (declaration is null || function.Body.Kind == AstBodyKind.Missing
            || declaration.SyntaxKind is not (NodeKind.ClassDeclaration or NodeKind.StructDeclaration
                or NodeKind.RefStructDeclaration or NodeKind.InterfaceDeclaration)) { return null; }
        var file = BodySourceIndex.Get(Sources).Files[declaration];
        for (var parent = file.GetParent(declaration); parent is not null; parent = file.GetParent(parent))
        { if (parent is AstTypeDeclaration) { return null; } }
        var key = new GenericBodyKey(function, TypeIds(owner.Arguments), TypeIds(arguments));
        while (!Monitor.TryEnter(_genericBodyGate, 25)) { cancellation.ThrowIfCancellationRequested(); }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_genericBodies.TryGetValue(key, out var prior)) { return prior.Summary.ReturnType; }
            if (_memberReturnInferenceSteps >= 1024 || _activeMemberReturns.Count >= 128
                || !_activeMemberReturns.Add(key)) { return null; }
            _memberReturnInferenceSteps++;
            try
            {
                var body = GetGenericMemberBody(function, owner, arguments, cancellation);
                return body.Summary.Status == BodyBindingStatus.Bound && body.Diagnostics.IsEmpty
                    && body.UnverifiedNodes.IsEmpty ? body.Summary.ReturnType : null;
            }
            catch (TypeSpecializationException) { return null; }
            finally { _activeMemberReturns.Remove(key); }
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }
}
