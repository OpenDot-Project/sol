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

internal sealed record LocalFunctionAliasUse(LocalSymbol FunctionLocal, AstNode Site,
    LocalFunctionEscapeUseKind Kind);

internal sealed record LocalFunctionEscapeAliasFacts(HashSet<AstAtom> SourceReferences,
    ImmutableArray<LocalFunctionAliasUse> Uses);

internal static class LocalFunctionEscapeAliasAnalysis
{
    private sealed record AliasEdge(LocalSymbol Source, LocalSymbol Target, AstNode Site);
    private sealed record StoredValue(LocalSymbol Target, AstNode Site, LocalFunctionEscapeUseKind Kind,
        AstFunction? Function, LocalSymbol? Source);
    private sealed record SourceValue(AstFunction? Function, LocalSymbol? Alias, AstAtom Reference);

    private sealed class FlowFacts
    {
        internal Dictionary<LocalSymbol, HashSet<AstFunction>> DirectValues { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AliasEdge>> Edges { get; } = new(LocalComparer);
        internal List<StoredValue> Stores { get; } = [];
        internal Dictionary<LocalSymbol, List<AstAssignment>> Writes { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AstAssignment>> OpaqueOverwrites { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<AstNode>> UnknownWrites { get; } = new(LocalComparer);
        internal Dictionary<LocalSymbol, List<NameBinding>> References { get; } = new(LocalComparer);
        internal HashSet<AstAtom> SourceReferences { get; } = new(AtomComparer);
        internal HashSet<AstAtom> WriteReferences { get; } = new(AtomComparer);
    }

    private static readonly IEqualityComparer<LocalSymbol> LocalComparer = ReferenceEqualityComparer.Instance;
    private static readonly IEqualityComparer<AstAtom> AtomComparer = ReferenceEqualityComparer.Instance;
    private static readonly IEqualityComparer<AstFunction> FunctionComparer = ReferenceEqualityComparer.Instance;

    internal static LocalFunctionEscapeAliasFacts Create(LexicalAnalysis analysis,
        Dictionary<LocalSymbol, AstFunction> functionsByLocal,
        Dictionary<AstAtom, NameBinding> bindingsByReference, CancellationToken cancellationToken)
    {
        var facts = new FlowFacts();
        var localByFunction = functionsByLocal.ToDictionary(pair => pair.Value, pair => pair.Key,
            FunctionComparer);
        var files = BuildFileMap(analysis, cancellationToken);
        foreach (var property in files.Keys.OfType<AstProperty>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (property.Initializer is not { } initializer || property.BindingNames.Count() != 1) { continue; }
            var target = DeclaredLocal(property, analysis);
            if (target is null || !TrySource(initializer, files[property], bindingsByReference,
                functionsByLocal, out var source)) { continue; }

            facts.SourceReferences.Add(source.Reference);
            facts.Stores.Add(new(target, property, source.Alias is null
                ? LocalFunctionEscapeUseKind.LocalStorage : LocalFunctionEscapeUseKind.LocalAlias,
                source.Function, source.Alias));
            AddSource(source, target, property, facts);
        }

        foreach (var assignment in files.Keys.OfType<AstAssignment>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assignment.Operation != "=" || assignment.Location is not { } location
                || assignment.Value is not { } value
                || !TryVariable(location, files[assignment], bindingsByReference, out var target,
                    out var locationReference)) { continue; }
            facts.WriteReferences.Add(locationReference);
            facts.Writes.GetOrAdd(target).Add(assignment);
            if (TrySource(value, files[assignment], bindingsByReference, functionsByLocal, out var source))
            {
                facts.SourceReferences.Add(source.Reference);
                facts.Stores.Add(new(target, assignment, LocalFunctionEscapeUseKind.LocalAlias,
                    source.Function, source.Alias));
                AddSource(source, target, assignment, facts);
                if (target.IsMutable && source.Function is null
                    && source.Alias is { Kind: LocalSymbolKind.Parameter, PassingMode: AstPassingMode.Value })
                { facts.OpaqueOverwrites.GetOrAdd(target).Add(assignment); }
            }
            else if (target.IsMutable && IsEscapingFunctionParameter(value, files[assignment], analysis,
                bindingsByReference))
            { facts.OpaqueOverwrites.GetOrAdd(target).Add(assignment); }
            else
            { facts.UnknownWrites.GetOrAdd(target).Add(assignment); }
        }

        foreach (var binding in bindingsByReference.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var local in binding.Lookup.Locals.Where(local => local.Kind == LocalSymbolKind.Variable))
            { facts.References.GetOrAdd(local).Add(binding); }
        }
        foreach (var references in facts.References.Values)
        { references.Sort((left, right) => left.Reference.Origin.Span.Start.CompareTo(right.Reference.Origin.Span.Start)); }

        var functionsByAlias = Propagate(facts, cancellationToken);
        var uses = ImmutableArray.CreateBuilder<LocalFunctionAliasUse>();
        foreach (var store in facts.Stores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<AstFunction> storedFunctions = store.Function is { } directFunction
                ? [directFunction]
                : store.Source is { } source
                    ? functionsByAlias.GetValueOrDefault(source, [])
                    : [];
            foreach (var function in storedFunctions)
            {
                if (!localByFunction.TryGetValue(function, out var functionLocal)) { continue; }
                uses.Add(new(functionLocal, store.Site, store.Kind));
            }
        }

        foreach (var (local, sites) in facts.UnknownWrites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var function in functionsByAlias.GetValueOrDefault(local, []))
            {
                if (!localByFunction.TryGetValue(function, out var functionLocal)) { continue; }
                foreach (var site in sites)
                { uses.Add(new(functionLocal, site, LocalFunctionEscapeUseKind.AliasMutation)); }
            }
        }

