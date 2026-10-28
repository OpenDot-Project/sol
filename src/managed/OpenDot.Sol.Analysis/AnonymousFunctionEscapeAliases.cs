// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal static class AnonymousFunctionEscapeAliasAnalysis
{
    private sealed record AliasEdge(LocalSymbol Source, LocalSymbol Target, AstNode Site);

    private sealed class FlowFacts
    {
        internal Dictionary<LocalSymbol, HashSet<AstNode>> Values { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AliasEdge>> Edges { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AliasEdge>> Incoming { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AstNode>> StorageSites { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AstNode>> Mutations { get; } = new(LocalComparer);
        internal HashSet<AstAssignment> TrackedWrites { get; } = new(ReferenceEqualityComparer.Instance);
        internal HashSet<AstAtom> AliasReferences { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<LocalSymbol, List<NameBinding>> Uses { get; } = new(LocalComparer);
    }

    private static readonly IEqualityComparer<LocalSymbol> LocalComparer = ReferenceEqualityComparer.Instance;

    internal static Dictionary<AstAnonymousFunction, AnonymousFunctionEscape> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        Dictionary<AstAnonymousFunction, ImmutableArray<AnonymousFunctionCapture>> captures,
        Dictionary<AstAnonymousFunction, ImmutableArray<AnonymousFunctionReceiverCapture>> receiverCaptures,
        AnalysisSession declarations,
        TypeAnalysis types,
        CancellationToken cancellationToken)
    {
        IEqualityComparer<AstAtom> atomComparer = ReferenceEqualityComparer.Instance;
        var bindingByReference = bindings.ToDictionary(binding => binding.Reference, atomComparer);
        var facts = BuildFacts(scopes, scopeOf, files, bindingByReference.Values, cancellationToken);
        var localsByCallable = Propagate(facts, scopes, scopeOf, files, bindingByReference, cancellationToken);
        MarkUnknownAliases(facts, localsByCallable, cancellationToken);
        var result = new Dictionary<AstAnonymousFunction, AnonymousFunctionEscape>(ReferenceEqualityComparer.Instance);

        foreach (var (callable, locals) in localsByCallable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (callable is not AstAnonymousFunction function) { continue; }
            var uses = new HashSet<AnonymousFunctionEscapeUse>();
            foreach (var local in locals)
            {
                foreach (var site in facts.StorageSites.GetValueOrDefault(local, []))
                { uses.Add(new(site, AnonymousFunctionEscapeUseKind.LocalStorage)); }
                foreach (var site in facts.Mutations.GetValueOrDefault(local, []))
                {
                    if (IsDefiniteEscapingParameterOverwrite(function, local, site, facts, scopes, scopeOf,
                        files, bindingByReference)) { continue; }
                    uses.Add(new(site, AnonymousFunctionEscapeUseKind.AliasMutation));
                }

                foreach (var edge in facts.Incoming.GetValueOrDefault(local, []))
                { uses.Add(new(edge.Site, AnonymousFunctionEscapeUseKind.LocalAlias)); }
                foreach (var binding in facts.Uses.GetValueOrDefault(local, []))
                {
                    if (facts.AliasReferences.Contains(binding.Reference)) { continue; }
                    if (IsOverwrittenBeforeNextUse(function, local, binding.Reference, facts, scopes,
                        scopeOf, files, bindingByReference)) { continue; }
                    var (kind, site) = ClassifyUse(binding, files[binding.Reference], facts,
                        bindingByReference, declarations, types);
                    uses.Add(new(site, kind));
                }
            }

            var ordered = uses.OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
                .ThenBy(use => use.Site.Origin.Span.Start).ThenBy(use => use.Kind).ToImmutableArray();
            result.Add(function, new(function, DispositionOf(ordered), ordered,
                captures.GetValueOrDefault(function, []), receiverCaptures.GetValueOrDefault(function, [])));
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
            if (!AnonymousFunctionEscapeAnalysis.IsInsideCallable(property, files[property])
                || property.Initializer is not { } initializer || property.BindingNames.Count() != 1
                || !scopeOf.TryGetValue(property, out int scopeId)) { continue; }
            var local = DeclaredLocal(property, scopes[scopeId]);
            if (local is null) { continue; }
            facts.StorageSites.GetOrAdd(local).Add(property);
            if (!AddValueSource(initializer, local, property, files[property], bindingByReference, facts))
            { facts.Mutations.GetOrAdd(local).Add(property); }
        }

        foreach (var assignment in files.Keys.OfType<AstAssignment>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assignment.Operation != "=" || assignment.Location is not { } location
                || assignment.Value is not { } value
                || !TryLocalValue(location, files[assignment], bindingByReference, out var target))
            { continue; }
            facts.StorageSites.GetOrAdd(target).Add(assignment);
            bool tracked = AddValueSource(value, target, assignment, files[assignment], bindingByReference, facts);
            if (tracked && target.IsMutable)
            { facts.TrackedWrites.Add(assignment); }
            else
            { facts.Mutations.GetOrAdd(target).Add(assignment); }
        }

        foreach (var binding in bindingByReference.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static bool AddValueSource(AstExpression value, LocalSymbol target, AstNode site, AstSnapshot file,
        Dictionary<AstAtom, NameBinding> bindings, FlowFacts facts)
    {
        if (TryCallableValue(value, file, out var callable))
        {
            facts.Values.GetOrAdd(target).Add(callable);
            return true;
        }
        if (TryLocalValue(value, file, bindings, out var source))
        {
            var edge = new AliasEdge(source, target, site);
            facts.Edges.GetOrAdd(source).Add(edge);
            facts.Incoming.GetOrAdd(target).Add(edge);
            if (UnwrapValue(value, file) is AstAtom atom) { facts.AliasReferences.Add(atom); }
            return true;
        }
        return false;
    }

    private static bool TryCallableValue(AstExpression expression, AstSnapshot file, out AstNode callable)
    {
        var value = UnwrapValue(expression, file);
        if (value is AstLambda or AstAnonymousFunction) { callable = value; return true; }
        callable = null!;
        return false;
    }

    private static bool TryLocalValue(AstExpression expression, AstSnapshot file,
        Dictionary<AstAtom, NameBinding> bindings, out LocalSymbol local)
    {
        if (UnwrapValue(expression, file) is AstAtom
            { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression } atom
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
        while (current is AstSequence { IsGrouping: true } grouping)
        {
            var elements = grouping.Elements.Take(2).ToArray();
            if (elements.Length != 1) { break; }
            current = elements[0];
        }
        return current;
    }

    private static Dictionary<AstNode, HashSet<LocalSymbol>> Propagate(FlowFacts facts,
        ImmutableArray<LexicalScope> scopes, Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files, Dictionary<AstAtom, NameBinding> bindings,
        CancellationToken cancellationToken)
    {
        var localsByCallable = new Dictionary<AstNode, HashSet<LocalSymbol>>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<(AstNode Callable, LocalSymbol Local)>();
        foreach (var (local, callables) in facts.Values)
        {
            foreach (var callable in callables)
            {
                AddLocal(callable, local);
                pending.Enqueue((callable, local));
            }
        }

        while (pending.TryDequeue(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var edge in facts.Edges.GetValueOrDefault(item.Local, []))
            {
                if (IsOverwrittenBeforeNextUse(item.Callable, item.Local, edge.Site, facts, scopes,
                    scopeOf, files, bindings)) { continue; }
                if (!AddLocal(item.Callable, edge.Target)) { continue; }
                pending.Enqueue((item.Callable, edge.Target));
            }
        }
        return localsByCallable;

        bool AddLocal(AstNode callable, LocalSymbol local)
        {
            if (!localsByCallable.TryGetValue(callable, out var locals))
            { locals = new(LocalComparer); localsByCallable.Add(callable, locals); }
            return locals.Add(local);
        }
    }

    private static bool IsOverwrittenBeforeNextUse(AstNode callable, LocalSymbol local, AstNode site,
        FlowFacts facts, ImmutableArray<LexicalScope> scopes, Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files, Dictionary<AstAtom, NameBinding> bindings)
    {
        var use = DirectBlockEntry(site, files[site]);
        if (use is null) { return false; }
        foreach (var mutation in facts.Mutations.GetValueOrDefault(local, []))
        {
            if (!IsDefiniteEscapingParameterOverwrite(callable, local, mutation, facts, scopes, scopeOf,
                files, bindings) || mutation is not AstAssignment assignment) { continue; }
            var overwrite = DirectBlockEntry(assignment, files[assignment]);
            if (overwrite is not { } entry || !ReferenceEquals(entry.Block, use.Value.Block)) { continue; }
            if (use.Value.Index == entry.Index && site is AstAtom atom
                && assignment.Location is { } location
                && AnonymousFunctionEscapeAnalysis.IsTransparentValue(location, atom, files[assignment]))
            { return true; }
            if (use.Value.Index <= entry.Index || HasInterveningAliasReference(local, assignment,
                site, facts)) { continue; }
            return true;
        }
        return false;
    }

    private static bool HasInterveningAliasReference(LocalSymbol local, AstAssignment overwrite,
        AstNode use, FlowFacts facts)
    {
        int start = overwrite.Origin.Span.End;
        int end = use.Origin.Span.Start;
        return facts.Uses.GetValueOrDefault(local, []).Any(binding =>
            binding.Reference.Origin.Span.Start > start && binding.Reference.Origin.Span.Start < end);
    }

    private static bool IsDefiniteEscapingParameterOverwrite(AstNode callable, LocalSymbol local, AstNode site,
        FlowFacts facts, ImmutableArray<LexicalScope> scopes, Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files, Dictionary<AstAtom, NameBinding> bindings)
    {
        if (!local.IsMutable || local.Kind != LocalSymbolKind.Variable || site is not AstAssignment assignment
            || assignment.Location is not { } location
            || !TryLocalValue(location, files[assignment], bindings, out var target)
            || !ReferenceEquals(target, local)
            || assignment.Value is not { } value
            || !IsEscapingFunctionParameter(value, files[assignment], scopes, bindings)) { return false; }

        var overwrite = DirectBlockEntry(assignment, files[assignment]);
        if (overwrite is null) { return false; }
        bool foundPriorStore = false;
        foreach (var store in facts.StorageSites.GetValueOrDefault(local, []))
        {
            if (!StoresCallable(store, callable, local, scopes, scopeOf, files, bindings)) { continue; }
            var stored = DirectBlockEntry(store, files[store]);
            if (stored is null || !ReferenceEquals(stored.Value.Block, overwrite.Value.Block)
                || stored.Value.Index >= overwrite.Value.Index) { return false; }
            foundPriorStore = true;
        }
        return foundPriorStore;
    }

    private static bool StoresCallable(AstNode site, AstNode callable, LocalSymbol local,
        ImmutableArray<LexicalScope> scopes, Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files, Dictionary<AstAtom, NameBinding> bindings)
    {
        AstExpression? value = site switch
        {
            AstProperty property when property.Initializer is { } initializer
                && scopeOf.TryGetValue(property, out int scopeId)
                && ReferenceEquals(DeclaredLocal(property, scopes[scopeId]), local) => initializer,
            AstAssignment assignment when assignment.Value is { } assigned
                && assignment.Location is { } location
                && TryLocalValue(location, files[assignment], bindings, out var target)
                && ReferenceEquals(target, local) => assigned,
            _ => null,
        };
        return value is not null && TryCallableValue(value, files[site], out var stored)
            && ReferenceEquals(stored, callable);
    }

    private static bool IsEscapingFunctionParameter(AstExpression value, AstSnapshot file,
        ImmutableArray<LexicalScope> scopes, Dictionary<AstAtom, NameBinding> bindings)
    {
        if (UnwrapValue(value, file) is not AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression } atom
            || !bindings.TryGetValue(atom, out var binding)) { return false; }
        var parameters = binding.Lookup.Locals.Where(candidate => candidate.Kind == LocalSymbolKind.Parameter
            && candidate.PassingMode == AstPassingMode.Value && IsFunctionType(candidate.WrittenType)).ToArray();
        if (parameters.Length != 1) { return false; }
        var parameter = parameters[0];
        if ((uint)parameter.ScopeId >= (uint)scopes.Length || scopes[parameter.ScopeId].Owner is not AstCallable owner)
        { return false; }
        return owner.Parameters.Any(candidate => candidate.IsEscaping
            && candidate.Name == parameter.Name
            && ReferenceEquals(candidate.NameOrigin.Element, parameter.Origin.Element));
    }

    private static bool IsFunctionType(AstType? type) => type?.Shape.Kind == AstTypeKind.Function
        || type?.Shape is { Kind: AstTypeKind.Nullable, Arguments.Length: 1 } nullable
            && nullable.Arguments[0].Kind == AstTypeKind.Function;

    private static (AstBlock Block, AstNode Entry, int Index)? DirectBlockEntry(AstNode node, AstSnapshot file)
    {
        AstNode current = node;
        AstNode? parent = file.GetParent(current);
        while (parent is not null)
        {
            if (parent is AstBlock block)
            {
                int index = block.Statements.IndexOf(current);
                if (index >= 0) { return (block, current, index); }
            }
            current = parent;
            parent = file.GetParent(current);
        }
        return null;
    }

    private static void MarkUnknownAliases(FlowFacts facts,
        Dictionary<AstNode, HashSet<LocalSymbol>> localsByCallable, CancellationToken cancellationToken)
    {
        var callableLocals = localsByCallable.Values.SelectMany(locals => locals).ToHashSet(LocalComparer);
        var unknown = facts.Mutations.Keys.ToHashSet(LocalComparer);
        foreach (var (source, edges) in facts.Edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (callableLocals.Contains(source)) { continue; }
            unknown.Add(source);
            foreach (var edge in edges)
            {
                unknown.Add(edge.Target);
                facts.Mutations.GetOrAdd(edge.Target).Add(edge.Site);
            }
        }

        var pending = new Queue<LocalSymbol>(unknown);
        while (pending.TryDequeue(out var source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var edge in facts.Edges.GetValueOrDefault(source, []))
            {
                facts.Mutations.GetOrAdd(edge.Target).Add(edge.Site);
                if (unknown.Add(edge.Target)) { pending.Enqueue(edge.Target); }
            }
        }
    }

    private static (AnonymousFunctionEscapeUseKind Kind, AstNode Site) ClassifyUse(NameBinding binding,
        AstSnapshot file, FlowFacts facts, Dictionary<AstAtom, NameBinding> bindings,
        AnalysisSession declarations, TypeAnalysis types)
    {
        AstNode value = binding.Reference;
        AstNode? parent = file.GetParent(value);
        while (parent is not null
            && AnonymousFunctionEscapeAnalysis.IsTransparentWrapper(parent, value))
        {
            value = parent;
            parent = file.GetParent(parent);
        }

        if (binding.CapturesOuterLocal)
        { return (AnonymousFunctionEscapeUseKind.CallableCapture, parent ?? value); }
        if (parent is AstCall call)
        {
            if (call.Target is { } target
                && AnonymousFunctionEscapeAnalysis.IsTransparentValue(target, binding.Reference, file))
            { return (AnonymousFunctionEscapeUseKind.LocalInvocation, call); }
            for (int index = 0; index < call.Arguments.Length; index++)
            {
                if (!AnonymousFunctionEscapeAnalysis.IsTransparentValue(
                    call.Arguments[index].Value, binding.Reference, file)) { continue; }
                var contract = EscapeParameterContracts.Resolve(call, index, bindings, declarations, types);
                return (contract == CallArgumentEscapeContract.NonEscaping
                    ? AnonymousFunctionEscapeUseKind.NonescapingCallArgument
                    : AnonymousFunctionEscapeUseKind.CallArgument, call);
            }
        }
        if (parent is AstReturn { Value: { } returned } statement
            && AnonymousFunctionEscapeAnalysis.IsTransparentValue(returned, binding.Reference, file))
        { return (AnonymousFunctionEscapeUseKind.ReturnValue, statement); }
        if (AnonymousFunctionEscapeAnalysis.IsCallableTail(value, parent, file, out var callable))
        { return (AnonymousFunctionEscapeUseKind.ReturnValue, callable); }
        if (parent is AstAssignment { Location: { } location, Value: { } assigned } assignment)
        {
            if (AnonymousFunctionEscapeAnalysis.IsTransparentValue(location, binding.Reference, file))
            {
                return facts.TrackedWrites.Contains(assignment)
                    ? (AnonymousFunctionEscapeUseKind.LocalAlias, assignment)
                    : (AnonymousFunctionEscapeUseKind.AliasMutation, assignment);
            }
            if (AnonymousFunctionEscapeAnalysis.IsTransparentValue(assigned, binding.Reference, file))
            {
                if (!AnonymousFunctionEscapeAnalysis.IsLocalLocation(location))
                { return (AnonymousFunctionEscapeUseKind.HeapStorage, assignment); }
                return facts.TrackedWrites.Contains(assignment)
                    ? (AnonymousFunctionEscapeUseKind.LocalAlias, assignment)
                    : (AnonymousFunctionEscapeUseKind.AliasMutation, assignment);
            }
        }
        if (parent is AstSequence { IsGrouping: false } aggregate)
        { return (AnonymousFunctionEscapeUseKind.AggregateElement, aggregate); }
        if (parent is AstProperty property)
        {
            return AnonymousFunctionEscapeAnalysis.IsInsideCallable(property, file)
                ? (AnonymousFunctionEscapeUseKind.LocalAlias, property)
                : (AnonymousFunctionEscapeUseKind.HeapStorage, property);
        }
        if (parent is AstLambda or AstFunction or AstAnonymousFunction)
        { return (AnonymousFunctionEscapeUseKind.CallableCapture, parent); }
        return (AnonymousFunctionEscapeUseKind.UnsupportedContext, parent ?? value);
    }

    private static AnonymousFunctionEscapeDisposition DispositionOf(
        ImmutableArray<AnonymousFunctionEscapeUse> uses)
    {
        if (uses.Any(use => use.Kind is AnonymousFunctionEscapeUseKind.ReturnValue
            or AnonymousFunctionEscapeUseKind.CallArgument or AnonymousFunctionEscapeUseKind.HeapStorage
            or AnonymousFunctionEscapeUseKind.AggregateElement))
        { return AnonymousFunctionEscapeDisposition.MayEscape; }
        if (uses.Any(use => use.Kind is AnonymousFunctionEscapeUseKind.AliasMutation
            or AnonymousFunctionEscapeUseKind.CallableCapture or AnonymousFunctionEscapeUseKind.UnsupportedContext))
        { return AnonymousFunctionEscapeDisposition.Unknown; }
        return AnonymousFunctionEscapeDisposition.ProvenNonEscaping;
    }
}
