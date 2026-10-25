// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private TResult Speculate<TResult>(Func<TResult> action, out bool reportedDiagnostic)
    {
        var expressionSnapshot = Expressions.ToArray();
        var callSnapshot = Calls.ToArray();
        var bodySnapshot = Bodies.ToArray();
        var localSnapshot = _localTypes.ToArray();
        var activeSnapshot = _active.ToArray();
        var reportedSnapshot = _reported.ToArray();
        var deferredSnapshot = _deferred.ToArray();
        var diagnosticCount = Diagnostics.Count;
        int depth = _depth;
        reportedDiagnostic = false;
        try
        {
            var result = action();
            reportedDiagnostic = Diagnostics.Count != diagnosticCount;
            return result;
        }
        finally
        {
            Restore(Expressions, expressionSnapshot);
            Restore(Calls, callSnapshot);
            Restore(Bodies, bodySnapshot);
            Restore(_localTypes, localSnapshot);
            Restore(_active, activeSnapshot);
            Restore(_reported, reportedSnapshot);
            Restore(_deferred, deferredSnapshot);
            if (Diagnostics.Count > diagnosticCount) { Diagnostics.RemoveRange(diagnosticCount, Diagnostics.Count - diagnosticCount); }
            _depth = depth;
        }
    }

    private static void Restore<TKey, TValue>(Dictionary<TKey, TValue> destination,
        KeyValuePair<TKey, TValue>[] snapshot) where TKey : notnull
    {
        destination.Clear();
        foreach (var pair in snapshot) { destination.Add(pair.Key, pair.Value); }
    }

    private static void Restore<T>(HashSet<T> destination, T[] snapshot)
    {
        destination.Clear();
        destination.UnionWith(snapshot);
    }

    private Value AnalyzeLambdaCandidate(AstLambda lambda, State state, Context context,
        ResolvedType expected, ImmutableArray<ResolvedType> inferenceParameters, out bool invalid) =>
        Speculate(() => Lambda(lambda, state.Clone(), context, expected, inferenceParameters), out invalid);

    private Value AnalyzeExpressionCandidate(AstExpression expression, State state, Context context,
        out bool invalid) => Speculate(() => Expression(expression, state.Clone(), context), out invalid);

    private bool ContainsInferenceParameter(ResolvedType type,
        ImmutableArray<ResolvedType> parameters)
    {
        if (parameters.IsDefaultOrEmpty) { return false; }
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<ResolvedType>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current)) { continue; }
            if (parameters.Contains(current)) { return true; }
            foreach (var argument in current.Arguments) { pending.Push(argument); }
            foreach (var effect in current.Effects) { pending.Push(effect); }
        }
        return false;
    }

    private ResolvedType? ArrayLiteralCommonType(AstSequence literal, State state, Context context,
        int depth = 0)
    {
        if (depth > 128) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
            "Array literal common-type nesting exceeds the 128-level budget."); }
        var elementTypes = new List<ResolvedType>();
        foreach (var element in literal.Elements)
        {
            _cancellation.ThrowIfCancellationRequested();
            ResolvedType? type;
            if (element is AstSequence { SyntaxKind: NodeKind.ArrayExpression } nested)
            {
                var nestedType = ArrayLiteralCommonType(nested, state, context, depth + 1);
                type = nestedType is null ? null : Types.GetArray(nestedType);
            }
            else if (element is AstExpression expression)
            {
                var value = AnalyzeExpressionCandidate(expression, state, context, out bool invalid);
                type = invalid ? null : value.Type;
            }
            else { type = null; }
            if (type is null) { return null; }
            elementTypes.Add(type);
        }
        return UniqueCommonType(elementTypes);
    }

    private ResolvedType? UniqueCommonType(IEnumerable<ResolvedType> source)
    {
        var inputs = source.Distinct().ToArray();
        if (inputs.Length == 0) { return null; }
        if (inputs.Length == 1) { return inputs[0]; }
        var candidates = new HashSet<ResolvedType>();
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        foreach (var input in inputs) { pending.Enqueue((input, 0)); }
        if (inputs.All(input => !input.IsRefLike && input.Kind != ResolvedTypeKind.Parameter)
            && Core("Object") is { } objectType)
        { candidates.Add(objectType); }
        while (pending.TryDequeue(out var item))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (item.Depth > 128) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Array literal common-type projection exceeds the 128-level budget."); }
            if (!candidates.Add(item.Type)) { continue; }
            if (candidates.Count > 4096) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Array literal common-type projection exceeds the 4096-type budget."); }
            if (item.Type.IsNullable) { pending.Enqueue((item.Type.Arguments[0], item.Depth + 1)); }
            else if (item.Type.Kind is ResolvedTypeKind.Nominal or ResolvedTypeKind.Parameter)
            {
                foreach (var parent in Types.GetDirectBases(item.Type, _cancellation))
                { pending.Enqueue((parent, item.Depth + 1)); }
            }
        }
        if (inputs.Any(input => input.IsNullable))
        {
            foreach (var candidate in candidates.ToArray())
            { if (!candidate.IsNullable) { candidates.Add(Types.GetNullable(candidate)); } }
        }
        var viable = candidates.Where(candidate => inputs.All(input => Converts(input, candidate))).ToArray();
        var mostSpecific = viable.Where(candidate => !viable.Any(other => other != candidate
            && Converts(other, candidate) && !Converts(candidate, other))).ToArray();
        return mostSpecific.Length == 1 ? mostSpecific[0] : null;
    }
}
