// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>Source shape eligible for receiver-free generic object-method function values.</summary>
internal static class GenericObjectMethodFunctionValuePolicy
{
    internal static bool IsSupportedSource(AstExpression source, AstFunction target, SourceAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(analysis);
        if (!GenericMethodSourcePolicy.IsSupportedGenericObjectMethod(target, analysis)
            || GenericMethodSourcePolicy.DirectOwner(target, analysis) is not { } owner
            || analysis.Declarations.GetDeclaredSymbol(owner) is not { } ownerSymbol)
        { return false; }

        var file = analysis.Declarations.Files.SingleOrDefault(candidate =>
            ReferenceEquals(candidate.Psi, source.Origin.Element.Snapshot));
        if (file is null) { return false; }

        if (source is AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression } atom)
        {
            if (atom.Spelling != target.Name || analysis.Lexical.Lookup(atom, atom.Spelling).IsLocal)
            { return false; }

            var caller = EnclosingFunction(atom, file);
            var callerOwner = caller is null ? null : GenericMethodSourcePolicy.DirectOwner(caller, analysis);
            return callerOwner is not null
                && ReferenceEquals(analysis.Declarations.GetDeclaredSymbol(callerOwner), ownerSymbol);
        }

        if (source is not AstMember { IsSafe: false, Name: var name, Receiver: { } receiver }
            || name != target.Name || !TryGetNamePath(receiver, out var root, out var path))
        { return false; }

        var rootLookup = analysis.Lexical.Lookup(root, root.Spelling);
        if (rootLookup.IsLocal) { return false; }

        if (path.Count == 1)
        {
            var candidates = rootLookup.Declarations.Candidates;
            return candidates.Length == 1 && candidates[0].Kind == SymbolKind.Type
                && ReferenceEquals(candidates[0], ownerSymbol);
        }

        // A multi-segment qualifier is accepted only when it resolves through the source import
        // table. A declaration with the same root name shadows an import qualifier.
        if (!rootLookup.IsMissing) { return false; }
        var qualifier = string.Join('.', path.Take(path.Count - 1));
        var resolved = analysis.Declarations.LookupQualified(file.Root, qualifier, path[^1]).Candidates;
        return resolved.Length == 1 && resolved[0].Kind == SymbolKind.Type
            && ReferenceEquals(resolved[0], ownerSymbol);
    }

    private static AstFunction? EnclosingFunction(AstNode node, AstSnapshot file)
    {
        for (var parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        { if (parent is AstFunction function) { return function; } }
        return null;
    }

    private static bool TryGetNamePath(AstExpression expression, out AstAtom root,
        out IReadOnlyList<string> path)
    {
        var suffix = new Stack<string>();
        var current = expression;
        while (current is AstMember { IsSafe: false, Name: { Length: > 0 } name, Receiver: { } receiver })
        {
            suffix.Push(name);
            current = receiver;
        }
        if (current is not AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression,
            Spelling: { Length: > 0 } })
        {
            root = null!;
            path = [];
            return false;
        }

        root = (AstAtom)current;
        var parts = new List<string>(suffix.Count + 1) { root.Spelling };
        while (suffix.TryPop(out var part)) { parts.Add(part); }
        path = parts;
        return true;
    }
}
