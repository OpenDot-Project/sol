// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>How far source analysis can prove a lambda value travels from its creation site.</summary>
public enum LambdaEscapeDisposition
{
    /// <summary>The lambda is used only as the immediate target of its creating call expression.</summary>
    ProvenNonEscaping,
    /// <summary>A source operation can publish the lambda beyond the current activation.</summary>
    MayEscape,
    /// <summary>Alias, control-flow, or callable behavior is not represented by this analysis.</summary>
    Unknown,
}

/// <summary>One source use that determines the lambda's escape disposition.</summary>
public enum LambdaEscapeUseKind
{
    ImmediateInvocation,
    ReturnValue,
    CallArgument,
    LocalStorage,
    LocalAlias,
    LocalInvocation,
    AliasMutation,
    HeapStorage,
    AggregateElement,
    CallableCapture,
    UnsupportedContext,
}

/// <param name="Site">The source AST node that consumes or stores the lambda value.</param>
/// <param name="Kind">The conservative use classification for the node.</param>
public sealed record LambdaEscapeUse(AstNode Site, LambdaEscapeUseKind Kind);

/// <summary>
/// Escape facts for one lambda. This is not an environment layout, borrow check, or permission to
/// emit a closure. In particular, ProvenNonEscaping does not make an otherwise unsupported capture
/// representable in FIR or safe for Yellow Tea.
/// </summary>
/// <param name="Lambda">The source lambda identity.</param>
/// <param name="Disposition">The strongest lifetime statement supported by the source tree.</param>
/// <param name="Uses">The deterministic source witnesses used for the disposition.</param>
/// <param name="Captures">The lambda's symbol-identity capture inventory.</param>
public sealed record LambdaEscape(
    AstLambda Lambda,
    LambdaEscapeDisposition Disposition,
    ImmutableArray<LambdaEscapeUse> Uses,
    ImmutableArray<LambdaCapture> Captures);

