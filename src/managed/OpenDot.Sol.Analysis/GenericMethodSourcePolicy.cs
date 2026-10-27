// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

/// <summary>Source shape eligible for receiver-free closed FIR method instances.</summary>
internal static class GenericMethodSourcePolicy
{
    private static readonly ConditionalWeakTable<SourceAnalysis, SourceIndex> SourceIndexes = new();

    internal static bool IsSupportedGenericObjectMethod(AstFunction function, SourceAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(analysis);
        if (!function.TypeParameters.Any() || function.ReturnsByReference || function.Body.Kind == AstBodyKind.Missing
            || function.IsCheckedOperator || function.Origin.Element.Tokens.Any(token => token.Kind == SyntaxKind.OperatorKeyword)
            || function.Modifiers.Any(IsVirtualModifier) || function.Annotations.Any())
        { return false; }

        var owner = DirectOwner(function, analysis);
        var file = SourceFile(function, analysis);
        if (file is null || owner is null || !owner.Members.Any(member => ReferenceEquals(member, function))) { return false; }
        for (var parent = file.GetParent(owner); parent is not null; parent = file.GetParent(parent))
        { if (parent is AstTypeDeclaration) { return false; } }

        var ownerSymbol = analysis.Declarations.GetDeclaredSymbol(owner);
        if (ownerSymbol is null || ownerSymbol.Parts.IsEmpty) { return false; }
        foreach (var part in ownerSymbol.Parts)
        {
            if (part is not AstTypeDeclaration type || type.SyntaxKind != NodeKind.ObjectDeclaration
                || type.IsCompanion || type.Arity != 0 || type.PrimaryConstructor is not null
                || type.Bases.Any() || type.HasInitialization || type.Annotations.Any()
                || type.Members.Any(member => member is not AstFunction))
            { return false; }

            foreach (var member in type.Members.OfType<AstFunction>())
            {
                if (member.Body.Kind == AstBodyKind.Missing || member.ReturnsByReference
                    || member.IsCheckedOperator
                    || member.Origin.Element.Tokens.Any(token => token.Kind == SyntaxKind.OperatorKeyword)
                    || member.Modifiers.Any(IsVirtualModifier) || member.Annotations.Any()
                    || member.Descendants().Prepend(member).OfType<AstAtom>()
                        .Any(atom => atom.Spelling is "this" or "super"))
                { return false; }
            }
        }

        return function.Descendants().Prepend(function).OfType<AstAtom>()
            .All(atom => atom.Spelling is not ("this" or "super"));
    }

    internal static AstTypeDeclaration? DirectOwner(AstFunction function, SourceAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(analysis);
        return SourceIndexes.GetValue(analysis, static source => new SourceIndex(source)).DirectOwner(function);
    }

    internal static bool IsSupportedGenericMethodCall(AstCall call, AstFunction caller,
        AstFunction target, BodyCallBinding binding, SourceAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(analysis);
        if (!IsSupportedGenericObjectMethod(target, analysis)) { return false; }
        var targetOwner = DirectOwner(target, analysis);
        var callerOwner = DirectOwner(caller, analysis);
        var ownerSymbol = targetOwner is null ? null : analysis.Declarations.GetDeclaredSymbol(targetOwner);
        if (ownerSymbol is null || binding.Target != target
            || binding.Receiver is not { Kind: ResolvedTypeKind.Nominal } receiver
            || !ReferenceEquals(receiver.Declaration, ownerSymbol))
        { return false; }

        var targetExpression = call.Target is AstGenericApplication application ? application.Target : call.Target;
        if (targetExpression is AstAtom atom)
        {
            return atom.Spelling == target.Name && callerOwner is not null
                && ReferenceEquals(analysis.Declarations.GetDeclaredSymbol(callerOwner), ownerSymbol)
                && !IsLocalName(atom, analysis);
        }

        return targetExpression is AstMember { IsSafe: false, Name: var name, Receiver: { } receiverExpression }
            && name == target.Name && IsDirectSingletonQualifier(receiverExpression, targetOwner!, analysis);
    }

    private static bool IsDirectSingletonQualifier(AstExpression expression, AstTypeDeclaration owner,
        SourceAnalysis analysis)
    {
        string? finalName = null;
        var current = expression;
        while (current is AstMember { IsSafe: false, Name: { } name, Receiver: { } receiver })
        {
            finalName ??= name;
            current = receiver;
        }
        if (current is not AstAtom { SyntaxKind: NodeKind.NameExpression, Spelling: { Length: > 0 } root }
            || IsLocalName((AstAtom)current, analysis))
        { return false; }
        return owner.Name is { Length: > 0 } ownerName && (finalName ?? root) == ownerName;
    }

    private static bool IsLocalName(AstAtom atom, SourceAnalysis analysis)
    {
        var file = SourceFile(atom, analysis);
        return file is null || analysis.Lexical.Lookup(atom, atom.Spelling).IsLocal;
    }

    private static AstSnapshot? SourceFile(AstNode node, SourceAnalysis analysis) =>
        SourceIndexes.GetValue(analysis, static source => new SourceIndex(source)).SourceFile(node);

    private static bool IsVirtualModifier(string modifier) => modifier is "open" or "abstract" or "override";

    private sealed class SourceIndex
    {
        private readonly Dictionary<object, AstSnapshot> _filesByPsi = new(ReferenceEqualityComparer.Instance);
        private readonly ConditionalWeakTable<AstFunction, OwnerCache> _owners = new();

        internal SourceIndex(SourceAnalysis analysis)
        {
            foreach (var file in analysis.Declarations.Files) { _filesByPsi.Add(file.Psi, file); }
        }

        internal AstSnapshot? SourceFile(AstNode node) =>
            _filesByPsi.TryGetValue(node.Origin.Element.Snapshot, out var file) ? file : null;

        internal AstTypeDeclaration? DirectOwner(AstFunction function) =>
            _owners.GetValue(function, FindOwner).Owner;

        private OwnerCache FindOwner(AstFunction function)
        {
            var file = SourceFile(function);
            for (var parent = file?.GetParent(function); parent is not null; parent = file!.GetParent(parent))
            { if (parent is AstTypeDeclaration owner) { return new(owner); } }
            return new(null);
        }

        private sealed record OwnerCache(AstTypeDeclaration? Owner);
    }
}
