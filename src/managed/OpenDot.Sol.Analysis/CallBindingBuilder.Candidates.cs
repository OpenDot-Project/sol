// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class CallBindingBuilder
{
    private CallCandidate Candidate(AstCall call, DeclarationSymbol symbol, ImmutableArray<ResolvedType> explicitTypes, int depth)
    {
        var function = (AstFunction)symbol.Parts[0];
        var parameters = function.Parameters;
        var defaultContract = Sources.Defaults.GetDefaults(function);
        var mappingResult = CallArgumentMapper.Map(call, function, defaultContract, _cancellation);
        var mappings = mappingResult.Arguments;
        var defaults = mappingResult.Defaults;
        bool expands = mappingResult.UsesVararg;
        var ranks = ImmutableArray.CreateBuilder<int>();
        var typeParameters = Sources.Types.GetTypeParameters(function);
        var substitution = new Dictionary<ResolvedType, ResolvedType>();
        var argumentTypes = new List<ArgumentValue>();
        var inferenceConstraints = ImmutableArray.CreateBuilder<GenericInferenceConstraint>();
        var writtenTypes = parameters.Select(p => p.Type is null ? null : Sources.Types.GetType(p.Type)).ToArray();
        CallCandidate Fail(string reason, bool deferred = false) => new(symbol,
            deferred ? CallCandidateStatus.Deferred : CallCandidateStatus.Rejected, reason, [], [], null, [],
            mappings, defaults, ranks.ToImmutable(), expands)
        {
            EmptyVarargParameters = mappingResult.EmptyVarargs,
            DefaultArguments = defaults.Select(index => defaultContract.Parameters[index]!).ToImmutableArray(),
        };

        if (!defaultContract.IsComplete) { return Fail("Inherited default lookup is incomplete.", true); }
        if (mappingResult.Error is not null) { return Fail(mappingResult.Error); }
        if (!explicitTypes.IsDefault)
        {
            if (explicitTypes.Length != typeParameters.Length) { return Fail("Explicit generic arity mismatch."); }
            for (int index = 0; index < typeParameters.Length; index++) { substitution.Add(typeParameters[index], explicitTypes[index]); }
        }
        foreach (var mapping in mappings)
        {
            var argument = call.Arguments[mapping.ArgumentIndex];
            var parameter = parameters[mapping.ParameterIndex];
            var value = argument.Mode == AstArgumentMode.OutVariable ? new ArgumentValue(null, true, null)
                : Value(argument.Value, depth);
            if (value.Type is null && !value.IsArrayLiteral && argument.Mode != AstArgumentMode.OutVariable)
            {
                return Fail("Argument expression typing is not available.", true);
            }
            if (mapping.IsSpread || mapping.IsArrayArgument)
            {
                if (value.Type?.Kind == ResolvedTypeKind.Array)
                {
                    value = value with { Type = value.Type.Arguments[0], Literal = null };
                }
                else if (!value.IsArrayLiteral)
                {
                    return Fail("A spread or named vararg argument requires a managed array.");
                }
            }
            argumentTypes.Add(value);
            if (parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out && !value.Writable)
            {
                return Fail("Ref/out requires writable exact local storage.");
            }
            var template = writtenTypes[mapping.ParameterIndex];
            if (template is null || template.IsError || value.Type?.IsError == true) { return Fail("The written parameter or argument type is invalid.", true); }
            if (explicitTypes.IsDefault && value.Type is not null)
            {
                var relation = parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                    || mapping.IsArrayArgument || mapping.IsSpread
                    ? GenericInferenceRelation.Exact : GenericInferenceRelation.Input;
                inferenceConstraints.Add(new(template, value.Type, relation,
                    "argument " + mapping.ArgumentIndex));
            }
            else if (explicitTypes.IsDefault && value.IsArrayLiteral)
            {
                if (template.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)
                {
                    if (!AddArrayElementEvidence(inferenceConstraints, template, value,
                            "argument " + mapping.ArgumentIndex, out var arrayError))
                    { return Fail(arrayError ?? "Array literal elements do not match the generic array shape.", true); }
                }
                else if (mapping.IsArrayArgument || mapping.IsSpread)
                {
                    if (!AddArrayElementEvidence(inferenceConstraints, Types.GetArray(template), value,
                            "argument " + mapping.ArgumentIndex, out var arrayError))
                    { return Fail(arrayError ?? "The generic array literal has no unique element evidence.", true); }
                }
                else
                {
                    return Fail("The array literal has no unique common type for this generic parameter.", true);
                }
            }
        }
        if (explicitTypes.IsDefault && !typeParameters.IsEmpty)
        {
            var inferred = GenericInference.Solve(typeParameters, substitution, inferenceConstraints);
            if (inferred.Status != GenericInferenceStatus.Solved)
            {
                bool deferred = inferred.Status is GenericInferenceStatus.MissingEvidence or GenericInferenceStatus.ResourceLimit;
                return Fail(InferenceFailureReason(inferred), deferred);
            }
            substitution.Clear();
            foreach (var pair in inferred.Substitution) { substitution.Add(pair.Key, pair.Value); }
        }
        if (typeParameters.Any(p => !substitution.ContainsKey(p)))
        { return Fail("Generic inference needs an expected type or explicit type arguments.", true); }
        var typeArguments = typeParameters.Select(p => substitution[p]).ToImmutableArray();
        var constraint = Types.CheckConstraints(function, typeArguments, _cancellation);
        if (constraint.Status != ConstraintStatus.Satisfied)
        {
            return Fail(constraint.Reason ?? "Constraint analysis is indeterminate.", constraint.Status == ConstraintStatus.Indeterminate);
        }
        if (writtenTypes.Any(t => t is null)) { return Fail("A parameter type is missing.", true); }
        var specialized = writtenTypes.Select(t => Types.Substitute(t!, substitution, _cancellation)).ToImmutableArray();
        foreach (var mapping in mappings)
        {
            var argument = argumentTypes[mapping.ArgumentIndex];
            var target = specialized[mapping.ParameterIndex];
            var mode = parameters[mapping.ParameterIndex].PassingMode;
            if (argument.Type is null && call.Arguments[mapping.ArgumentIndex].Mode == AstArgumentMode.OutVariable)
            {
                ranks.Add(0);
                continue;
            }
            if (mode is AstPassingMode.Ref or AstPassingMode.Out)
            {
                if (!ReferenceEquals(argument.Type, target)) { return Fail("Ref/out storage type must match exactly."); }
                ranks.Add(0);
                continue;
            }
            if (argument.IsArrayLiteral)
            {
                var arrayTarget = mapping.IsArrayArgument || mapping.IsSpread ? Types.GetArray(target) : target;
                if ((mapping.IsArrayArgument || mapping.IsSpread)
                    && argument.Type is not null && !ReferenceEquals(argument.Type, target))
                { return Fail("A named or spread vararg array must match the invariant element type."); }
                if (!TryArrayLiteralConversion(argument, arrayTarget, out int arrayRank, out bool deferred))
                {
                    return Fail(deferred ? "Array literal element typing is incomplete."
                        : "Array literal elements do not convert to the selected array element type.", deferred);
                }
                ranks.Add(arrayRank);
                continue;
            }
            var literalTarget = target.IsNullable ? target.Arguments[0] : target;
            if (argument.Literal is not null)
            {
                if (literalTarget.Kind != ResolvedTypeKind.Primitive) { literalTarget = argument.Type!; }
                if (literalTarget.Name == "quad") { return Fail("Binary128 contextual literal rounding is not available.", true); }
                if (!LiteralFits(argument.Literal, literalTarget)) { return Fail("The numeric literal is outside the target domain."); }
            }
            var conversion = Types.ClassifyConversion(argument.Type!, target, _cancellation);
            if (mapping.IsArrayArgument && !mapping.IsSpread && conversion != IntrinsicConversion.Identity)
            {
                return Fail("A named vararg array must match the invariant element type.");
            }
            if (conversion == IntrinsicConversion.None)
            {
                if (argument.Literal is not null && LiteralFits(argument.Literal, target)) { ranks.Add(1); continue; }
                return Fail(_hasUserConversions ? "User conversion analysis is not available." : "Argument type mismatch.", _hasUserConversions);
            }
            ranks.Add((int)conversion);
        }
        foreach (int index in defaults)
        {
            var expression = defaultContract.Parameters[index]!.Expression;
            var value = Value(expression, depth);
            if (value.Type is null) { return Fail("Default expression typing is not available.", true); }
            var actual = Types.Substitute(value.Type, substitution, _cancellation);
            var target = parameters[index].PassingMode == AstPassingMode.Vararg ? Types.GetArray(specialized[index]) : specialized[index];
            var literalTarget = target.IsNullable ? target.Arguments[0] : target;
            if (value.Literal is not null)
            {
                if (literalTarget.Kind != ResolvedTypeKind.Primitive) { literalTarget = actual; }
                if (literalTarget.Name == "quad") { return Fail("Binary128 default literal rounding is not available.", true); }
                if (!LiteralFits(value.Literal, literalTarget)) { return Fail("The default literal is outside the target domain."); }
            }
            var conversion = Types.ClassifyConversion(actual, target, _cancellation);
            if (conversion == IntrinsicConversion.None
                && !(value.Literal is not null && LiteralFits(value.Literal, target)))
            {
                return Fail("Default expression type mismatch or unsupported user conversion.", _hasUserConversions);
            }
        }
        if (function.ReturnsByReference) { return Fail("Ref-return location analysis is not available.", true); }
        ResolvedType? result = function.ReturnType is { } resultType ? Types.Substitute(Sources.Types.GetType(resultType), substitution, _cancellation)
            : function.Body.Kind == AstBodyKind.Block ? Types.GetTuple([]) : null;
        if (result is null) { return Fail("Expression-body return inference is not available.", true); }
        var effects = function.ThrownTypes.Select(t => Types.Substitute(Sources.Types.GetType(t), substitution, _cancellation))
            .Distinct().OrderBy(t => t.Id).ToImmutableArray();
        if (result.IsError || specialized.Any(t => t.IsError) || effects.Any(t => t.IsError)) { return Fail("A signature type is invalid.", true); }
        foreach (var type in specialized.Concat(effects).Append(result))
        {
            var check = Types.CheckTypeConstraints(type, _cancellation);
            if (check.Status != ConstraintStatus.Satisfied)
            {
                return Fail(check.Reason ?? "A constructed signature constraint is indeterminate.", check.Status == ConstraintStatus.Indeterminate);
            }
        }
        return new(symbol, CallCandidateStatus.Applicable, null, typeArguments, specialized, result, effects,
            mappings, defaults, ranks.ToImmutable(), expands)
        {
            EmptyVarargParameters = mappingResult.EmptyVarargs,
            DefaultArguments = defaults.Select(index => defaultContract.Parameters[index]!).ToImmutableArray(),
        };
    }

    private bool AddArrayElementEvidence(
        ImmutableArray<GenericInferenceConstraint>.Builder constraints,
        ResolvedType template,
        ArgumentValue literal,
        string origin,
        out string? error)
    {
        error = null;
        if (!literal.IsArrayLiteral || template.Kind is not (ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray))
        {
            error = "Array literal evidence requires a matching array formal.";
            return false;
        }
        if (template.Kind == ResolvedTypeKind.FixedArray
            && (!int.TryParse(template.Name, out int length) || length != literal.ArrayElements.Length))
        {
            error = "Fixed array length differs from the literal element count.";
            return false;
        }

        var elementTemplate = template.Arguments[0];
        for (int index = 0; index < literal.ArrayElements.Length; index++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var element = literal.ArrayElements[index];
            string elementOrigin = origin + " element " + index;
            if (element.Type is { } actual)
            {
                constraints.Add(new(elementTemplate, actual, GenericInferenceRelation.Input, elementOrigin));
                continue;
            }
            if (element.IsArrayLiteral
                && elementTemplate.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)
            {
                if (!AddArrayElementEvidence(constraints, elementTemplate, element, elementOrigin, out error))
                { return false; }
                continue;
            }
            error = "An array element has no standalone type or nested expected-array shape.";
            return false;
        }
        return true;
    }

    private bool TryArrayLiteralConversion(ArgumentValue literal, ResolvedType target,
        out int rank, out bool deferred)
    {
        rank = 0;
        deferred = false;
        if (!literal.IsArrayLiteral || target.Kind is not (ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray))
        { return false; }
        if (target.Kind == ResolvedTypeKind.FixedArray
            && (!int.TryParse(target.Name, out int length) || length != literal.ArrayElements.Length))
        { return false; }

        var elementTarget = target.Arguments[0];
        foreach (var element in literal.ArrayElements)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (element.IsArrayLiteral
                && elementTarget.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)
            {
                if (!TryArrayLiteralConversion(element, elementTarget, out int nestedRank, out deferred))
                { return false; }
                rank = Math.Max(rank, nestedRank);
                continue;
            }
            if (element.Type is null)
            {
                deferred = true;
                return false;
            }
            if (element.Literal is { } literalText
                && !LiteralFits(literalText, elementTarget))
            { return false; }

            var conversion = Types.ClassifyConversion(element.Type, elementTarget, _cancellation);
            if (conversion == IntrinsicConversion.None)
            {
                if (element.Literal is null || !LiteralFits(element.Literal, elementTarget)) { return false; }
                rank = Math.Max(rank, 1);
            }
            else
            {
                rank = Math.Max(rank, (int)conversion);
            }
        }
        return true;
    }

    private static string InferenceFailureReason(GenericInferenceResult inference)
    {
        var reason = inference.Reason ?? "Generic inference did not produce a unique substitution.";
        var conflicting = inference.ConflictingParameters.ToHashSet();
        var relevant = inference.Evidence.Where(fact => conflicting.Count == 0
            || conflicting.Contains(fact.Parameter))
            .Select(fact => fact.Origin)
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(origin => origin, StringComparer.Ordinal)
            .ToArray();
        if (relevant.Length == 0) { return reason; }

        const int maximumOrigins = 8;
        var displayed = relevant.Take(maximumOrigins);
        var suffix = relevant.Length > maximumOrigins
            ? $" (+{relevant.Length - maximumOrigins} more)"
            : string.Empty;
        return $"{reason} Evidence came from {string.Join(", ", displayed)}{suffix}.";
    }

}
