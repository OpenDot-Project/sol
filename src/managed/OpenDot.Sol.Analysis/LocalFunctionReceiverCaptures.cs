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

[Flags]
public enum LocalFunctionReceiverAccess
{
    ExplicitThis = 1,
    ExplicitSuper = 2,
    ImplicitMember = 4,
}

/// <summary>A receiver identity required by a local named-function environment.</summary>
/// <param name="Function">The local function that needs the receiver.</param>
/// <param name="Owner">The nominal declaration that owns the receiver identity.</param>
/// <param name="Access">Explicit receiver or implicit member accesses.</param>
/// <param name="References">Source references in ascending byte-offset order.</param>
/// <param name="Members">Resolved member candidates for implicit member references.</param>
/// <param name="IsComplete">False when bounded member lookup could not complete.</param>
public sealed record LocalFunctionReceiverCapture(
    AstFunction Function,
    AstTypeDeclaration Owner,
    LocalFunctionReceiverAccess Access,
    ImmutableArray<AstAtom> References,
    ImmutableArray<DeclarationSymbol> Members,
    bool IsComplete);

internal static class LocalFunctionReceiverCaptureAnalysis
{
    private sealed class Builder(AstFunction function, AstTypeDeclaration owner)
    {
        internal AstFunction Function { get; } = function;
        internal AstTypeDeclaration Owner { get; } = owner;
        internal List<AstAtom> References { get; } = [];
        internal HashSet<DeclarationSymbol> Members { get; } = new(ReferenceEqualityComparer.Instance);
        internal LocalFunctionReceiverAccess Access { get; set; }
        internal bool IsComplete { get; set; } = true;
    }

    private sealed record InheritedMembers(ImmutableArray<DeclarationSymbol> Members, bool IsComplete);

    internal static ImmutableArray<LocalFunctionReceiverCapture> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        TypeAnalysis types,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(types);
        var functionsByOrigin = new Dictionary<object, AstFunction>(ReferenceEqualityComparer.Instance);
        foreach (var function in files.Keys.OfType<AstFunction>())
        { functionsByOrigin.TryAdd(function.Origin.Element, function); }
        IEqualityComparer<AstFunction> functionComparer = ReferenceEqualityComparer.Instance;
        var localFunctions = new HashSet<AstFunction>(scopes.SelectMany(scope => scope.Symbols)
            .Where(local => local.Kind == LocalSymbolKind.Function)
            .Select(local => functionsByOrigin.GetValueOrDefault(local.Origin.Element))
            .Where(function => function is not null).Cast<AstFunction>(), functionComparer);
        var scopesById = scopes.ToDictionary(scope => scope.Id);
        var captures = new Dictionary<(AstFunction Function, AstTypeDeclaration Owner), Builder>();
        var specialization = new TypeSpecialization(types);
        var memberResolver = new MemberLookupResolver(types, specialization, types.Declarations.Files,
            cancellationToken);

        foreach (var atom in files.Keys.OfType<AstAtom>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (atom.SyntaxKind != NodeKind.NameExpression || atom.Spelling is not ("this" or "super")) { continue; }
            Add(atom, atom.Spelling == "this" ? LocalFunctionReceiverAccess.ExplicitThis
                : LocalFunctionReceiverAccess.ExplicitSuper, [], files[atom], scopesById, scopeOf,
                localFunctions, captures);
        }

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.Reference.Spelling == "field"
                && Ancestors(binding.Reference, files[binding.Reference]).OfType<AstAccessor>().Any())
            {
                Add(binding.Reference, LocalFunctionReceiverAccess.ImplicitMember, [], files[binding.Reference],
                    scopesById, scopeOf, localFunctions, captures);
                continue;
            }

            if (binding.Lookup.Declarations.Scope != LookupScope.Member)
            {
                if (binding.Lookup.Locals.Any(local => local.Kind == LocalSymbolKind.ConstructorProperty))
                {
                    Add(binding.Reference, LocalFunctionReceiverAccess.ImplicitMember, [], files[binding.Reference],
                        scopesById, scopeOf, localFunctions, captures);
                }
                else if (binding.Lookup.Locals.IsEmpty && binding.Lookup.Declarations.Candidates.IsEmpty
                    && InheritedMember(binding.Reference, files[binding.Reference], types, specialization,
                        memberResolver, cancellationToken) is { } inherited)
                {
                    Add(binding.Reference, LocalFunctionReceiverAccess.ImplicitMember, inherited.Members,
                        files[binding.Reference], scopesById, scopeOf, localFunctions, captures,
                        inherited.IsComplete);
                }
                continue;
            }

            var members = binding.Lookup.Declarations.Candidates.Where(candidate => candidate.Kind is
                SymbolKind.Function or SymbolKind.Property or SymbolKind.Indexer).ToImmutableArray();
            if (!members.IsEmpty)
            {
                Add(binding.Reference, LocalFunctionReceiverAccess.ImplicitMember, members,
                    files[binding.Reference], scopesById, scopeOf, localFunctions, captures);
            }
        }

        return captures.Values
            .OrderBy(capture => capture.Function.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(capture => capture.Function.Origin.Span.Start)
            .ThenBy(capture => capture.Owner.Origin.Span.Start)
            .Select(capture => new LocalFunctionReceiverCapture(capture.Function, capture.Owner,
                capture.Access, capture.References.OrderBy(reference => reference.Origin.Span.Start)
                    .ToImmutableArray(), capture.Members.OrderBy(member => member.QualifiedName, StringComparer.Ordinal)
                    .ThenBy(member => member.Signature, StringComparer.Ordinal).ToImmutableArray(), capture.IsComplete))
            .ToImmutableArray();
    }

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

    private static void Add(AstAtom reference, LocalFunctionReceiverAccess access,
        ImmutableArray<DeclarationSymbol> members, AstSnapshot file,
        IReadOnlyDictionary<int, LexicalScope> scopes, Dictionary<AstNode, int> scopeOf,
        IReadOnlySet<AstFunction> localFunctions,
        Dictionary<(AstFunction Function, AstTypeDeclaration Owner), Builder> captures,
        bool isComplete = true)
    {
        var owner = Ancestors(reference, file).OfType<AstTypeDeclaration>().FirstOrDefault();
        if (owner is null || !scopeOf.TryGetValue(owner, out int ownerScope)) { return; }
        for (int? current = scopeOf.GetValueOrDefault(reference); current is { } id && id != ownerScope;
            current = scopes[id].ParentId)
        {
            var function = scopes[id].Owner as AstFunction;
            if (function is null || !localFunctions.Contains(function)) { continue; }
            var key = (function, owner);
            if (!captures.TryGetValue(key, out var capture))
            { capture = new(function, owner); captures.Add(key, capture); }
            capture.References.Add(reference);
            capture.Access |= access;
            capture.IsComplete &= isComplete;
            foreach (var member in members) { capture.Members.Add(member); }
        }
    }

    private static IEnumerable<AstNode> Ancestors(AstNode node, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        { yield return parent; }
    }
}