        foreach (var binding in bindingsByReference.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var atom = binding.Reference;
            if (facts.SourceReferences.Contains(atom) || facts.WriteReferences.Contains(atom)) { continue; }
            var aliases = binding.Lookup.Locals.Where(local => local.Kind == LocalSymbolKind.Variable)
                .Where(functionsByAlias.ContainsKey).ToArray();
            if (aliases.Length != 1) { continue; }
            var file = files[atom];
            var (kind, site) = ClassifyAliasUse(binding, file, bindingsByReference, analysis);
            var alias = aliases[0];
            var functions = functionsByAlias.GetValueOrDefault(alias, []);
            foreach (var function in functions)
            {
                if (IsKilledBySequentialOpaqueOverwrite(alias, atom, facts, file)) { continue; }
                if (localByFunction.TryGetValue(function, out var functionLocal))
                { uses.Add(new(functionLocal, site, kind)); }
            }
        }

        var ordered = uses.Distinct()
            .OrderBy(use => use.FunctionLocal.Id)
            .ThenBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(use => use.Site.Origin.Span.Start)
            .ThenBy(use => use.Kind)
            .ToImmutableArray();
        return new(facts.SourceReferences, ordered);
    }

    private static bool IsKilledBySequentialOpaqueOverwrite(LocalSymbol alias, AstAtom use,
        FlowFacts facts, AstSnapshot file)
    {
        if (!facts.OpaqueOverwrites.TryGetValue(alias, out var candidates)
            || !TryGetContainingBlock(use, file, out var useBlock, out _)) { return false; }

        foreach (var overwrite in candidates.OrderByDescending(site => site.Origin.Span.Start))
        {
            if (overwrite.Origin.Span.Start >= use.Origin.Span.Start
                || !TryGetContainingBlock(overwrite, file, out var overwriteBlock, out var overwriteStatement)
                || !ReferenceEquals(useBlock, overwriteBlock)
                || !ReferenceEquals(overwrite, overwriteStatement)) { continue; }

            int overwriteEnd = overwrite.Origin.Span.End;
            int useStart = use.Origin.Span.Start;
            bool laterWrite = facts.Writes.GetValueOrDefault(alias, [])
                .Any(write => write.Origin.Span.Start > overwriteEnd && write.Origin.Span.Start < useStart);
            if (laterWrite) { continue; }

            bool interveningRead = HasReferenceBetween(facts.References.GetValueOrDefault(alias, []),
                overwriteEnd, useStart);
            if (!interveningRead) { return true; }
        }

        return false;
    }

