// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private const int MaximumLambdaResultPaths = 4096;

    private static bool LambdaShape(AstLambda lambda, ResolvedType expected)
    {
        if (expected.Kind != ResolvedTypeKind.Function) { return false; }
        int count = expected.ParameterModes.Length;
        if (lambda.Form == AstLambdaForm.Named) { return lambda.Parameters.Count() == count; }
        if (lambda.Form == AstLambdaForm.Positional)
        { return !lambda.HasUnrepresentablePosition && lambda.MinimumPositionalArity <= count && expected.ParameterModes.All(m => m == AstPassingMode.Value); }
        return count == 0;
    }

    private Value Lambda(AstLambda lambda, State outer, Context parent, ResolvedType? expected,
        ImmutableArray<ResolvedType> inferenceParameters = default)
    {
        int diagnosticStart = Diagnostics.Count;
        var reportedBefore = _reported.ToHashSet();
        var expectedFunction = ExpectedLambdaFunction(expected);
        if (expectedFunction is not null) { ValidateFunctionEffects(expectedFunction, lambda); }
        if (expected is not null && (expectedFunction is null || !LambdaShape(lambda, expectedFunction)))
        { Report("SOL2220", "Lambda parameters do not match the expected function type.", lambda); return new(null); }
        if (lambda.Form == AstLambdaForm.Positional && expectedFunction is null)
        { Report("SOL2220", "Positional lambdas require an expected function type.", lambda); return new(null); }
        var state = outer.Clone(); state.Effects.Clear();
        // A lambda body runs only when its value is invoked, outside the creator's active
        // exception region. Keep its potential call edges in the lambda summary instead of
        // adding them to the enclosing body's cleanup/catch flow.
        state.ExceptionSink = null;
        var types = new List<ResolvedType>();
        var modes = new List<AstPassingMode>();
        var parameters = lambda.Parameters.ToArray();
        var ownLocals = OwnLocals(lambda).ToArray();
        for (int index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var written = parameter.Children.OfType<AstType>().FirstOrDefault();
            var type = written is null ? expectedFunction?.Arguments[index] : Written(written);
            var passing = parameter.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.PassingMode)?.Origin.Text;
            var mode = passing == "ref" ? AstPassingMode.Ref : passing == "out" ? AstPassingMode.Out : AstPassingMode.Value;
            if (type is null) { Report("SOL2220", "An uncontextualized lambda parameter needs an explicit type.", parameter); return new(null); }
            bool modeMatches = expectedFunction is null || mode == expectedFunction.ParameterModes[index];
            bool parameterTypeMatches = expectedFunction is null
                || ContainsInferenceParameter(expectedFunction.Arguments[index], inferenceParameters)
                || (mode is AstPassingMode.Ref or AstPassingMode.Out
                    ? type == expectedFunction.Arguments[index]
                    : Converts(expectedFunction.Arguments[index], type));
            if (!modeMatches || !parameterTypeMatches)
            { Report("SOL2220", "Lambda parameter type or mode differs from the expected signature.", parameter); }
            types.Add(type); modes.Add(mode);
            var name = parameter.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name)?.Origin.Text;
            foreach (var local in ownLocals.Where(l => l.Name == name
                && parameter.Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name) is { } nameNode
                && ReferenceEquals(l.Origin.Element, nameNode.Origin.Element)))
            { _localTypes[local] = type; if (mode != AstPassingMode.Out) { state.Assigned.Add(local); } }
        }
        if (lambda.Form == AstLambdaForm.Positional && expectedFunction is not null)
        { types.AddRange(expectedFunction.Arguments.Take(expectedFunction.ParameterModes.Length)); modes.AddRange(expectedFunction.ParameterModes); }
        bool nonEscaping = IsProvenNonEscaping(lambda);
        foreach (var capture in Sources.Lexical.GetLambdaCaptures(lambda))
        {
            var local = capture.Local;
            var type = LocalType(local);
            var callable = parent.Callable;
            bool byref = local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                || callable?.Parameters.Any(parameter => parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
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
        var receiverCaptures = Sources.Lexical.GetLambdaReceiverCaptures(lambda);
        CheckRefLikeReceiverCaptures(lambda, receiverCaptures.Select(capture => capture.Owner),
            receiverCaptures.Select(capture => capture.References));
        var exits = new List<State>();
        var returnPaths = new List<LambdaReturnPath>();
        bool inferResult = expectedFunction is not null
            && ContainsInferenceParameter(expectedFunction.Arguments[^1], inferenceParameters);
        bool inferEffects = expectedFunction is not null
            && expectedFunction.Effects.Any(effect => ContainsInferenceParameter(effect, inferenceParameters));
        var resultType = inferResult ? null : expectedFunction?.Arguments[^1];
        string? returnLabel = Ancestors(lambda).OfType<AstLabelledExpression>()
            .FirstOrDefault(labelled => ReferenceEquals(labelled.Lambda, lambda))?.Label;
        var context = new Context(null, resultType, lambda, expectedFunction, Exits: exits,
            LambdaReturnPaths: resultType is null ? returnPaths : null, LambdaReturnLabel: returnLabel);
        var result = Statements(lambda.Statements, state, context, true);
        if (resultType is null) { resultType = InferLambdaResultType(lambda, result, state, returnPaths, returnLabel); }
        var normalExits = exits.Append(state).Where(exit => exit.Reachable).ToArray();
        foreach (var exit in normalExits)
        {
            for (int index = 0; index < parameters.Length; index++)
            {
                if (modes[index] != AstPassingMode.Out) { continue; }
                var name = parameters[index].Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name)?.Origin.Text;
                var nameNode = parameters[index].Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name);
                if (ownLocals.Any(l => l.Name == name && nameNode is not null
                    && ReferenceEquals(l.Origin.Element, nameNode.Origin.Element) && !exit.Assigned.Contains(l)))
                { Report("SOL2242", "Out lambda parameter must be assigned on every normal exit.", lambda); }
            }
        }
        var effects = exits.Append(state).SelectMany(exit => exit.Effects).Where(IsChecked).Distinct().ToArray();
        if (expectedFunction is not null && !inferEffects
            && effects.Any(effect => !expectedFunction.Effects.Any(budget => Converts(effect, budget))))
        { Report("SOL2253", "Lambda checked effects exceed the expected function contract.", lambda); }
        if (resultType is null) { Defer(lambda); return new(null); }
        var functionType = Types.GetFunction(types, resultType, modes,
            inferEffects ? effects.ToImmutableArray() : expectedFunction?.Effects ?? effects.ToImmutableArray());
        ValidateFunctionEffects(functionType, lambda);

        var outParameters = ImmutableArray.CreateBuilder<LambdaOutParameterSummary>();
        for (int index = 0; index < parameters.Length; index++)
        {
            if (modes[index] != AstPassingMode.Out) { continue; }
            var nameNode = parameters[index].Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name);
            var local = nameNode is null ? null : ownLocals.FirstOrDefault(candidate =>
                candidate.Name == nameNode.Origin.Text && ReferenceEquals(candidate.Origin.Element, nameNode.Origin.Element));
            outParameters.Add(new(parameters[index], local is not null
                && normalExits.All(exit => exit.Assigned.Contains(local))));
        }

        var bodyNodes = new HashSet<AstNode>(lambda.Descendants(), ReferenceEqualityComparer.Instance) { lambda };
        bool hasDeferredDescendant = _deferred.Any(bodyNodes.Contains);
        bool hasNewError = Diagnostics.Skip(diagnosticStart).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            || _reported.Any(reported => !reportedBefore.Contains(reported) && bodyNodes.Contains(reported.Item1));
        LambdaBodies[lambda] = new(lambda, functionType, normalExits.Length, state.Reachable,
            outParameters.ToImmutable(), !hasDeferredDescendant && !hasNewError);
        return new(functionType);
    }

    private ResolvedType? ExpectedLambdaFunction(ResolvedType? expected)
        => expected?.Kind == ResolvedTypeKind.Function ? expected
            : expected is { IsNullable: true, Arguments.Length: 1 }
                && expected.Arguments[0].Kind == ResolvedTypeKind.Function
                ? expected.Arguments[0] : null;

    private ResolvedType? InferLambdaResultType(AstLambda lambda, Value fallthrough, State state,
        List<LambdaReturnPath> returnPaths, string? returnLabel)
    {
        var candidates = new List<ResolvedType>();
        foreach (var path in returnPaths)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!path.Reachable || returnLabel is null || path.Return.Label != returnLabel) { continue; }
            if (path.Type is null)
            { Defer((AstNode?)path.Return.Value ?? path.Return); return null; }
            if (path.Type.Kind != ResolvedTypeKind.Bottom) { candidates.Add(path.Type); }
        }

        if (state.Reachable)
        {
            if (fallthrough.Type is null) { Defer(lambda); return null; }
            if (fallthrough.Type.Kind != ResolvedTypeKind.Bottom) { candidates.Add(fallthrough.Type); }
        }

        if (candidates.Count == 0) { return Types.GetBottom(); }
        var inferred = UniqueCommonType(candidates);
        if (inferred is null)
        {
            Report("SOL2200", "Lambda return paths do not have a unique common inferred type.", lambda);
            Defer(lambda);
        }
        return inferred;
    }
}
