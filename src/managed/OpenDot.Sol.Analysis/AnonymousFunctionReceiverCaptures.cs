// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

/// <summary>Source operations that require an enclosing nominal receiver.</summary>
[Flags]
public enum AnonymousFunctionReceiverAccess
{
    /// <summary>The body explicitly references <c>this</c>.</summary>
    ExplicitThis = 1,
    /// <summary>The body explicitly references <c>super</c>.</summary>
    ExplicitSuper = 2,
    /// <summary>The body refers to an instance member without an explicit receiver.</summary>
    ImplicitMember = 4,
}

/// <summary>An enclosing nominal receiver required by an anonymous-function environment.</summary>
/// <param name="Function">The anonymous function whose environment needs the receiver.</param>
/// <param name="Owner">The nearest nominal declaration that owns the receiver identity.</param>
/// <param name="Access">Explicit or implicit receiver operations found in the expression.</param>
/// <param name="References">Receiver and implicit-member references in source order.</param>
/// <param name="Members">Resolved direct or inherited instance members for implicit references.</param>
/// <param name="IsComplete">False if bounded inherited-member lookup could not finish.</param>
public sealed record AnonymousFunctionReceiverCapture(
    AstAnonymousFunction Function,
    AstTypeDeclaration Owner,
    AnonymousFunctionReceiverAccess Access,
    ImmutableArray<AstAtom> References,
    ImmutableArray<DeclarationSymbol> Members,
    bool IsComplete);

internal static class AnonymousFunctionReceiverCaptureAnalysis
{
    private sealed class Builder(AstAnonymousFunction function, AstTypeDeclaration owner)
    {
        internal AstAnonymousFunction Function { get; } = function;
        internal AstTypeDeclaration Owner { get; } = owner;
        internal List<AstAtom> References { get; } = [];
        internal HashSet<DeclarationSymbol> Members { get; } = new(ReferenceEqualityComparer.Instance);
        internal AnonymousFunctionReceiverAccess Access { get; set; }
        internal bool IsComplete { get; set; } = true;
    }

    internal static ImmutableArray<AnonymousFunctionReceiverCapture> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        TypeAnalysis types,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(types);
        var scopesById = scopes.ToDictionary(scope => scope.Id);
        var captures = new Dictionary<(AstAnonymousFunction Function, AstTypeDeclaration Owner), Builder>();
        var specialization = new TypeSpecialization(types);
        var memberResolver = new MemberLookupResolver(types, specialization, types.Declarations.Files,
            cancellationToken);

