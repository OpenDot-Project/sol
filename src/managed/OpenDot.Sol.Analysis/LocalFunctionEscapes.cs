// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>How source uses affect a local named function's environment lifetime candidate.</summary>
public enum LocalFunctionEscapeDisposition
{
    /// <summary>Every observed use stays in local storage or invokes a proven local alias.</summary>
    ProvenNonEscaping,
    /// <summary>A modeled use publishes the function value beyond the current callable.</summary>
    MayEscape,
    /// <summary>An alias, nested callable, or unsupported consumer prevents a lifetime conclusion.</summary>
    Unknown,
}

/// <summary>A source witness for a local named function use.</summary>
public enum LocalFunctionEscapeUseKind
{
    /// <summary>The local function name or its resolved local alias is the target of a call.</summary>
    DirectInvocation,
    /// <summary>The local function value is returned from a callable.</summary>
    ReturnValue,
    /// <summary>The local function value is passed to a call without a no-escape contract.</summary>
    CallArgument,
    /// <summary>The local function value is passed to a parameter with a known no-escape contract.</summary>
    NonescapingCallArgument,
    /// <summary>The local function value is stored in a local binding or alias.</summary>
    LocalStorage,
    /// <summary>A local binding forwards the function value to another local binding.</summary>
    LocalAlias,
    /// <summary>The local function value is stored in a field or other nonlocal location.</summary>
    HeapStorage,
    /// <summary>The local function value becomes an element of an aggregate.</summary>
    AggregateElement,
    /// <summary>A nested callable retains the local function binding or its environment.</summary>
    CallableCapture,
    /// <summary>The local function binding is assigned or rebound through an unmodeled path.</summary>
    AliasMutation,
    /// <summary>The value reaches a consumer for which this source analysis has no lifetime rule.</summary>
    UnsupportedContext,
}

/// <param name="Site">The AST operation witnessing the local function use.</param>
/// <param name="Kind">The conservative source classification for that operation.</param>
public sealed record LocalFunctionEscapeUse(AstNode Site, LocalFunctionEscapeUseKind Kind);

/// <summary>
/// Escape facts for one local named-function declaration. The local symbol is its identity; the
/// declaration spelling is never used to join references. These facts are not a closure ABI,
/// allocation decision, or permission to lower a function value.
/// </summary>
/// <param name="Function">The local named-function declaration.</param>
/// <param name="Local">The exact resolved function symbol declared by <paramref name="Function"/>.</param>
/// <param name="Disposition">The strongest lifetime result supported by modeled source uses.</param>
/// <param name="Uses">Deterministically ordered source witnesses.</param>
public sealed record LocalFunctionEscape(
    AstFunction Function,
    LocalSymbol Local,
    LocalFunctionEscapeDisposition Disposition,
    ImmutableArray<LocalFunctionEscapeUse> Uses);

/// <summary>
/// Classifies uses of local named-function symbols in one lexical snapshot. Resolved local aliases
/// are propagated conservatively; nested-callable environment lifetime remains unknown.
/// </summary>
public sealed class LocalFunctionEscapeAnalysis
{
    private readonly Dictionary<AstFunction, LocalFunctionEscape> _byFunction;
    private readonly ImmutableHashSet<AstFunction> _ownedFunctions;

    private LocalFunctionEscapeAnalysis(ImmutableArray<LocalFunctionEscape> escapes,
        IEnumerable<AstFunction> ownedFunctions)
    {
        Escapes = escapes;
        _byFunction = escapes.ToDictionary<LocalFunctionEscape, AstFunction, LocalFunctionEscape>(
            escape => escape.Function, escape => escape, ReferenceEqualityComparer.Instance);
        _ownedFunctions = ImmutableHashSet.CreateRange<AstFunction>(ReferenceEqualityComparer.Instance,
            ownedFunctions);
    }

    /// <summary>Escapes for all local named functions, ordered by source path and span.</summary>
    public ImmutableArray<LocalFunctionEscape> Escapes { get; }

    /// <summary>Builds source escape facts from resolved local-symbol bindings.</summary>
    public static LocalFunctionEscapeAnalysis Create(LexicalAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();

        var ownedFunctions = analysis.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>().ToArray();
        var functionsByOrigin = new Dictionary<object, AstFunction>(ReferenceEqualityComparer.Instance);
        foreach (var function in ownedFunctions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            functionsByOrigin.TryAdd(function.Origin.Element, function);
        }

        var functionsByLocal = new Dictionary<LocalSymbol, AstFunction>(ReferenceEqualityComparer.Instance);
        foreach (var local in analysis.Scopes.SelectMany(scope => scope.Symbols)
            .Where(symbol => symbol.Kind == LocalSymbolKind.Function))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (functionsByOrigin.TryGetValue(local.Origin.Element, out var function))
            { functionsByLocal.TryAdd(local, function); }
        }

