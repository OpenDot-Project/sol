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
        var mappings = ImmutableArray.CreateBuilder<CallArgumentBinding>();
        var defaults = ImmutableArray.CreateBuilder<int>();
        var ranks = ImmutableArray.CreateBuilder<int>();
        var typeParameters = Sources.Types.GetTypeParameters(function);
        var substitution = new Dictionary<ResolvedType, ResolvedType>();
        var argumentTypes = new List<ArgumentValue>();
        var writtenTypes = parameters.Select(p => p.Type is null ? null : Sources.Types.GetType(p.Type)).ToArray();
        var occupied = new bool[parameters.Length];
        var named = new HashSet<string>(StringComparer.Ordinal);
        int position = 0;
        bool expands = false;
        bool sawNamed = false;

        CallCandidate Fail(string reason, bool deferred = false) => new(symbol,
            deferred ? CallCandidateStatus.Deferred : CallCandidateStatus.Rejected, reason, [], [], null, [],
            mappings.ToImmutable(), defaults.ToImmutable(), ranks.ToImmutable(), expands);

        if (!explicitTypes.IsDefault)
        {
            if (explicitTypes.Length != typeParameters.Length) { return Fail("Explicit generic arity mismatch."); }
            for (int index = 0; index < typeParameters.Length; index++) { substitution.Add(typeParameters[index], explicitTypes[index]); }
        }
        for (int argumentIndex = 0; argumentIndex < call.Arguments.Length; argumentIndex++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var argument = call.Arguments[argumentIndex];
            int parameterIndex;
            if (argument.Name is { } name)
            {
                sawNamed = true;
                if (!named.Add(name)) { return Fail("Duplicate named argument."); }
                parameterIndex = -1;
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (parameters[index].Name == name) { parameterIndex = index; break; }
                }
                if (parameterIndex < 0) { return Fail("Unknown named parameter."); }
            }
            else
            {
                if (sawNamed) { return Fail("A positional argument cannot follow a named argument."); }
                parameterIndex = position;
            }
            if (parameterIndex >= parameters.Length) { return Fail("Too many arguments."); }
            var parameter = parameters[parameterIndex];
            bool vararg = parameter.PassingMode == AstPassingMode.Vararg;
            if (occupied[parameterIndex] && (!vararg || argument.Name is not null)) { return Fail("A parameter was supplied more than once."); }
            occupied[parameterIndex] = true;
            if (argument.Name is null && !vararg) { position++; }
            bool spread = argument.Mode == AstArgumentMode.Spread;
            expands |= vararg;
            mappings.Add(new(argumentIndex, parameterIndex, vararg && !spread, spread));
            bool marker = parameter.PassingMode switch
            {
                AstPassingMode.Ref => argument.Mode == AstArgumentMode.Ref,
                AstPassingMode.Out => argument.Mode is AstArgumentMode.Out or AstArgumentMode.OutVariable,
                AstPassingMode.Vararg => argument.Mode is AstArgumentMode.Value or AstArgumentMode.Spread,
                _ => argument.Mode == AstArgumentMode.Value,
            };
            if (!marker) { return Fail("Argument passing mode mismatch."); }
        }
        for (int index = 0; index < parameters.Length; index++)
        {
            if (occupied[index]) { continue; }
            if (parameters[index].PassingMode == AstPassingMode.Vararg) { expands = true; continue; }
            if (parameters[index].DefaultValue is null) { return Fail("A required parameter is missing."); }
            defaults.Add(index);
        }
        foreach (var mapping in mappings)
        {
            var argument = call.Arguments[mapping.ArgumentIndex];
            var parameter = parameters[mapping.ParameterIndex];
            var value = argument.Mode == AstArgumentMode.OutVariable ? new ArgumentValue(null, true, null)
                : Value(argument.Value, depth);
            if (value.Type is null && argument.Mode != AstArgumentMode.OutVariable)
            {
                return Fail("Argument expression typing is not available.", true);
            }
            if (mapping.IsSpread)
            {
                if (value.Type?.Kind != ResolvedTypeKind.Array) { return Fail("A spread argument requires a managed array."); }
                value = value with { Type = value.Type.Arguments[0], Literal = null };
            }
            argumentTypes.Add(value);
            if (parameter.PassingMode is AstPassingMode.Ref or AstPassingMode.Out && !value.Writable)
            {
                return Fail("Ref/out requires writable exact local storage.");
            }
            var template = writtenTypes[mapping.ParameterIndex];
            if (template is null || template.IsError || value.Type?.IsError == true) { return Fail("The written parameter or argument type is invalid.", true); }
            if (explicitTypes.IsDefault && value.Type is not null
                && !Infer(template, value.Type, typeParameters, substitution, 0))
            {
                return Fail("Generic inference requires a common or expected type.", true);
            }
        }
        if (typeParameters.Any(p => !substitution.ContainsKey(p))) { return Fail("Generic inference needs an expected type or explicit type arguments.", true); }
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
            var literalTarget = target.IsNullable ? target.Arguments[0] : target;
            if (argument.Literal is not null)
            {
                if (literalTarget.Kind != ResolvedTypeKind.Primitive) { literalTarget = argument.Type!; }
                if (literalTarget.Name == "quad") { return Fail("Binary128 contextual literal rounding is not available.", true); }
                if (!LiteralFits(argument.Literal, literalTarget)) { return Fail("The numeric literal is outside the target domain."); }
            }
            var conversion = Types.ClassifyConversion(argument.Type!, target, _cancellation);
            if (conversion == IntrinsicConversion.None)
            {
                if (argument.Literal is not null && LiteralFits(argument.Literal, target)) { ranks.Add(1); continue; }
                return Fail(_hasUserConversions ? "User conversion analysis is not available." : "Argument type mismatch.", _hasUserConversions);
            }
            ranks.Add((int)conversion);
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
            mappings.ToImmutable(), defaults.ToImmutable(), ranks.ToImmutable(), expands);
    }

    private bool Infer(ResolvedType template, ResolvedType actual, ImmutableArray<ResolvedType> parameters,
        Dictionary<ResolvedType, ResolvedType> substitution, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (depth > 128) { return false; }
        if (parameters.Contains(template))
        {
            if (substitution.TryGetValue(template, out var previous)) { return ReferenceEquals(previous, actual); }
            substitution.Add(template, actual);
            return true;
        }
        bool containsParameter = template.Arguments.Concat(template.Effects).Any(t => ContainsParameter(t, parameters));
        if (!containsParameter) { return true; }
        if (template.IsNullable && !actual.IsNullable) { return Infer(template.Arguments[0], actual, parameters, substitution, depth + 1); }
        if (template.Kind != actual.Kind || template.Declaration != actual.Declaration || template.Name != actual.Name
            || template.Arguments.Length != actual.Arguments.Length || !template.ParameterModes.SequenceEqual(actual.ParameterModes)) { return false; }
        for (int index = 0; index < template.Arguments.Length; index++)
        {
            if (!Infer(template.Arguments[index], actual.Arguments[index], parameters, substitution, depth + 1)) { return false; }
        }
        // Effect-set inference needs constraint solving, not a positional zip of unordered effects.
        return !template.Effects.Any(t => ContainsParameter(t, parameters));
    }

    private bool ContainsParameter(ResolvedType type, ImmutableArray<ResolvedType> parameters)
    {
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<int>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current.Id)) { continue; }
            if (parameters.Contains(current)) { return true; }
            foreach (var child in current.Arguments.Concat(current.Effects)) { pending.Push(child); }
        }
        return false;
    }
}