internal static class LambdaEscapeAnalysis
{
    internal static ImmutableArray<LambdaEscape> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        ImmutableArray<LambdaCapture> captures,
        CancellationToken cancellationToken)
    {
        IEqualityComparer<AstLambda> comparer = ReferenceEqualityComparer.Instance;
        var capturesByLambda = captures.GroupBy(capture => capture.Lambda, comparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), comparer);
        var aliases = LambdaEscapeAliasAnalysis.Create(scopes, scopeOf, files, bindings,
            capturesByLambda, cancellationToken);
        var escapes = ImmutableArray.CreateBuilder<LambdaEscape>();

        foreach (var lambda in files.Keys.OfType<AstLambda>()
            .OrderBy(node => node.Origin.Source.Path, System.StringComparer.Ordinal)
            .ThenBy(node => node.Origin.Span.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (aliases.TryGetValue(lambda, out var aliasEscape))
            {
                escapes.Add(aliasEscape);
                continue;
            }
            var file = files[lambda];
            AstNode value = lambda;
            AstNode? parent = file.GetParent(value);
            while (parent is AstStructure || parent is not null && IsTransparentWrapper(parent, value))
            {
                value = parent;
                parent = file.GetParent(parent);
            }

            var (disposition, use) = Classify(lambda, value, parent, file);
            escapes.Add(new(lambda, disposition, [use], capturesByLambda.GetValueOrDefault(lambda, [])));
        }

        return escapes.ToImmutable();
    }

    private static bool IsTransparentWrapper(AstNode parent, AstNode value)
    {
        if (parent is AstSequence { IsGrouping: true } grouping)
        {
            var elements = grouping.Elements.Take(2).ToArray();
            return elements.Length == 1 && ReferenceEquals(elements[0], value);
        }
        return parent is AstLabelledExpression labelled && ReferenceEquals(labelled.Lambda, value);
    }

    private static (LambdaEscapeDisposition Disposition, LambdaEscapeUse Use) Classify(
        AstLambda lambda, AstNode value, AstNode? parent, AstSnapshot file)
    {
        if (parent is AstCall call)
        {
            if (ReferenceEquals(call.Target, value))
            { return (LambdaEscapeDisposition.ProvenNonEscaping, new(call, LambdaEscapeUseKind.ImmediateInvocation)); }
            if (call.Arguments.Any(argument => IsTransparentValue(argument.Value, lambda, file)))
            { return (LambdaEscapeDisposition.MayEscape, new(call, LambdaEscapeUseKind.CallArgument)); }
        }

        if (parent is AstReturn { Value: { } returned } statement && ReferenceEquals(returned, value))
        { return (LambdaEscapeDisposition.MayEscape, new(statement, LambdaEscapeUseKind.ReturnValue)); }

        if (IsCallableTail(value, parent, file, out var callable))
        { return (LambdaEscapeDisposition.MayEscape, new(callable, LambdaEscapeUseKind.ReturnValue)); }

        if (parent is AstAssignment { Value: { } assigned, Location: { } location } assignment
            && ReferenceEquals(assigned, value))
        {
            if (IsLocalLocation(location))
            { return (LambdaEscapeDisposition.Unknown, new(assignment, LambdaEscapeUseKind.LocalStorage)); }
            return (LambdaEscapeDisposition.MayEscape, new(assignment, LambdaEscapeUseKind.HeapStorage));
        }

        if (parent is AstProperty { Initializer: { } initializer } property && ReferenceEquals(initializer, value))
        {
            return IsInsideCallable(property, file)
                ? (LambdaEscapeDisposition.Unknown, new(property, LambdaEscapeUseKind.LocalStorage))
                : (LambdaEscapeDisposition.MayEscape, new(property, LambdaEscapeUseKind.HeapStorage));
        }

        if (parent is AstSequence { IsGrouping: false } aggregate)
        { return (LambdaEscapeDisposition.MayEscape, new(aggregate, LambdaEscapeUseKind.AggregateElement)); }

        if (parent is AstLambda or AstFunction)
        { return (LambdaEscapeDisposition.MayEscape, new(parent, LambdaEscapeUseKind.CallableCapture)); }

        return (LambdaEscapeDisposition.Unknown,
            new(parent ?? value, LambdaEscapeUseKind.UnsupportedContext));
    }

    internal static bool IsTransparentValue(AstNode expression, AstNode value, AstSnapshot file)
    {
        while (!ReferenceEquals(value, expression))
        {
            var parent = file.GetParent(value);
            if (parent is null || parent is not AstStructure && !IsTransparentWrapper(parent, value)) { return false; }
            value = parent;
        }
        return true;
    }

    internal static bool IsCallableTail(AstNode value, AstNode? parent, AstSnapshot file, out AstNode callable)
    {
        if (parent is AstFunction function && ReferenceEquals(function.Body.TailExpression, value))
        { callable = function; return true; }
        if (parent is AstLambda lambda && ReferenceEquals(lambda.TailExpression, value))
        { callable = lambda; return true; }

        if (parent is AstExpressionStatement statement && ReferenceEquals(statement.Expression, value)
            && file.GetParent(statement) is AstLambda lambdaBody
            && ReferenceEquals(lambdaBody.Statements.LastOrDefault(), statement))
        { callable = lambdaBody; return true; }

        if (parent is AstExpressionStatement tailStatement && ReferenceEquals(tailStatement.Expression, value)
            && file.GetParent(tailStatement) is AstBlock block
            && ReferenceEquals(block.Statements.LastOrDefault(), tailStatement))
        {
            var owner = file.GetParent(block);
            if (owner is AstFunction { Body.Node: { } body } functionOwner && ReferenceEquals(body, block))
            { callable = functionOwner; return true; }
            if (owner is AstLambda lambdaOwner && ReferenceEquals(lambdaOwner.Statements.LastOrDefault(), block))
            { callable = lambdaOwner; return true; }
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
            if (parent is AstCallable or AstLambda) { return true; }
            if (parent is AstTypeDeclaration or AstFile) { return false; }
        }
        return false;
    }
}
