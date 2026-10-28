// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>How far source analysis can classify an anonymous-function value's lifetime.</summary>
public enum AnonymousFunctionEscapeDisposition
{
    /// <summary>Every modeled use is local and does not publish the function value.</summary>
    ProvenNonEscaping,
    /// <summary>A modeled use may publish the value beyond the current activation.</summary>
    MayEscape,
    /// <summary>Alias, control-flow, or consumer behavior is not represented well enough to decide.</summary>
    Unknown,
}

/// <summary>A source operation that contributes evidence to an anonymous function's escape result.</summary>
public enum AnonymousFunctionEscapeUseKind
{
    /// <summary>Invoked at the creation expression without first being stored or passed elsewhere.</summary>
    ImmediateInvocation,
    /// <summary>Returned from the current callable.</summary>
    ReturnValue,
    /// <summary>Passed to a callable without a no-escape contract.</summary>
    CallArgument,
    /// <summary>Passed to a function parameter whose no-escape contract is known.</summary>
    NonescapingCallArgument,
    /// <summary>Stored in a local binding.</summary>
    LocalStorage,
    /// <summary>Copied into another local binding by a tracked alias.</summary>
    LocalAlias,
    /// <summary>Invoked through a local binding.</summary>
    LocalInvocation,
    /// <summary>Reassigned or subject to an incomplete alias update.</summary>
    AliasMutation,
    /// <summary>Stored outside a local binding.</summary>
    HeapStorage,
    /// <summary>Stored in an array, tuple, or other aggregate expression.</summary>
    AggregateElement,
    /// <summary>Retained by a nested callable or otherwise forwarded through a callable boundary.</summary>
    CallableCapture,
    /// <summary>Consumed in a context for which the classifier has no lifetime rule.</summary>
    UnsupportedContext,
}

/// <param name="Site">The source AST node that consumes, stores, or publishes the value.</param>
/// <param name="Kind">The conservative use classification for this source site.</param>
public sealed record AnonymousFunctionEscapeUse(AstNode Site, AnonymousFunctionEscapeUseKind Kind);

/// <summary>
/// Source-use facts for one anonymous function. They do not prove borrow safety, closure layout,
/// heap/root ownership, or permission to emit native code.
/// </summary>
/// <param name="Function">The source anonymous-function identity.</param>
/// <param name="Disposition">The strongest lifetime classification supported by modeled uses.</param>
/// <param name="Uses">Deterministic source witnesses used for the classification.</param>
/// <param name="Captures">Captured local symbols, separate from enclosing receiver captures.</param>
/// <param name="ReceiverCaptures">Enclosing receiver requirements found for this function.</param>
public sealed record AnonymousFunctionEscape(
    AstAnonymousFunction Function,
    AnonymousFunctionEscapeDisposition Disposition,
    ImmutableArray<AnonymousFunctionEscapeUse> Uses,
    ImmutableArray<AnonymousFunctionCapture> Captures,
    ImmutableArray<AnonymousFunctionReceiverCapture> ReceiverCaptures);

