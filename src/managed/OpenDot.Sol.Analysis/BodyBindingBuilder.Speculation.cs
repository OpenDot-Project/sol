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
    private TResult Speculate<TResult>(State state, Func<TResult> action, out bool reportedDiagnostic)
    {
        var exceptionSink = state.ExceptionSink;
        int exceptionalEdgeCount = exceptionSink?.Count ?? 0;
        var expressionSnapshot = Expressions.ToArray();
        var callSnapshot = Calls.ToArray();
        var operatorSnapshot = OperatorBindings.ToArray();
        var bodySnapshot = Bodies.ToArray();
        var anonymousFunctionBodySnapshot = AnonymousFunctionBodies.ToArray();
        var lambdaBodySnapshot = LambdaBodies.ToArray();
        var localSnapshot = _localTypes.ToArray();
        var interfaceWrites = _interfaceWrites.ToArray();
        var interfaceUses = InterfaceUses.ToArray();
        var interfaceIndexArguments = _interfaceIndexArguments.ToArray();
        var interfaceImplementations = InterfaceImplementations.ToArray();
        var delegateValues = _interfaceDelegateValues.ToArray();
        var interfaceActive = _interfaceResolutionActive.ToArray();
        var constructions = ConstructionProofs.ToArray();
        var constructionAliases = _constructionReceiverValues.ToArray();
        bool returnsConstruction = _constructionReturnsReceiver;
        var constructionSession = _constructionSession;
        var helpers = constructionSession?.Helpers.ToArray();
        var activeHelpers = constructionSession?.ActiveHelpers.ToArray();
        var activeDefaults = constructionSession?.ActiveDefaults.ToArray();
        var activeCallbacks = constructionSession?.ActiveCallbacks.ToArray();
        var callbackInstances = constructionSession?.CallbackInstances.ToArray();
        var helperDependencies = constructionSession?.HelperDependencies.ToArray();
        var helperTypeArguments = constructionSession?.HelperTypeArguments.ToArray();
        var recursiveGroups = constructionSession?.RecursiveGroups.ToArray();
        var constructorInstances = constructionSession?.ConstructorInstances.ToArray();
        var constructionDependencies = constructionSession?.Dependencies.ToArray();
        int constructionInstances = constructionSession?.Instances ?? 0;
        int constructionEdges = constructionSession?.Edges ?? 0;
        int constructionOrigins = constructionSession?.Origins.Count ?? 0;
        bool recursiveHelpers = constructionSession?.RecursiveHelpers ?? false;
        var activeSnapshot = _active.ToArray();
        var reportedSnapshot = _reported.ToArray();
        var constructionErrorSnapshot = _constructionOnlyErrors.ToArray();
        var deferredSnapshot = _deferred.ToArray();
        var diagnosticCount = Diagnostics.Count;
        long errorGeneration = _errorGeneration;
        int depth = _depth;
        reportedDiagnostic = false;
        try
        {
            var result = action();
            reportedDiagnostic = _errorGeneration != errorGeneration;
            return result;
        }
        finally
        {
            Restore(Expressions, expressionSnapshot);
            Restore(Calls, callSnapshot);
            Restore(OperatorBindings, operatorSnapshot);
            Restore(Bodies, bodySnapshot);
            Restore(AnonymousFunctionBodies, anonymousFunctionBodySnapshot);
            Restore(LambdaBodies, lambdaBodySnapshot);
            Restore(_localTypes, localSnapshot);
            Restore(_interfaceWrites, interfaceWrites);
            Restore(InterfaceUses, interfaceUses);
            Restore(_interfaceIndexArguments, interfaceIndexArguments);
            Restore(InterfaceImplementations, interfaceImplementations);
            Restore(_interfaceDelegateValues, delegateValues);
            Restore(_interfaceResolutionActive, interfaceActive);
            Restore(ConstructionProofs, constructions);
            Restore(_constructionReceiverValues, constructionAliases);
            _constructionReturnsReceiver = returnsConstruction;
            if (constructionSession is not null)
            {
                Restore(constructionSession.Helpers, helpers!);
                Restore(constructionSession.ActiveHelpers, activeHelpers!);
                Restore(constructionSession.ActiveDefaults, activeDefaults!);
                Restore(constructionSession.ActiveCallbacks, activeCallbacks!);
                Restore(constructionSession.CallbackInstances, callbackInstances!);
                Restore(constructionSession.HelperDependencies, helperDependencies!);
                Restore(constructionSession.HelperTypeArguments, helperTypeArguments!);
                Restore(constructionSession.RecursiveGroups, recursiveGroups!);
                Restore(constructionSession.ConstructorInstances, constructorInstances!);
                Restore(constructionSession.Dependencies, constructionDependencies!);
                constructionSession.Instances = constructionInstances;
                constructionSession.Edges = constructionEdges;
                constructionSession.RecursiveHelpers = recursiveHelpers;
                if (constructionSession.Origins.Count > constructionOrigins)
                { constructionSession.Origins.RemoveRange(constructionOrigins, constructionSession.Origins.Count - constructionOrigins); }
            }
            Restore(_active, activeSnapshot);
            Restore(_reported, reportedSnapshot);
            Restore(_constructionOnlyErrors, constructionErrorSnapshot);
            Restore(_deferred, deferredSnapshot);
            if (Diagnostics.Count > diagnosticCount) { Diagnostics.RemoveRange(diagnosticCount, Diagnostics.Count - diagnosticCount); }
            _errorGeneration = errorGeneration;
            _depth = depth;
            if (exceptionSink is not null && exceptionSink.Count > exceptionalEdgeCount)
            { exceptionSink.RemoveRange(exceptionalEdgeCount, exceptionSink.Count - exceptionalEdgeCount); }
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
        ResolvedType expected, ImmutableArray<ResolvedType> inferenceParameters, out bool invalid)
    {
        var frame = _constructionFrame;
        _constructionFrame = null;
        try { return Speculate(state, () => Lambda(lambda, state.Clone(), context, expected, inferenceParameters), out invalid); }
        finally { _constructionFrame = frame; }
    }

    private Value AnalyzeExpressionCandidate(AstExpression expression, State state, Context context,
        out bool invalid) => Speculate(state, () => Expression(expression, state.Clone(), context), out invalid);

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
