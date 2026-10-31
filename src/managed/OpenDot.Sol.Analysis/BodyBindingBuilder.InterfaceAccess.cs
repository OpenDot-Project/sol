// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private readonly Dictionary<AstNode, InterfaceImplementation> _interfaceWrites = [];

    private Value QualifiedInterfaceSuper(AstGenericApplication application)
    {
        var parent = Ancestors(application).FirstOrDefault();
        if (parent is not AstMember && parent?.SyntaxKind != NodeKind.IndexExpression)
        { Report("SOL2238", "Qualified super is only available as an operation receiver.", application); return new(null); }
        var owner = ContainingType(application);
        var arguments = application.TypeArguments.ToArray();
        if (owner is null || arguments.Length != 1)
        { Report("SOL2238", "Qualified super requires one direct base interface.", application); return new(null); }
        var target = Written(arguments[0]);
        if (target.Declaration?.Parts[0].SyntaxKind != NodeKind.InterfaceDeclaration
            || !Types.GetDirectBases(owner, _cancellation).Any(type => ReferenceEquals(type, target))
            || IsQualifiedSuperInitialization(application) || _constructionFrame is not null)
        { Report("SOL2238", "Qualified super must name a direct constructed interface outside construction.", application); return new(null); }
        return new(target);
    }

    private bool IsQualifiedSuperInitialization(AstGenericApplication application)
    {
        AstNode child = application;
        foreach (var parent in Ancestors(application))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (parent is AstTypeDeclaration) { break; }
            if (parent is AstConstructor or AstInitializer || parent.SyntaxKind == NodeKind.BaseList)
            { return true; }
            if (parent is AstProperty property
                && (ReferenceEquals(child, property.Initializer) || child.SyntaxKind == NodeKind.Delegation)
                && Ancestors(property).OfType<AstDeclaration>().FirstOrDefault() is AstTypeDeclaration)
            { return true; }
            child = parent;
        }
        return false;
    }

    private void ValidateQualifiedSuperInitialization()
    {
        // Some initialization bodies remain deferred without being expression-bound. Their
        // qualified-super prohibition still applies to unused declarations.
        foreach (var application in Files.Keys.OfType<AstGenericApplication>())
        {
            _cancellation.ThrowIfCancellationRequested();
            if (application.Target is AstAtom { Spelling: "super" } && IsQualifiedSuperInitialization(application))
            { Report("SOL2238", "Qualified super must name a direct constructed interface outside construction.", application); }
        }
    }

    private IEnumerable<MemberBinding> InterfaceMethodCandidates(ResolvedType receiver, string name,
        AstNode location, IEnumerable<MemberBinding> fallback)
    {
        if (_interfaceResolutionActive.Contains(receiver.Id)) { return fallback; }
        var records = ResolveInterfaceImplementations(receiver, location)
            .Where(record => record.Contracts.Any(operation => operation.Kind == InterfaceOperationKind.Method
                && operation.Member.Symbol.Name == name)).ToArray();
        if (records.Length == 0) { return fallback; }
        var output = new List<MemberBinding>();
        foreach (var record in records)
        {
            if (record.Status == BodyBindingStatus.Invalid)
            { Report("SOL2236", record.Reason ?? "Invalid interface implementation.", location); Defer(location); }
            else if (record.Status == BodyBindingStatus.Deferred) { Defer(location); }
            var selected = record.Implementation ?? record.Contracts.First();
            if (IsQualifiedSuperUse(location) && record.Implementation is null)
            { Report("SOL2238", "Qualified super requires a unique concrete interface body.", location); Defer(location); }
            output.Add(new(selected.Member.Symbol, selected.Member.DeclaringType));
        }
        var covered = records.SelectMany(record => record.Contracts).Select(operation => operation.Member.Symbol).ToHashSet();
        output.AddRange(fallback.Where(member => !covered.Contains(member.Symbol)));
        return output.Distinct().ToArray();
    }

    private Value? InterfacePropertyAccess(ResolvedType receiver, string name, AstNode location,
        State state, Context context, bool read)
    {
        if (_interfaceResolutionActive.Contains(receiver.Id)) { return null; }
        var records = ResolveInterfaceImplementations(receiver, location).Where(record =>
            record.Contracts.Any(operation => operation.Member.Symbol.Parts[0] is AstProperty
                && operation.Member.Symbol.Name == name)).ToArray();
        if (records.Length == 0) { return null; }
        var getter = records.FirstOrDefault(record => record.Contracts[0].Kind == InterfaceOperationKind.Get);
        var setter = records.FirstOrDefault(record => record.Contracts[0].Kind == InterfaceOperationKind.Set);
        if (setter is not null && (!read || IsWriteTarget(location))) { _interfaceWrites[location] = setter; }
        var selected = read ? getter : setter;
        if (selected is null)
        { Report("SOL2237", "The interface property has no requested accessor.", location); return new(null); }
        if (selected.Status != BodyBindingStatus.Bound)
        {
            if (selected.Status == BodyBindingStatus.Invalid)
            { Report("SOL2236", selected.Reason ?? "The interface accessor is unresolved.", location); }
            Defer(location);
        }
        var operation = selected.Implementation ?? selected.Contracts[0];
        if (operation.Member.Symbol.Parts[0] is AstProperty property)
        { ConstructorRead(property, location, state, context, read, operation.Owner); }
        ValidateInterfaceAccess(selected, location, IsQualifiedSuperUse(location));
        if (getter?.Implementation?.ValueType is { } getType && setter?.Implementation?.ValueType is { } setType
            && !ReferenceEquals(getType, setType))
        { Report("SOL2239", "Merged interface accessor value types differ.", location); }
        if (read)
        {
            RecordPotentialCallEdges(state, operation.Effects);
            state.Effects.UnionWith(operation.Effects);
        }
        return new(operation.ValueType, setter is not null && setter.Status == BodyBindingStatus.Bound,
            false, Symbol: operation.Member.Symbol, Receiver: operation.Owner);
    }

    private void ValidateInterfaceAccess(InterfaceImplementation selected, AstNode location, bool qualified)
    {
        var operation = selected.Implementation ?? selected.Contracts[0];
        InterfaceUses[(location, operation.Kind)] = new(selected, qualified);
        if (qualified && (selected.Implementation is null || operation.DelegateExpression is not null))
        { Report("SOL2238", "Qualified super requires a concrete interface body.", location); Defer(location); }
        if (!Accessible(operation.Member.Symbol, location))
        { Report("SOL2214", "The interface operation is inaccessible.", location); }
        if (operation.Body is { } accessor && accessor.Modifiers.Contains("private")
            && ContainingType(location)?.Declaration != operation.Member.DeclaringType.Declaration)
        { Report("SOL2214", "The interface accessor is inaccessible.", location); }
        if (operation.Body is { } protectedAccessor && protectedAccessor.Modifiers.Contains("protected")
            && !ProtectedReceiverAllowed(selected.Receiver, operation.Member.DeclaringType, location))
        { Report("SOL2214", "The interface accessor requires a protected receiver.", location); }
    }

    private static bool IsQualifiedSuperUse(AstNode node)
    {
        if (node.SyntaxKind == NodeKind.IndexExpression && node.Children.FirstOrDefault()
            is AstGenericApplication { Target: AstAtom { Spelling: "super" } }) { return true; }
        AstExpression? expression = node is AstCall call ? call.Target : node as AstExpression;
        if (expression is AstGenericApplication generic) { expression = generic.Target; }
        return expression is AstMember { Receiver: AstGenericApplication { Target: AstAtom { Spelling: "super" } } };
    }
}