        var usesByLocal = functionsByLocal.Keys.ToDictionary<LocalSymbol, LocalSymbol,
            List<LocalFunctionEscapeUse>>(local => local, _ => new List<LocalFunctionEscapeUse>(),
                ReferenceEqualityComparer.Instance);
        var files = new Dictionary<AstNode, AstSnapshot>(ReferenceEqualityComparer.Instance);
        foreach (var file in analysis.Declarations.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var node in file.Nodes) { files.Add(node, file); }
        }

        var bindingArray = analysis.Bindings.ToArray();
        IEqualityComparer<AstAtom> atomComparer = ReferenceEqualityComparer.Instance;
        var bindingsByReference = bindingArray.ToDictionary(binding => binding.Reference, atomComparer);
        var aliasFacts = LocalFunctionEscapeAliasAnalysis.Create(analysis, functionsByLocal,
            bindingsByReference, cancellationToken);
        foreach (var use in aliasFacts.Uses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            usesByLocal[use.FunctionLocal].Add(new(use.Site, use.Kind));
        }
        foreach (var binding in bindingArray)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var local in binding.Lookup.Locals.Where(symbol => symbol.Kind == LocalSymbolKind.Function))
            {
                if (!usesByLocal.TryGetValue(local, out var uses)
                    || aliasFacts.SourceReferences.Contains(binding.Reference)) { continue; }
                foreach (var use in Classify(binding, functionsByLocal[local], files[binding.Reference],
                    bindingsByReference, analysis))
                { uses.Add(use); }
            }
        }

        var escapes = ImmutableArray.CreateBuilder<LocalFunctionEscape>(functionsByLocal.Count);
        foreach (var pair in functionsByLocal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uses = usesByLocal[pair.Key].Distinct()
                .OrderBy(use => use.Site.Origin.Source.Path, StringComparer.Ordinal)
                .ThenBy(use => use.Site.Origin.Span.Start)
                .ThenBy(use => use.Kind)
                .ToImmutableArray();
            escapes.Add(new(pair.Value, pair.Key, DispositionOf(uses), uses));
        }

        var orderedEscapes = escapes
            .OrderBy(escape => escape.Function.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(escape => escape.Function.Origin.Span.Start)
            .ThenBy(escape => escape.Local.Id)
            .ToImmutableArray();

        return new(orderedEscapes, ownedFunctions);
    }

    /// <summary>Returns facts for a local named function owned by this lexical snapshot.</summary>
    public LocalFunctionEscape GetEscape(AstFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (_byFunction.TryGetValue(function, out var escape)) { return escape; }
        if (!_ownedFunctions.Contains(function))
        { throw new ArgumentException("The function belongs to another lexical snapshot.", nameof(function)); }
        throw new ArgumentException("The function is not a local named-function declaration.", nameof(function));
    }

    private static ImmutableArray<LocalFunctionEscapeUse> Classify(NameBinding binding,
        AstFunction function, AstSnapshot file, Dictionary<AstAtom, NameBinding> bindings,
        LexicalAnalysis analysis)
    {
        AstNode value = binding.Reference;
        AstNode? parent = file.GetParent(value);
        while (parent is not null && AnonymousFunctionEscapeAnalysis.IsTransparentWrapper(parent, value))
        {
            value = parent;
            parent = file.GetParent(parent);
        }
        // Explicit generic application is part of a direct call target. It is not a general
        // transparent function-value wrapper: storing or passing that value stays conservative.
        if (parent is AstGenericApplication application && ReferenceEquals(application.Target, value)
            && file.GetParent(application) is AstCall genericCall && ReferenceEquals(genericCall.Target, application))
        {
            value = application;
            parent = genericCall;
        }

        var uses = ImmutableArray.CreateBuilder<LocalFunctionEscapeUse>(2);
        bool referenceIsInsideFunction = NearestFunction(value, file) is { } owner
            && ReferenceEquals(owner, function);
        if (binding.CapturesOuterLocal && !referenceIsInsideFunction)
        { uses.Add(new(NearestCallable(value, file) ?? parent ?? value, LocalFunctionEscapeUseKind.CallableCapture)); }

        if (parent is AstCall call)
        {
            if (call.Target is { } target
                && (AnonymousFunctionEscapeAnalysis.IsTransparentValue(target, binding.Reference, file)
                    || target is AstGenericApplication { Target: { } genericTarget }
                        && AnonymousFunctionEscapeAnalysis.IsTransparentValue(genericTarget, binding.Reference, file)))
            { uses.Add(new(call, LocalFunctionEscapeUseKind.DirectInvocation)); }
            else if (call.Arguments.Select((argument, index) => (argument, index))
                .FirstOrDefault(item => AnonymousFunctionEscapeAnalysis.IsTransparentValue(
                    item.argument.Value, binding.Reference, file)) is var found
                && found.argument is not null)
            {
                var contract = EscapeParameterContracts.Resolve(call, found.index, bindings,
                    analysis.Declarations, analysis.Types);
                uses.Add(new(call, contract == CallArgumentEscapeContract.NonEscaping
                    ? LocalFunctionEscapeUseKind.NonescapingCallArgument
                    : LocalFunctionEscapeUseKind.CallArgument));
            }
            else
            { uses.Add(new(call, LocalFunctionEscapeUseKind.UnsupportedContext)); }
            return uses.ToImmutable();
        }

        if (parent is AstReturn { Value: { } returned } statement
            && AnonymousFunctionEscapeAnalysis.IsTransparentValue(returned, binding.Reference, file))
        { uses.Add(new(statement, LocalFunctionEscapeUseKind.ReturnValue)); return uses.ToImmutable(); }
        if (AnonymousFunctionEscapeAnalysis.IsCallableTail(value, parent, file, out var callable))
        { uses.Add(new(callable, LocalFunctionEscapeUseKind.ReturnValue)); return uses.ToImmutable(); }

        if (parent is AstAssignment { Location: { } location, Value: { } assigned } assignment)
        {
            if (AnonymousFunctionEscapeAnalysis.IsTransparentValue(location, binding.Reference, file))
            { uses.Add(new(assignment, LocalFunctionEscapeUseKind.AliasMutation)); }
            else if (AnonymousFunctionEscapeAnalysis.IsTransparentValue(assigned, binding.Reference, file))
            {
                uses.Add(new(assignment, IsLocalLocation(location)
                    ? LocalFunctionEscapeUseKind.LocalStorage : LocalFunctionEscapeUseKind.HeapStorage));
            }
            else
            { uses.Add(new(assignment, LocalFunctionEscapeUseKind.UnsupportedContext)); }
            return uses.ToImmutable();
        }

        if (parent is AstProperty property && property.Initializer is { } initializer
            && AnonymousFunctionEscapeAnalysis.IsTransparentValue(initializer, binding.Reference, file))
        {
            uses.Add(new(property, AnonymousFunctionEscapeAnalysis.IsInsideCallable(property, file)
                ? LocalFunctionEscapeUseKind.LocalStorage : LocalFunctionEscapeUseKind.HeapStorage));
            return uses.ToImmutable();
        }
        if (parent is AstSequence { IsGrouping: false } aggregate)
        { uses.Add(new(aggregate, LocalFunctionEscapeUseKind.AggregateElement)); return uses.ToImmutable(); }
        if (parent is AstFunction or AstLambda or AstAnonymousFunction)
        { uses.Add(new(parent, LocalFunctionEscapeUseKind.CallableCapture)); return uses.ToImmutable(); }

        uses.Add(new(parent ?? value, LocalFunctionEscapeUseKind.UnsupportedContext));
        return uses.ToImmutable();
    }

    private static AstFunction? NearestFunction(AstNode node, AstSnapshot file)
    {
        for (AstNode? current = node; current is not null; current = file.GetParent(current))
        {
            if (current is AstFunction function) { return function; }
            if (current is AstLambda or AstAnonymousFunction) { return null; }
        }
        return null;
    }

    private static AstNode? NearestCallable(AstNode node, AstSnapshot file)
    {
        for (AstNode? current = node; current is not null; current = file.GetParent(current))
        {
            if (current is AstCallable or AstLambda or AstAnonymousFunction) { return current; }
        }
        return null;
    }

    private static bool IsLocalLocation(AstExpression location)
    {
        if (location is AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression }) { return true; }
        if (location is not AstSequence { IsGrouping: true } group) { return false; }
        var elements = group.Elements.Take(2).ToArray();
        return elements.Length == 1 && IsLocalLocation(elements[0]);
    }

    private static LocalFunctionEscapeDisposition DispositionOf(ImmutableArray<LocalFunctionEscapeUse> uses)
    {
        if (uses.Any(use => use.Kind is LocalFunctionEscapeUseKind.ReturnValue
            or LocalFunctionEscapeUseKind.CallArgument or LocalFunctionEscapeUseKind.HeapStorage
            or LocalFunctionEscapeUseKind.AggregateElement))
        { return LocalFunctionEscapeDisposition.MayEscape; }
        if (uses.Any(use => use.Kind is LocalFunctionEscapeUseKind.CallableCapture
            or LocalFunctionEscapeUseKind.AliasMutation
            or LocalFunctionEscapeUseKind.UnsupportedContext))
        { return LocalFunctionEscapeDisposition.Unknown; }
        return LocalFunctionEscapeDisposition.ProvenNonEscaping;
    }
}