internal static class AnonymousFunctionEscapeAnalysis
{
    internal static ImmutableArray<AnonymousFunctionEscape> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        ImmutableArray<AnonymousFunctionCapture> captures,
        ImmutableArray<AnonymousFunctionReceiverCapture> receiverCaptures,
        AnalysisSession declarations,
        TypeAnalysis types,
        CancellationToken cancellationToken)
    {
        IEqualityComparer<AstAnonymousFunction> comparer = ReferenceEqualityComparer.Instance;
        var capturesByFunction = captures.GroupBy(capture => capture.Function, comparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), comparer);
        var receiversByFunction = receiverCaptures.GroupBy(capture => capture.Function, comparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), comparer);
        var bindingArray = bindings.ToArray();
        IEqualityComparer<AstAtom> atomComparer = ReferenceEqualityComparer.Instance;
        var bindingByReference = bindingArray.ToDictionary(binding => binding.Reference, atomComparer);
        var aliases = AnonymousFunctionEscapeAliasAnalysis.Create(scopes, scopeOf, files, bindingByReference.Values,
            capturesByFunction, receiversByFunction, declarations, types, cancellationToken);
        var escapes = ImmutableArray.CreateBuilder<AnonymousFunctionEscape>();

        foreach (var function in files.Keys.OfType<AstAnonymousFunction>()
            .OrderBy(node => node.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(node => node.Origin.Span.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (aliases.TryGetValue(function, out var aliasEscape))
            {
                escapes.Add(aliasEscape);
                continue;
            }

            var file = files[function];
            AstNode value = function;
            AstNode? parent = file.GetParent(value);
            while (parent is not null && IsTransparentWrapper(parent, value))
            {
                value = parent;
                parent = file.GetParent(parent);
            }

            var (disposition, use) = Classify(function, value, parent, file, bindingByReference, declarations, types);
            escapes.Add(new(function, disposition, [use], capturesByFunction.GetValueOrDefault(function, []),
                receiversByFunction.GetValueOrDefault(function, [])));
        }

        return escapes.ToImmutable();
    }

    internal static bool IsTransparentWrapper(AstNode parent, AstNode value)
    {
        if (parent is AstStructure structure)
        { return structure.Children.Length == 1 && ReferenceEquals(structure.Children[0], value); }
        if (parent is AstSequence { IsGrouping: true } grouping)
        {
            var elements = grouping.Elements.Take(2).ToArray();
            return elements.Length == 1 && ReferenceEquals(elements[0], value);
        }
        return false;
    }

    private static (AnonymousFunctionEscapeDisposition Disposition, AnonymousFunctionEscapeUse Use) Classify(
        AstAnonymousFunction function, AstNode value, AstNode? parent, AstSnapshot file,
        Dictionary<AstAtom, NameBinding> bindings, AnalysisSession declarations, TypeAnalysis types)
    {
        if (parent is AstCall call)
        {
            if (call.Target is { } target && IsTransparentValue(target, function, file))
            { return (AnonymousFunctionEscapeDisposition.ProvenNonEscaping,
                new(call, AnonymousFunctionEscapeUseKind.ImmediateInvocation)); }
            for (int index = 0; index < call.Arguments.Length; index++)
            {
                if (!IsTransparentValue(call.Arguments[index].Value, function, file)) { continue; }
                var contract = EscapeParameterContracts.Resolve(call, index, bindings,
                    declarations, types);
                return contract == CallArgumentEscapeContract.NonEscaping
                    ? (AnonymousFunctionEscapeDisposition.ProvenNonEscaping,
                        new(call, AnonymousFunctionEscapeUseKind.NonescapingCallArgument))
                    : (AnonymousFunctionEscapeDisposition.MayEscape,
                        new(call, AnonymousFunctionEscapeUseKind.CallArgument));
            }
        }

        if (parent is AstReturn { Value: { } returned } statement
            && IsTransparentValue(returned, function, file))
        { return (AnonymousFunctionEscapeDisposition.MayEscape,
            new(statement, AnonymousFunctionEscapeUseKind.ReturnValue)); }

        if (IsCallableTail(function, parent, file, out var callable))
        { return (AnonymousFunctionEscapeDisposition.MayEscape,
            new(callable, AnonymousFunctionEscapeUseKind.ReturnValue)); }

        if (parent is AstAssignment { Value: { } assigned, Location: { } location } assignment
            && IsTransparentValue(assigned, function, file))
        {
            if (IsLocalLocation(location))
            { return (AnonymousFunctionEscapeDisposition.Unknown,
                new(assignment, AnonymousFunctionEscapeUseKind.LocalStorage)); }
            return (AnonymousFunctionEscapeDisposition.MayEscape,
                new(assignment, AnonymousFunctionEscapeUseKind.HeapStorage));
        }

        if (parent is AstProperty { Initializer: { } initializer } property
            && IsTransparentValue(initializer, function, file))
        {
            return IsInsideCallable(property, file)
                ? (AnonymousFunctionEscapeDisposition.Unknown,
                    new(property, AnonymousFunctionEscapeUseKind.LocalStorage))
                : (AnonymousFunctionEscapeDisposition.MayEscape,
                    new(property, AnonymousFunctionEscapeUseKind.HeapStorage));
        }

        if (parent is AstSequence { IsGrouping: false } aggregate)
        { return (AnonymousFunctionEscapeDisposition.MayEscape,
            new(aggregate, AnonymousFunctionEscapeUseKind.AggregateElement)); }

        if (parent is AstLambda or AstAnonymousFunction or AstFunction)
        { return (AnonymousFunctionEscapeDisposition.Unknown,
            new(parent, AnonymousFunctionEscapeUseKind.CallableCapture)); }

        return (AnonymousFunctionEscapeDisposition.Unknown,
            new(parent ?? value, AnonymousFunctionEscapeUseKind.UnsupportedContext));
    }

    internal static bool IsTransparentValue(AstNode expression, AstNode value, AstSnapshot file)
    {
        while (!ReferenceEquals(value, expression))
        {
            var parent = file.GetParent(value);
            if (parent is null || !IsTransparentWrapper(parent, value)) { return false; }
            value = parent;
        }
        return true;
    }

    internal static bool IsCallableTail(AstNode value, AstNode? parent, AstSnapshot file, out AstNode callable)
    {
        for (AstNode? current = parent; current is not null; current = file.GetParent(current))
        {
            switch (current)
            {
                case AstFunction function:
                    callable = function;
                    return function.Body.TailExpression is { } functionTail
                        && IsTransparentValue(functionTail, value, file);
                case AstLambda lambda:
                    callable = lambda;
                    return lambda.TailExpression is { } lambdaTail && IsTransparentValue(lambdaTail, value, file);
                case AstAnonymousFunction anonymous:
                    callable = anonymous;
                    return anonymous.Body.TailExpression is { } anonymousTail
                        && IsTransparentValue(anonymousTail, value, file);
                case AstAccessor accessor:
                    callable = accessor;
                    return accessor.Body.TailExpression is { } accessorTail
                        && IsTransparentValue(accessorTail, value, file);
                case AstCallable otherCallable:
                    callable = otherCallable;
                    return false;
            }
        }

        callable = value;
        return false;
    }

    internal static bool IsLocalLocation(AstExpression location)
    {
        if (location is AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression }) { return true; }
        if (location is not AstSequence { IsGrouping: true } group) { return false; }
        var elements = group.Elements.Take(2).ToArray();
        return elements.Length == 1 && IsLocalLocation(elements[0]);
    }

    internal static bool IsInsideCallable(AstNode node, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        {
            if (parent is AstCallable or AstLambda or AstAnonymousFunction) { return true; }
            if (parent is AstTypeDeclaration or AstFile) { return false; }
        }
        return false;
    }
}