        foreach (var atom in files.Keys.OfType<AstAtom>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (atom.SyntaxKind != NodeKind.NameExpression) { continue; }
            if (atom.Spelling is "this" or "super")
            {
                Add(atom, atom.Spelling == "this" ? AnonymousFunctionReceiverAccess.ExplicitThis
                    : AnonymousFunctionReceiverAccess.ExplicitSuper, [], files[atom], scopesById, scopeOf, captures);
            }
        }

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.Reference.Spelling == "field"
                && Ancestors(binding.Reference, files[binding.Reference]).OfType<AstAccessor>().Any())
            {
                Add(binding.Reference, AnonymousFunctionReceiverAccess.ImplicitMember, [], files[binding.Reference],
                    scopesById, scopeOf, captures);
                continue;
            }

            if (binding.Lookup.Declarations.Scope != LookupScope.Member)
            {
                if (binding.Lookup.Locals.Any(local => local.Kind == LocalSymbolKind.ConstructorProperty))
                {
                    Add(binding.Reference, AnonymousFunctionReceiverAccess.ImplicitMember, [],
                        files[binding.Reference], scopesById, scopeOf, captures);
                }
                else if (binding.Lookup.Locals.IsEmpty && binding.Lookup.Declarations.Candidates.IsEmpty
                    && InheritedMember(binding.Reference, files[binding.Reference], types, specialization,
                        memberResolver, cancellationToken) is { } inherited)
                {
                    Add(binding.Reference, AnonymousFunctionReceiverAccess.ImplicitMember, inherited.Members,
                        files[binding.Reference], scopesById, scopeOf, captures, inherited.IsComplete);
                }
                continue;
            }

            var members = binding.Lookup.Declarations.Candidates.Where(candidate => candidate.Kind is
                SymbolKind.Function or SymbolKind.Property or SymbolKind.Indexer).ToImmutableArray();
            if (members.IsEmpty) { continue; }
            Add(binding.Reference, AnonymousFunctionReceiverAccess.ImplicitMember, members,
                files[binding.Reference], scopesById, scopeOf, captures);
        }

        return captures.Values
            .OrderBy(capture => capture.Function.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(capture => capture.Function.Origin.Span.Start)
            .ThenBy(capture => capture.Owner.Origin.Span.Start)
            .Select(capture => new AnonymousFunctionReceiverCapture(capture.Function, capture.Owner,
                capture.Access,
                capture.References.OrderBy(reference => reference.Origin.Span.Start).ToImmutableArray(),
                capture.Members.OrderBy(member => member.QualifiedName, StringComparer.Ordinal)
                    .ThenBy(member => member.Signature, StringComparer.Ordinal).ToImmutableArray(),
                capture.IsComplete))
            .ToImmutableArray();
    }

    private sealed record InheritedMembers(ImmutableArray<DeclarationSymbol> Members, bool IsComplete);

    private static InheritedMembers? InheritedMember(AstAtom reference, AstSnapshot file, TypeAnalysis types,
        TypeSpecialization specialization, MemberLookupResolver resolver, CancellationToken cancellationToken)
    {
        var owner = Ancestors(reference, file).OfType<AstTypeDeclaration>().FirstOrDefault();
        var symbol = owner is null ? null : types.Declarations.GetDeclaredSymbol(owner);
        if (owner is null || symbol is null) { return null; }

        MemberLookupResult result;
        try
        {
            var receiver = specialization.Nominal(symbol, types.GetTypeParameters(owner));
            result = resolver.Resolve(receiver, reference.Spelling, reference);
        }
        catch (TypeSpecializationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new([], false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status == MemberLookupStatus.LimitExceeded) { return new([], false); }
        var members = result.Candidates.Select(candidate => candidate.Symbol)
            .Where(candidate => candidate.Kind is SymbolKind.Function or SymbolKind.Property or SymbolKind.Indexer)
            .GroupBy(candidate => candidate.Id).Select(group => group.First()).ToImmutableArray();
        return members.IsEmpty ? null : new(members, true);
    }

    private static void Add(AstAtom reference, AnonymousFunctionReceiverAccess access,
        ImmutableArray<DeclarationSymbol> members, AstSnapshot file,
        IReadOnlyDictionary<int, LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<(AstAnonymousFunction Function, AstTypeDeclaration Owner), Builder> captures,
        bool isComplete = true)
    {
        var owner = Ancestors(reference, file).OfType<AstTypeDeclaration>().FirstOrDefault();
        if (owner is null || !scopeOf.TryGetValue(owner, out int ownerScope)) { return; }
        int? current = scopeOf.GetValueOrDefault(reference);
        while (current is { } id && id != ownerScope)
        {
            var scope = scopes[id];
            if (scope.Owner is AstAnonymousFunction function)
            {
                var key = (function, owner);
                if (!captures.TryGetValue(key, out var capture))
                {
                    capture = new(function, owner);
                    captures.Add(key, capture);
                }
                capture.References.Add(reference);
                capture.Access |= access;
                capture.IsComplete &= isComplete;
                foreach (var member in members) { capture.Members.Add(member); }
            }
            current = scope.ParentId;
        }
    }

    private static IEnumerable<AstNode> Ancestors(AstNode node, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        { yield return parent; }
    }
}