    private static bool HasReferenceBetween(List<NameBinding> references, int start, int end)
    {
        int low = 0;
        int high = references.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (references[middle].Reference.Origin.Span.Start <= start) { low = middle + 1; }
            else { high = middle; }
        }
        return low < references.Count && references[low].Reference.Origin.Span.Start < end;
    }

    private static bool TryGetContainingBlock(AstNode node, AstSnapshot file,
        out AstBlock block, out AstNode statement)
    {
        AstNode current = node;
        while (file.GetParent(current) is { } parent)
        {
            if (parent is AstBlock owner)
            { block = owner; statement = current; return true; }
            if (parent is AstCallable or AstLambda or AstAnonymousFunction)
            { break; }
            current = parent;
        }

        block = null!;
        statement = null!;
        return false;
    }

    private static Dictionary<AstNode, AstSnapshot> BuildFileMap(LexicalAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<AstNode, AstSnapshot>(ReferenceEqualityComparer.Instance);
        foreach (var file in analysis.Declarations.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var node in file.Nodes) { files.Add(node, file); }
        }
        return files;
    }

    private static LocalSymbol? DeclaredLocal(AstProperty property, LexicalAnalysis analysis)
    {
        if (!analysis.TryGetScopeId(property, out int scopeId)) { return null; }
        var candidates = analysis.Scopes[scopeId].Symbols.Where(local =>
            local.Kind == LocalSymbolKind.Variable
            && local.Origin.Span.Start >= property.Origin.Span.Start
            && local.Origin.Span.End <= property.Origin.Span.End
            && property.BindingNames.Contains(local.Name, StringComparer.Ordinal)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static void AddSource(SourceValue source, LocalSymbol target, AstNode site, FlowFacts facts)
    {
        if (source.Function is { } function)
        { facts.DirectValues.GetOrAdd(target).Add(function); }
        else if (source.Alias is { } alias)
        { facts.Edges.GetOrAdd(alias).Add(new(alias, target, site)); }
    }

    private static bool TrySource(AstExpression expression, AstSnapshot file,
        Dictionary<AstAtom, NameBinding> bindings, Dictionary<LocalSymbol, AstFunction> functionsByLocal,
        out SourceValue source)
    {
        if (Unwrap(expression, file) is AstAtom { SyntaxKind: NodeKind.NameExpression } atom
            && bindings.TryGetValue(atom, out var binding))
        {
            var functions = binding.Lookup.Locals.Where(local => local.Kind == LocalSymbolKind.Function
                && functionsByLocal.ContainsKey(local)).ToArray();
            if (functions.Length == 1)
            { source = new(functionsByLocal[functions[0]], null, atom); return true; }

            var variables = binding.Lookup.Locals.Where(local => local.Kind == LocalSymbolKind.Variable).ToArray();
            if (variables.Length == 1)
            { source = new(null, variables[0], atom); return true; }
        }
        source = null!;
        return false;
    }

    private static bool IsEscapingFunctionParameter(AstExpression value, AstSnapshot file,
        LexicalAnalysis analysis, Dictionary<AstAtom, NameBinding> bindings)
    {
        if (Unwrap(value, file) is not AstAtom { SyntaxKind: NodeKind.NameExpression } atom
            || !bindings.TryGetValue(atom, out var binding)) { return false; }
        var parameters = binding.Lookup.Locals.Where(candidate => candidate.Kind == LocalSymbolKind.Parameter
            && candidate.PassingMode == AstPassingMode.Value && IsFunctionType(candidate.WrittenType)).ToArray();
        if (parameters.Length != 1) { return false; }
        var parameter = parameters[0];
        if ((uint)parameter.ScopeId >= (uint)analysis.Scopes.Length
            || analysis.Scopes[parameter.ScopeId].Owner is not AstCallable owner) { return false; }
        return owner.Parameters.Any(candidate => candidate.IsEscaping
            && candidate.Name == parameter.Name
            && ReferenceEquals(candidate.NameOrigin.Element, parameter.Origin.Element));
    }

    private static bool IsFunctionType(AstType? type) => type?.Shape.Kind == AstTypeKind.Function
        || type?.Shape is { Kind: AstTypeKind.Nullable, Arguments.Length: 1 } nullable
            && nullable.Arguments[0].Kind == AstTypeKind.Function;

    private static bool TryVariable(AstExpression expression, AstSnapshot file,
        Dictionary<AstAtom, NameBinding> bindings, out LocalSymbol local, out AstAtom reference)
    {
        if (Unwrap(expression, file) is AstAtom { SyntaxKind: NodeKind.NameExpression } atom
            && bindings.TryGetValue(atom, out var binding))
        {
            var variables = binding.Lookup.Locals.Where(candidate => candidate.Kind == LocalSymbolKind.Variable).ToArray();
            if (variables.Length == 1)
            { local = variables[0]; reference = atom; return true; }
        }
        local = null!;
        reference = null!;
        return false;
    }

    private static AstExpression Unwrap(AstExpression expression, AstSnapshot file)
    {
        while (expression is AstSequence { IsGrouping: true } group)
        {
            var elements = group.Elements.Take(2).ToArray();
            if (elements.Length != 1) { break; }
            expression = elements[0];
        }
        return expression;
    }

    private static Dictionary<LocalSymbol, HashSet<AstFunction>> Propagate(FlowFacts facts,
        CancellationToken cancellationToken)
    {
        var roots = new Dictionary<LocalSymbol, HashSet<AstFunction>>(LocalComparer);
        var pending = new Queue<LocalSymbol>();
        foreach (var pair in facts.DirectValues)
        {
            roots.Add(pair.Key, new(pair.Value, FunctionComparer));
            pending.Enqueue(pair.Key);
        }

        while (pending.TryDequeue(out var source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(source, out var sourceFunctions)) { continue; }
            foreach (var edge in facts.Edges.GetValueOrDefault(source, []))
            {
                if (!roots.TryGetValue(edge.Target, out var targetFunctions))
                { targetFunctions = new(FunctionComparer); roots.Add(edge.Target, targetFunctions); }
                int oldCount = targetFunctions.Count;
                targetFunctions.UnionWith(sourceFunctions);
                if (targetFunctions.Count != oldCount) { pending.Enqueue(edge.Target); }
            }
        }
        return roots;
    }

    private static (LocalFunctionEscapeUseKind Kind, AstNode Site) ClassifyAliasUse(NameBinding binding,
        AstSnapshot file, Dictionary<AstAtom, NameBinding> bindingsByReference, LexicalAnalysis analysis)
    {
        AstNode value = binding.Reference;
        AstNode? parent = file.GetParent(value);
        while (parent is not null && AnonymousFunctionEscapeAnalysis.IsTransparentWrapper(parent, value))
        { value = parent; parent = file.GetParent(parent); }

        if (binding.CapturesOuterLocal)
        { return (LocalFunctionEscapeUseKind.CallableCapture, parent ?? value); }
        if (parent is AstCall call)
        {
            if (call.Target is { } target
                && AnonymousFunctionEscapeAnalysis.IsTransparentValue(target, binding.Reference, file))
            { return (LocalFunctionEscapeUseKind.DirectInvocation, call); }
            for (int index = 0; index < call.Arguments.Length; index++)
            {
                if (!AnonymousFunctionEscapeAnalysis.IsTransparentValue(call.Arguments[index].Value,
                    binding.Reference, file)) { continue; }
                var contract = EscapeParameterContracts.Resolve(call, index, bindingsByReference,
                    analysis.Declarations, analysis.Types);
                return (contract == CallArgumentEscapeContract.NonEscaping
                    ? LocalFunctionEscapeUseKind.NonescapingCallArgument
                    : LocalFunctionEscapeUseKind.CallArgument, call);
            }
        }
        if (parent is AstReturn { Value: { } returned } statement
            && AnonymousFunctionEscapeAnalysis.IsTransparentValue(returned, binding.Reference, file))
        { return (LocalFunctionEscapeUseKind.ReturnValue, statement); }
        if (AnonymousFunctionEscapeAnalysis.IsCallableTail(value, parent, file, out var callable))
        { return (LocalFunctionEscapeUseKind.ReturnValue, callable); }
        if (parent is AstAssignment { Location: { } location, Value: { } assigned } assignment
            && AnonymousFunctionEscapeAnalysis.IsTransparentValue(assigned, binding.Reference, file))
        {
            return (AnonymousFunctionEscapeAnalysis.IsLocalLocation(location)
                ? LocalFunctionEscapeUseKind.LocalAlias : LocalFunctionEscapeUseKind.HeapStorage, assignment);
        }
        if (parent is AstProperty property && property.Initializer is { } initializer
            && AnonymousFunctionEscapeAnalysis.IsTransparentValue(initializer, binding.Reference, file))
        {
            return (AnonymousFunctionEscapeAnalysis.IsInsideCallable(property, file)
                ? LocalFunctionEscapeUseKind.LocalStorage : LocalFunctionEscapeUseKind.HeapStorage, property);
        }
        if (parent is AstSequence { IsGrouping: false } aggregate)
        { return (LocalFunctionEscapeUseKind.AggregateElement, aggregate); }
        if (parent is AstLambda or AstFunction or AstAnonymousFunction)
        { return (LocalFunctionEscapeUseKind.CallableCapture, parent); }
        return (LocalFunctionEscapeUseKind.UnsupportedContext, parent ?? value);
    }
}
