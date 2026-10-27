// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal static class LambdaEscapeAliasAnalysis
{
    private sealed record AliasEdge(LocalSymbol Source, LocalSymbol Target, AstNode Site);

    private sealed class FlowFacts
    {
        internal Dictionary<LocalSymbol, HashSet<AstLambda>> Values { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AliasEdge>> Edges { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AliasEdge>> Incoming { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AstNode>> StorageSites { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AstNode>> Mutations { get; } = new(LocalComparer);
        internal HashSet<AstAtom> AliasReferences { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<LocalSymbol, List<NameBinding>> Uses { get; } = new(LocalComparer);
    }

    private static readonly IEqualityComparer<LocalSymbol> LocalComparer = ReferenceEqualityComparer.Instance;

    internal static Dictionary<AstLambda, LambdaEscape> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        Dictionary<AstLambda, ImmutableArray<LambdaCapture>> captures,
        CancellationToken cancellationToken)
    {
        var facts = BuildFacts(scopes, scopeOf, files, bindings, cancellationToken);
        var localSets = Propagate(facts, cancellationToken);
        var result = new Dictionary<AstLambda, LambdaEscape>(ReferenceEqualityComparer.Instance);

        foreach (var (lambda, locals) in localSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uses = new HashSet<LambdaEscapeUse>();
            foreach (var local in locals)
            {
                foreach (var site in facts.StorageSites.GetValueOrDefault(local, []))
                { uses.Add(new(site, LambdaEscapeUseKind.LocalStorage)); }
                foreach (var site in facts.Mutations.GetValueOrDefault(local, []))
                { uses.Add(new(site, LambdaEscapeUseKind.AliasMutation)); }
                if (local.IsMutable && !facts.Mutations.ContainsKey(local)
                    && facts.StorageSites.TryGetValue(local, out var storage) && storage.Count != 0)
                { uses.Add(new(storage[0], LambdaEscapeUseKind.AliasMutation)); }

                foreach (var edge in facts.Incoming.GetValueOrDefault(local, []))
                { uses.Add(new(edge.Site, LambdaEscapeUseKind.LocalAlias)); }
                foreach (var binding in facts.Uses.GetValueOrDefault(local, []))
                {
                    if (facts.AliasReferences.Contains(binding.Reference)) { continue; }
                    var (kind, site) = ClassifyUse(binding, files[binding.Reference]);
                    uses.Add(new(site, kind));
                }
            }

            var ordered = uses.OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
                .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind).ToImmutableArray();
            var disposition = DispositionOf(ordered);
            result.Add(lambda, new(lambda, disposition, ordered, captures.GetValueOrDefault(lambda, [])));
        }

        return result;
    }

    private static FlowFacts BuildFacts(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        CancellationToken cancellationToken)
    {
        var facts = new FlowFacts();
        IEqualityComparer<AstAtom> atomComparer = ReferenceEqualityComparer.Instance;
        var bindingByReference = bindings.ToDictionary(binding => binding.Reference, atomComparer);

        foreach (var property in files.Keys.OfType<AstProperty>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (property.Initializer is not { } initializer || property.BindingNames.Count() != 1
                || !scopeOf.TryGetValue(property, out int scopeId)) { continue; }
            var local = DeclaredLocal(property, scopes[scopeId]);
            if (local is null) { continue; }
            facts.StorageSites.GetOrAdd(local).Add(property);
            AddValueSource(initializer, local, property, files[property], bindingByReference, facts);
        }

        foreach (var assignment in files.Keys.OfType<AstAssignment>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assignment.Operation != "=" || assignment.Location is not { } location
                || assignment.Value is not { } value || !TryLocalValue(location, files[assignment], bindingByReference, out var target))
            { continue; }
            facts.Mutations.GetOrAdd(target).Add(assignment);
            facts.StorageSites.GetOrAdd(target).Add(assignment);
            AddValueSource(value, target, assignment, files[assignment], bindingByReference, facts);
        }

        foreach (var binding in bindingByReference.Values)
        {
            foreach (var local in binding.Lookup.Locals.Where(local => local.Kind == LocalSymbolKind.Variable))
            { facts.Uses.GetOrAdd(local).Add(binding); }
        }

        return facts;
    }

    private static LocalSymbol? DeclaredLocal(AstProperty property, LexicalScope scope)
    {
        var candidates = scope.Symbols.Where(local => local.Kind == LocalSymbolKind.Variable
            && local.Origin.Span.Start >= property.Origin.Span.Start
            && local.Origin.Span.End <= property.Origin.Span.End
            && property.BindingNames.Contains(local.Name, StringComparer.Ordinal)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static void AddValueSource(AstExpression value, LocalSymbol target, AstNode site, AstSnapshot file,
        Dictionary<AstAtom, NameBinding> bindings, FlowFacts facts)
    {
        if (TryLambdaValue(value, file, out var lambda))
        {
            facts.Values.GetOrAdd(target).Add(lambda);
            return;
        }
        if (TryLocalValue(value, file, bindings, out var source))
        {
            var edge = new AliasEdge(source, target, site);
            facts.Edges.GetOrAdd(source).Add(edge);
            facts.Incoming.GetOrAdd(target).Add(edge);
            var atom = UnwrapValue(value, file) as AstAtom;
            if (atom is not null) { facts.AliasReferences.Add(atom); }
        }
    }

    private static bool TryLambdaValue(AstExpression expression, AstSnapshot file, out AstLambda lambda)
    {
        var value = UnwrapValue(expression, file);
        if (value is AstLambda direct) { lambda = direct; return true; }
        lambda = null!;
        return false;
    }

    private static bool TryLocalValue(AstExpression expression, AstSnapshot file,
        Dictionary<AstAtom, NameBinding> bindings, out LocalSymbol local)
    {
        if (UnwrapValue(expression, file) is AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression } atom
            && bindings.TryGetValue(atom, out var binding))
        {
            var locals = binding.Lookup.Locals.Where(candidate => candidate.Kind == LocalSymbolKind.Variable).ToArray();
            if (locals.Length == 1) { local = locals[0]; return true; }
        }
        local = null!;
        return false;
    }

    private static AstExpression UnwrapValue(AstExpression expression, AstSnapshot file)
    {
        AstExpression current = expression;
        while (true)
        {
            switch (current)
            {
                case AstSequence { IsGrouping: true } grouping:
                    var elements = grouping.Elements.Take(2).ToArray();
                    if (elements.Length == 1) { current = elements[0]; continue; }
                    return current;
                case AstLabelledExpression { Lambda: { } lambda }:
                    current = lambda;
                    continue;
                default:
                    return current;
            }
        }
    }

    private static Dictionary<AstLambda, HashSet<LocalSymbol>> Propagate(FlowFacts facts,
        CancellationToken cancellationToken)
    {
        var localsByLambda = new Dictionary<AstLambda, HashSet<LocalSymbol>>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<(AstLambda Lambda, LocalSymbol Local)>();
        foreach (var (local, lambdas) in facts.Values)
        {
            foreach (var lambda in lambdas)
            {
                AddLocal(lambda, local);
                pending.Enqueue((lambda, local));
            }
        }

        while (pending.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var edge in facts.Edges.GetValueOrDefault(item.Local, []))
            {
                if (!AddLocal(item.Lambda, edge.Target)) { continue; }
                pending.Enqueue((item.Lambda, edge.Target));
            }
        }
        return localsByLambda;

        bool AddLocal(AstLambda lambda, LocalSymbol local)
        {
            if (!localsByLambda.TryGetValue(lambda, out var locals))
            { locals = new(LocalComparer); localsByLambda.Add(lambda, locals); }
            return locals.Add(local);
        }
    }

    private static (LambdaEscapeUseKind Kind, AstNode Site) ClassifyUse(NameBinding binding, AstSnapshot file)
    {
        AstNode value = binding.Reference;
        AstNode? parent = file.GetParent(value);
        while (parent is AstStructure || parent is not null
            && IsTransparentWrapper(parent, value, file))
        {
            value = parent;
            parent = file.GetParent(parent);
        }

        if (binding.CapturesOuterLocal)
        { return (LambdaEscapeUseKind.CallableCapture, parent ?? value); }
        if (parent is AstCall call)
        {
            if (call.Target is { } target && LambdaEscapeAnalysis.IsTransparentValue(target, binding.Reference, file))
            { return (LambdaEscapeUseKind.LocalInvocation, call); }
            if (call.Arguments.Any(argument => LambdaEscapeAnalysis.IsTransparentValue(argument.Value, binding.Reference, file)))
            { return (LambdaEscapeUseKind.CallArgument, call); }
        }
        if (parent is AstReturn { Value: { } returned } statement
            && LambdaEscapeAnalysis.IsTransparentValue(returned, binding.Reference, file))
        { return (LambdaEscapeUseKind.ReturnValue, statement); }
        if (LambdaEscapeAnalysis.IsCallableTail(value, parent, file, out var callable))
        { return (LambdaEscapeUseKind.ReturnValue, callable); }
        if (parent is AstAssignment { Location: { } location, Value: { } assigned } assignment)
        {
            if (LambdaEscapeAnalysis.IsTransparentValue(location, binding.Reference, file))
            { return (LambdaEscapeUseKind.AliasMutation, assignment); }
            if (LambdaEscapeAnalysis.IsTransparentValue(assigned, binding.Reference, file))
            {
                return LambdaEscapeAnalysis.IsLocalLocation(location)
                    ? (LambdaEscapeUseKind.LocalAlias, assignment)
                    : (LambdaEscapeUseKind.HeapStorage, assignment);
            }
        }
        if (parent is AstSequence { IsGrouping: false } aggregate)
        { return (LambdaEscapeUseKind.AggregateElement, aggregate); }
        if (parent is AstProperty property)
        {
            return LambdaEscapeAnalysis.IsInsideCallable(property, file)
                ? (LambdaEscapeUseKind.LocalAlias, property)
                : (LambdaEscapeUseKind.HeapStorage, property);
        }
        if (parent is AstLambda or AstFunction)
        { return (LambdaEscapeUseKind.CallableCapture, parent); }
        return (LambdaEscapeUseKind.UnsupportedContext, parent ?? value);
    }

    private static bool IsTransparentWrapper(AstNode parent, AstNode value, AstSnapshot file)
    {
        if (parent is AstSequence { IsGrouping: true } grouping)
        {
            var elements = grouping.Elements.Take(2).ToArray();
            return elements.Length == 1 && ReferenceEquals(elements[0], value);
        }
        if (parent is AstLabelledExpression labelled)
        { return ReferenceEquals(labelled.Lambda, value); }
        if (parent is AstStructure)
        { return file.GetParent(parent) is AstStructure or AstCall; }
        return false;
    }

    private static LambdaEscapeDisposition DispositionOf(ImmutableArray<LambdaEscapeUse> uses)
    {
        if (uses.Any(use => use.Kind is LambdaEscapeUseKind.ReturnValue or LambdaEscapeUseKind.CallArgument
            or LambdaEscapeUseKind.HeapStorage or LambdaEscapeUseKind.AggregateElement))
        { return LambdaEscapeDisposition.MayEscape; }
        if (uses.Any(use => use.Kind is LambdaEscapeUseKind.AliasMutation or LambdaEscapeUseKind.CallableCapture
            or LambdaEscapeUseKind.UnsupportedContext))
        { return LambdaEscapeDisposition.Unknown; }
        return LambdaEscapeDisposition.ProvenNonEscaping;
    }
}

internal static class LambdaEscapeCollectionExtensions
{
    internal static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out var value))
        { value = new(); dictionary.Add(key, value); }
        return value;
    }
}
