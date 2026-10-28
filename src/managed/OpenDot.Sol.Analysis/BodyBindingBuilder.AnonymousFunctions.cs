// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private const int MaximumAnonymousFunctionBodyNodes = 65536;
    private const int MaximumAnonymousFunctionResultPaths = 4096;

    private Value AnonymousFunction(AstAnonymousFunction function, State outer, Context parent,
        ResolvedType? expected)
    {
        // The value expression belongs to the enclosing body, but its body has an independent
        // expression-depth budget. Do not count nested callable bodies as one recursive expression.
        int enclosingDepth = _depth;
        _depth = 0;
        try { return AnalyzeAnonymousFunction(function, outer, parent, expected); }
        finally { _depth = enclosingDepth; }
    }

    private Value AnalyzeAnonymousFunction(AstAnonymousFunction function, State outer, Context parent,
        ResolvedType? expected)
    {
        _cancellation.ThrowIfCancellationRequested();
        int diagnosticStart = Diagnostics.Count;
        var reportedBefore = _reported.ToHashSet();
        var expectedFunction = expected?.Kind == ResolvedTypeKind.Function ? expected
            : expected is { IsNullable: true, Arguments.Length: 1 }
                && expected.Arguments[0].Kind == ResolvedTypeKind.Function
                ? expected.Arguments[0] : null;
        if (expected is not null && expectedFunction is null)
        {
            Report("SOL2220", "An anonymous function requires a function type as its expected type.", function);
            return new(null);
        }
        if (expectedFunction is not null && expectedFunction.ParameterModes.Length != function.Parameters.Length)
        {
            Report("SOL2220", "Anonymous function parameter count differs from the expected function type.", function);
            return new(null);
        }

        var parameterTypes = ImmutableArray.CreateBuilder<ResolvedType>(function.Parameters.Length);
        var parameterModes = ImmutableArray.CreateBuilder<AstPassingMode>(function.Parameters.Length);
        var localScope = OwnLocals(function).ToArray();
        ValidateAnonymousFunctionParameters(function);
        ValidateEscapingParameterAnnotations(function, function.Parameters);
        foreach (var parameter in function.Parameters.Where(parameter => parameter.DefaultValue is not null))
        {
            Report("SOL2226", "Anonymous functions cannot declare default parameter values.", parameter.DefaultValue!);
        }
        var state = outer.Clone();
        state.Effects.Clear();
        // The function body runs only when this value is invoked, outside the creator's exception region.
        state.ExceptionSink = null;
        for (int index = 0; index < function.Parameters.Length; index++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var parameter = function.Parameters[index];
            var mode = parameter.PassingMode == AstPassingMode.Vararg
                ? AstPassingMode.Value : parameter.PassingMode;
            var expectedParameterType = expectedFunction?.Arguments[index];
            var elementType = parameter.Type is null
                ? parameter.PassingMode == AstPassingMode.Vararg
                    && expectedParameterType?.Kind == ResolvedTypeKind.Array
                    ? expectedParameterType.Arguments[0]
                    : expectedParameterType
                : Written(parameter.Type);
            if (elementType is null || elementType.IsError)
            {
                if (elementType is null)
                { Report("SOL2220", "An anonymous function parameter needs a written or contextual type.", function); }
                else { Defer(function); }
                return new(null);
            }

            var type = parameter.PassingMode == AstPassingMode.Vararg ? Types.GetArray(elementType) : elementType;
            if (expectedFunction is not null)
            {
                var expectedType = expectedFunction.Arguments[index];
                bool compatible = mode == expectedFunction.ParameterModes[index]
                    && (mode is AstPassingMode.Ref or AstPassingMode.Out
                        ? type == expectedType : Converts(expectedType, type));
                if (!compatible)
                { Report("SOL2220", "Anonymous function parameter type or passing mode differs from the expected function type.", function); }
            }

            parameterTypes.Add(type);
            parameterModes.Add(mode);
            foreach (var local in localScope.Where(local => local.Name == parameter.Name
                && ReferenceEquals(local.Origin.Element, parameter.NameOrigin.Element)))
            {
                _localTypes[local] = type;
                if (parameter.PassingMode != AstPassingMode.Out) { state.Assigned.Add(local); }
            }

            // A vararg declaration has the ordinary fixed function-value parameter type []T.
            // Expansion belongs to declaration-call mapping and is never inferred from an
            // indirect function type.
        }

        ValidateAnonymousFunctionCaptures(function, parent.Callable);
        if (function.Body.Kind == AstBodyKind.Missing || function.Body.Node is null)
        {
            Defer(function);
            return new(null);
        }

        ResolvedType? resultType = function.ReturnType is not null ? Written(function.ReturnType)
            : expectedFunction?.Arguments[^1];
        if (resultType?.IsError == true)
        { Defer(function); return new(null); }
        var exits = new List<State>();
        if (resultType is null)
        {
            resultType = InferAnonymousFunctionResult(function, state, exits);
            if (resultType is null) { return new(null); }
        }

        var context = new Context(null, resultType, Exits: exits);
        Value bodyResult;
        if (function.Body.Kind == AstBodyKind.Expression && function.Body.Node is AstExpression expression)
        {
            bodyResult = Expression(expression, state, context, resultType);
            Require(bodyResult, resultType, expression, state);
            if (bodyResult.Type?.IsRefLike == true) { CheckEscape(bodyResult, expression, context); }
        }
        else if (function.Body.Node is AstBlock block)
        {
            if (block.Statements.IsEmpty && resultType != Empty)
            {
                Report("SOL2247", "A reachable normal path has no result value.", function);
                bodyResult = new(Empty);
            }
            else
            {
                bodyResult = Statements(block.Statements, state, context, resultContext: true);
                if (function.Body.TailExpression is { } tail)
                { Require(bodyResult, resultType, tail, state); }
            }
        }
        else
        {
            Defer(function);
            return new(null);
        }

        ValidateAnonymousFunctionOutParameters(function, state, exits, localScope);
        var bodyEffects = exits.Append(state).SelectMany(exit => exit.Effects)
            .Where(IsChecked).Distinct().ToImmutableArray();
        var declaredEffects = function.ThrownTypes.Select(Written).ToImmutableArray();
        foreach (var effect in declaredEffects) { CheckThrowable(effect, function); }

        ImmutableArray<ResolvedType> functionEffects;
        if (!declaredEffects.IsEmpty)
        {
            if (bodyEffects.Any(effect => !declaredEffects.Any(allowed => Converts(effect, allowed))))
            { Report("SOL2250", "Checked exception is not declared by the anonymous function.", function); }
            functionEffects = declaredEffects;
        }
        else if (expectedFunction is not null)
        {
            if (bodyEffects.Any(effect => !expectedFunction.Effects.Any(allowed => Converts(effect, allowed))))
            { Report("SOL2253", "Anonymous function checked effects exceed the expected function contract.", function); }
            functionEffects = expectedFunction.Effects;
        }
        else { functionEffects = bodyEffects; }

        var functionType = Types.GetFunction(parameterTypes, resultType, parameterModes, functionEffects);
        ValidateFunctionEffects(functionType, function);
        var normalExits = exits.Append(state).Where(exit => exit.Reachable).ToArray();
        var outParameters = ImmutableArray.CreateBuilder<AnonymousFunctionOutParameterSummary>();
        foreach (var parameter in function.Parameters.Where(parameter => parameter.PassingMode == AstPassingMode.Out))
        {
            var local = localScope.FirstOrDefault(local => local.Name == parameter.Name
                && ReferenceEquals(local.Origin.Element, parameter.NameOrigin.Element));
            outParameters.Add(new(parameter, local is not null
                && normalExits.All(exit => exit.Assigned.Contains(local))));
        }

        var bodyNodes = new HashSet<AstNode>(function.Descendants(), ReferenceEqualityComparer.Instance)
        { function };
        bool hasDeferredDescendant = _deferred.Any(bodyNodes.Contains);
        bool hasNewError = Diagnostics.Skip(diagnosticStart).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            || _reported.Any(reported => !reportedBefore.Contains(reported) && bodyNodes.Contains(reported.Item1));
        AnonymousFunctionBodies[function] = new(function, functionType, normalExits.Length, state.Reachable,
            outParameters.ToImmutable(), !hasDeferredDescendant && !hasNewError);
        return new(functionType);
    }

    private void ValidateAnonymousFunctionParameters(AstAnonymousFunction function)
    {
        foreach (var group in function.Parameters.GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        { Report("SOL2005", "Duplicate parameter name: " + group.Key, function); }

        var varargs = function.Parameters.Where(parameter => parameter.PassingMode == AstPassingMode.Vararg).ToArray();
        if (varargs.Length > 1) { Report("SOL2005", "Only one vararg parameter is permitted.", function); }
        foreach (var parameter in varargs)
        {
            if (function.Parameters.Count(candidate => ReferenceEquals(
                    candidate.GroupOrigin.Element, parameter.GroupOrigin.Element)) > 1)
            { Report("SOL2005", "A vararg parameter requires a single-name parameter group.", function); }
        }
    }

    private ResolvedType? InferAnonymousFunctionResult(AstAnonymousFunction function, State state,
        List<State> exits)
    {
        var candidates = AnonymousFunctionResultExpressions(function);
        if (candidates.Count == 0)
        {
            if (function.Body.Node is AstBlock block)
            {
                var probeState = state.Clone();
                var probeExits = new List<State>();
                var probeContext = new Context(null, null, Exits: probeExits);
                var probe = Speculate(probeState,
                    () => Statements(block.Statements, probeState, probeContext, resultContext: true),
                    out bool reportedDiagnostic);
                if (reportedDiagnostic || probe.Type is null)
                { Defer(function); return null; }
                if (!probeState.Reachable && !probeExits.Any(exit => exit.Reachable))
                { return Types.GetBottom(); }
            }
            return Empty;
        }

        var types = ImmutableArray.CreateBuilder<ResolvedType>(candidates.Count);
        var context = new Context(null, null, Exits: exits);
        foreach (var candidate in candidates)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (candidate is null)
            {
                types.Add(Empty);
                continue;
            }

            var value = AnalyzeExpressionCandidate(candidate, state, context, out bool invalid);
            if (invalid || value.Type is null || value.Type.IsError)
            {
                Defer(candidate);
                return null;
            }
            types.Add(value.Type);
        }

        var inferred = UniqueCommonType(types);
        if (inferred is null)
        {
            Report("SOL2200", "Anonymous function return paths do not have a common inferred type.", function);
            Defer(function);
        }
        return inferred;
    }

    private List<AstExpression?> AnonymousFunctionResultExpressions(AstAnonymousFunction function)
    {
        if (function.Body.Kind == AstBodyKind.Expression)
        {
            return function.Body.Node is AstExpression expression ? [expression] : [];
        }

        var result = new List<AstExpression?>();
        var root = function.Body.Node;
        if (root is null) { return result; }
        var pending = new Stack<AstNode>();
        pending.Push(root);
        int visited = 0;
        while (pending.TryPop(out var node))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (++visited > MaximumAnonymousFunctionBodyNodes)
            { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Anonymous function result inference exceeded its 65536-node traversal budget."); }
            if (!ReferenceEquals(node, root) && node is AstCallable or AstLambda or AstAnonymousFunction)
            { continue; }
            if (node is AstReturn returned)
            {
                result.Add(returned.Value);
                if (result.Count > MaximumAnonymousFunctionResultPaths)
                { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                    "Anonymous function result inference exceeded its 4096-return-path budget."); }
            }
            for (int index = node.Children.Length - 1; index >= 0; index--)
            { pending.Push(node.Children[index]); }
        }
        if (function.Body.TailExpression is { } tail)
        {
            result.Add(tail);
            if (result.Count > MaximumAnonymousFunctionResultPaths)
            { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                "Anonymous function result inference exceeded its 4096-return-path budget."); }
        }
        return result;
    }

    private void ValidateAnonymousFunctionCaptures(AstAnonymousFunction function, AstCallable? parentCallable)
    {
        bool nonEscaping = IsProvenNonEscaping(function);
        foreach (var capture in Sources.Lexical.GetAnonymousFunctionCaptures(function))
        {
            _cancellation.ThrowIfCancellationRequested();
            var local = capture.Local;
            var type = LocalType(local);
            bool byref = local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                || parentCallable?.Parameters.Any(parameter => parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                    && ReferenceEquals(parameter.NameOrigin.Element, local.Origin.Element)) == true;
            if (!nonEscaping && (byref || type is not null && MayContainBorrow(type)))
            {
                foreach (var reference in capture.References)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    Report("SOL2262", "A closure that is not proven nonescaping cannot capture ref/out storage or a ref-like value.", reference);
                }
            }
        }
        var receiverCaptures = Sources.Lexical.GetAnonymousFunctionReceiverCaptures(function);
        CheckRefLikeReceiverCaptures(function, receiverCaptures.Select(capture => capture.Owner),
            receiverCaptures.Select(capture => capture.References));
    }

    private void ValidateAnonymousFunctionOutParameters(AstAnonymousFunction function, State state,
        List<State> exits, LocalSymbol[] ownLocals)
    {
        foreach (var parameter in function.Parameters.Where(parameter => parameter.PassingMode == AstPassingMode.Out))
        {
            _cancellation.ThrowIfCancellationRequested();
            var local = ownLocals.FirstOrDefault(local => local.Name == parameter.Name
                && ReferenceEquals(local.Origin.Element, parameter.NameOrigin.Element));
            if (local is null) { Defer(function); continue; }
            foreach (var exit in exits.Append(state).Where(exit => exit.Reachable))
            {
                if (!exit.Assigned.Contains(local))
                { Report("SOL2242", "Out anonymous-function parameter must be assigned on every normal exit.", function); }
            }
        }
    }
}
