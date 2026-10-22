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
    private sealed record Target(AstCallable Callable, DeclarationSymbol? Symbol, ResolvedType? Receiver, ResolvedType? Constructed = null);
    private sealed record Applicable(Target Target, ArgumentMapping Mapping, ImmutableArray<ResolvedType> Parameters,
        ResolvedType? Result, ImmutableArray<ResolvedType> Effects, ImmutableArray<int> Ranks, bool Generic);

    private Value SafeCall(AstCall call, State state, Context context, ResolvedType? expected)
    {
        var target = call.Target is AstGenericApplication generic ? generic.Target : call.Target;
        if (target is not AstMember { IsSafe: true }) { return Call(call, state, context, expected); }
        var skipped = state.Clone();
        var result = Call(call, state, context, expected);
        state.Join(skipped, state.Clone());
        return result;
    }

    private Value Call(AstCall call, State state, Context context, ResolvedType? expected)
    {
        var generic = call.Target as AstGenericApplication;
        var targetExpression = generic?.Target ?? call.Target;
        var explicitTypes = generic?.TypeArguments.Select(Written).ToImmutableArray() ?? default;
        var targets = Targets(targetExpression, call, state, context, explicitTypes).ToArray();
        if (targets.Length == 0)
        {
            if (ImplicitConstruction(targetExpression, call, state) is { } implicitValue) { return implicitValue; }
            var function = Expression(targetExpression, state, context);
            if (function.Type?.Kind == ResolvedTypeKind.Function) { return InvokeValue(call, function.Type, state, context); }
            if (targetExpression is AstAtom { Spelling: "unreachable" }) { return new(Types.GetBottom()); }
            foreach (var argument in call.Arguments.Where(a => a.Value is AstExpression)) { Expression((AstExpression)argument.Value, state, context); }
            Defer(call); return new(null);
        }
        var values = new Value?[call.Arguments.Length];
        for (int index = 0; index < values.Length; index++)
        {
            var argument = call.Arguments[index];
            if (argument.Mode == AstArgumentMode.OutVariable || argument.Value is AstLambda or AstLabelledExpression
                || argument.Value is AstSequence { SyntaxKind: NodeKind.ArrayExpression }) { continue; }
            if (argument.Value is AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal
                && literal.Origin.Element.Tokens.FirstOrDefault()?.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken)
            { values[index] = new(Types.GetPrimitive(literal.Origin.Element.Tokens.First().Kind == SyntaxKind.IntegerLiteralToken ? "int" : "double"), Literal: literal.Spelling); continue; }
            values[index] = argument.Value is AstExpression expression
                ? Expression(expression, state, context, read: argument.Mode != AstArgumentMode.Out) : new(null);
            if (argument.Mode == AstArgumentMode.Ref && values[index]?.Local is { } local)
            { values[index] = values[index]! with { Type = LocalType(local) }; }
        }
        var candidates = targets.Select(t => ApplicableCall(call, t, explicitTypes, values, expected)).OfType<Applicable>().ToArray();
        var best = candidates.Where(c => !candidates.Any(other => !ReferenceEquals(c, other) && Better(other, c))).ToArray();
        if (best.Length != 1)
        {
            Report(best.Length == 0 ? "SOL2221" : "SOL2222", best.Length == 0 ? "No applicable callable signature." : "Ambiguous callable signature.", call);
            return new(null);
        }
        var selected = best[0];
        Calls[call] = new(call, selected.Target.Callable, selected.Target.Receiver, selected.Parameters,
            selected.Result, selected.Mapping.Arguments, selected.Mapping.Defaults);
        if (context.Callable is AstConstructor && targetExpression is AstMember { Receiver: AstAtom { Spelling: "this" } }
            && (selected.Target.Callable.Modifiers.Contains("open") || selected.Target.Callable.Modifiers.Contains("override")))
        { Report("SOL2272", "A constructor cannot perform virtual dispatch on its uninitialized receiver.", call); }
        if (selected.Target.Symbol is { } symbol && !Accessible(symbol, call)) { Report("SOL2214", "The callable is inaccessible here.", call); }
        if (selected.Target.Callable.Modifiers.Contains("private") && selected.Target.Constructed is { } constructed
            && ContainingType(call)?.Declaration != constructed.Declaration) { Report("SOL2214", "The constructor is private.", call); }
        foreach (var mapping in selected.Mapping.Arguments)
        {
            var argument = call.Arguments[mapping.ArgumentIndex];
            var type = selected.Parameters[mapping.ParameterIndex];
            var mode = selected.Target.Callable.Parameters[mapping.ParameterIndex].PassingMode;
            if (mapping.IsSpread || mapping.IsArrayArgument) { type = Types.GetArray(type); }
            if (argument.Mode == AstArgumentMode.OutVariable)
            {
                var local = Sources.Lexical.Scopes.SelectMany(s => s.Symbols).FirstOrDefault(l => argument.Value.Origin.Span.Start <= l.Origin.Span.Start
                    && l.Origin.Span.End <= argument.Value.Origin.Span.End && ReferenceEquals(l.Origin.Source, argument.Origin.Source));
                if (local is not null) { _localTypes[local] = type; state.Assigned.Add(local); }
                continue;
            }
            var value = values[mapping.ArgumentIndex];
            if (value is null || value.Literal is not null)
            { value = Expression(argument.Value as AstExpression, state, context, type, mode != AstPassingMode.Out); }
            if (mode is AstPassingMode.Ref or AstPassingMode.Out)
            {
                if (!value.Storage || !value.Writable || value.Type != type) { Report("SOL2244", "Ref/out requires writable storage of exactly the parameter type.", argument.Value); }
                if (value.Local is not null) { state.Assigned.Add(value.Local); state.Narrowed.Remove(value.Local); }
            }
            else
            {
                if (context.Callable is AstConstructor && argument.Value is AstAtom { Spelling: "this" })
                { Report("SOL2273", "An uninitialized receiver cannot escape through a call argument.", argument.Value); }
                Require(value, type, argument.Value, state);
                if (value.Type?.IsRefLike == true && selected.Target.Constructed is not null) { Defer(call); }
            }
        }
        state.Effects.UnionWith(selected.Effects);
        // Calls can mutate captured var cells. Narrowing of mutable locals never survives arbitrary code.
        foreach (var local in state.Narrowed.Keys.Where(l => l.IsMutable).ToArray()) { state.Narrowed.Remove(local); }
        var result = selected.Result;
        if (targetExpression is AstMember { IsSafe: true } && result is not null) { result = Types.GetNullable(result); }
        return new(result);
    }

    private IEnumerable<Target> Targets(AstExpression? expression, AstCall call, State state, Context context, ImmutableArray<ResolvedType> explicitTypes)
    {
        IEnumerable<MemberBinding> members = [];
        ImmutableArray<DeclarationSymbol> declarations = [];
        if (expression is AstAtom atom)
        {
            var lookup = Sources.Lexical.Lookup(atom, atom.Spelling);
            if (lookup.IsLocal)
            {
                return lookup.Locals.Where(l => l.Kind == LocalSymbolKind.Function)
                    .SelectMany(l => Files.Keys.OfType<AstFunction>().Where(f => ReferenceEquals(f.Origin.Element, l.Origin.Element)))
                    .Select(f => new Target(f, null, null));
            }
            declarations = lookup.Declarations.Candidates;
            if (ContainingType(atom) is { } receiver)
            {
                members = FindMembers(receiver, atom.Spelling, atom);
                if (members.Any()) { declarations = []; }
            }
        }
        else if (expression is AstMember member && member.Name is not null)
        {
            if (Qualified(member) is { } qualified) { declarations = qualified; }
            else
            {
                var value = Expression(member.Receiver, state, context);
                if (value.Type is null) { return []; }
                var receiver = value.Type;
                if (receiver.IsNullable)
                {
                    if (!member.IsSafe) { Report("SOL2243", "Nullable call receiver requires safe access or a check.", member); }
                    receiver = receiver.Arguments[0];
                }
                members = ReceiverMembers(receiver, member.Name, call, value.IsType);
            }
        }
        var result = new List<Target>();
        foreach (var symbol in declarations)
        {
            if (symbol.Parts[0] is AstFunction function) { result.Add(new(function, symbol, null)); }
            else if (symbol.Kind == SymbolKind.Type)
            {
                var arguments = explicitTypes.IsDefault ? symbol.Arity == 0 ? ImmutableArray<ResolvedType>.Empty
                    : Sources.Types.GetTypeParameters(symbol.Parts[0]) : explicitTypes;
                if (arguments.Length != symbol.Arity) { continue; }
                var type = Types.Nominal(symbol, arguments);
                if (symbol.Parts[0].Modifiers.Contains("abstract") || symbol.Parts[0].SyntaxKind is NodeKind.InterfaceDeclaration or NodeKind.ObjectDeclaration)
                { Report("SOL2223", "This type cannot be constructed.", call); continue; }
                var constructors = symbol.Parts.OfType<AstTypeDeclaration>().Select(t => t.PrimaryConstructor).OfType<AstConstructor>()
                    .Concat(symbol.Members.SelectMany(s => s.Parts).OfType<AstConstructor>()).ToArray();
                foreach (var constructor in constructors) { result.Add(new(constructor, null, type, type)); }
                if (constructors.Length == 0 && call.Arguments.IsEmpty)
                {
                    // Implicit construction needs field initialization and publication verification.
                    Defer(call);
                }
            }
        }
        foreach (var member in members)
        {
            if (member.Symbol.Parts[0] is AstFunction function) { result.Add(new(function, member.Symbol, member.DeclaringType)); }
        }
        return result;
    }

    private Applicable? ApplicableCall(AstCall call, Target target, ImmutableArray<ResolvedType> explicitTypes, Value?[] values, ResolvedType? expected)
    {
        var callable = target.Callable;
        var defaults = Sources.Defaults.GetDefaults(callable);
        var mapping = CallArgumentMapper.Map(call, callable, defaults, _cancellation);
        if (mapping.Error is not null || !defaults.IsComplete || callable.Parameters.Any(p => p.Type is null)) { return null; }
        var substitution = Substitution(target.Receiver);
        var parameters = Sources.Types.GetTypeParameters(callable);
        if (target.Constructed is null && !explicitTypes.IsDefault)
        {
            if (parameters.Length != explicitTypes.Length) { return null; }
            for (int index = 0; index < parameters.Length; index++) { substitution[parameters[index]] = explicitTypes[index]; }
        }
        var inferParameters = target.Constructed is not null && explicitTypes.IsDefault
            ? Sources.Types.GetTypeParameters(target.Constructed.Declaration!.Parts[0]) : parameters;
        foreach (var parameter in inferParameters) { if (substitution.GetValueOrDefault(parameter) == parameter) { substitution.Remove(parameter); } }
        foreach (var entry in mapping.Arguments)
        {
            var actual = values[entry.ArgumentIndex]?.Type;
            if (actual is null) { continue; }
            if (entry.IsArrayArgument || entry.IsSpread)
            {
                if (actual.Kind != ResolvedTypeKind.Array) { return null; }
                actual = actual.Arguments[0];
            }
            if (!InferBody(Written(callable.Parameters[entry.ParameterIndex].Type!), actual, inferParameters, substitution)) { return null; }
        }
        if (expected is not null && callable.ReturnType is not null)
        { InferBody(Written(callable.ReturnType), expected, inferParameters, substitution); }
        if (inferParameters.Any(p => !substitution.ContainsKey(p))) { Defer(call); return null; }
        var constraintOwner = target.Constructed?.Declaration?.Parts[0] ?? callable;
        var constraintParameters = Sources.Types.GetTypeParameters(constraintOwner);
        if (Types.CheckConstraints(constraintOwner, constraintParameters.Select(p => substitution.GetValueOrDefault(p, p)).ToArray(), _cancellation).Status != ConstraintStatus.Satisfied) { return null; }
        var types = callable.Parameters.Select(p => Types.Substitute(Written(p.Type!), substitution, _cancellation)).ToImmutableArray();
        var ranks = ImmutableArray.CreateBuilder<int>();
        foreach (var entry in mapping.Arguments)
        {
            var argument = call.Arguments[entry.ArgumentIndex];
            var type = types[entry.ParameterIndex];
            var mode = callable.Parameters[entry.ParameterIndex].PassingMode;
            if (entry.IsSpread || entry.IsArrayArgument) { type = Types.GetArray(type); }
            var value = values[entry.ArgumentIndex];
            if (argument.Mode == AstArgumentMode.OutVariable) { ranks.Add(0); continue; }
            if (argument.Value is AstLambda || argument.Value is AstLabelledExpression)
            {
                var actualLambda = argument.Value as AstLambda ?? ((AstLabelledExpression)argument.Value).Lambda;
                if (type.Kind != ResolvedTypeKind.Function || actualLambda is null || !LambdaShape(actualLambda, type)) { return null; }
                ranks.Add(0); continue;
            }
            if (argument.Value is AstSequence { SyntaxKind: NodeKind.ArrayExpression })
            { if (type.Kind is not (ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)) { return null; } ranks.Add(0); continue; }
            if (value?.Literal is not null)
            {
                var literalType = type.IsNullable ? type.Arguments[0] : type;
                if (literalType.Kind != ResolvedTypeKind.Primitive)
                {
                    if (value.Type is null || !CallBindingBuilder.NumericLiteralFits(value.Literal, value.Type)) { return null; }
                    int conversion = ConversionRank(value.Type, type);
                    if (conversion < 0) { return null; }
                    ranks.Add(conversion); continue;
                }
                if (!CallBindingBuilder.NumericLiteralFits(value.Literal, literalType)) { return null; }
                string defaultType = value.Literal.Contains('.', StringComparison.Ordinal) || value.Literal.Contains('e', StringComparison.OrdinalIgnoreCase) ? "double" : "int";
                ranks.Add(type.Name == defaultType ? 0 : 1); continue;
            }
            if (value?.Type is null) { Defer(call); return null; }
            if (mode is AstPassingMode.Ref or AstPassingMode.Out)
            { if (!value.Storage || !value.Writable || value.Type != type) { return null; } ranks.Add(0); continue; }
            int rank = ConversionRank(value.Type, type);
            if (rank < 0) { return null; }
            ranks.Add(rank);
        }
        ResolvedType? result = target.Constructed is not null ? Types.Substitute(target.Constructed, substitution, _cancellation)
            : callable.ReturnType is not null ? Types.Substitute(Written(callable.ReturnType), substitution, _cancellation)
                : CheckBody(callable).ReturnType is { } inferred ? Types.Substitute(inferred, substitution, _cancellation) : null;
        var effects = callable.ThrownTypes.Select(t => Types.Substitute(Written(t), substitution, _cancellation)).ToImmutableArray();
        return new(target, mapping, types, result, effects, ranks.ToImmutable(), inferParameters.Length != 0);
    }

    private bool InferBody(ResolvedType template, ResolvedType actual, ImmutableArray<ResolvedType> parameters,
        Dictionary<ResolvedType, ResolvedType> substitution, int depth = 0)
    {
        if (depth > 128) { return false; }
        if (parameters.Contains(template))
        {
            if (substitution.TryGetValue(template, out var previous)) { return previous == actual; }
            substitution.Add(template, actual); return true;
        }
        if (template.IsNullable && !actual.IsNullable) { return InferBody(template.Arguments[0], actual, parameters, substitution, depth + 1); }
        if (template.Kind != actual.Kind || template.Declaration != actual.Declaration || template.Arguments.Length != actual.Arguments.Length) { return true; }
        return template.Arguments.Zip(actual.Arguments).All(p => InferBody(p.First, p.Second, parameters, substitution, depth + 1));
    }

    private static bool Better(Applicable left, Applicable right)
    {
        bool strict = false;
        for (int index = 0; index < left.Ranks.Length; index++)
        { if (left.Ranks[index] > right.Ranks[index]) { return false; } strict |= left.Ranks[index] < right.Ranks[index]; }
        if (strict) { return true; }
        if (left.Mapping.UsesVararg != right.Mapping.UsesVararg) { return !left.Mapping.UsesVararg; }
        if (left.Mapping.Defaults.Length != right.Mapping.Defaults.Length) { return left.Mapping.Defaults.Length < right.Mapping.Defaults.Length; }
        return !left.Generic && right.Generic;
    }

    private Value InvokeValue(AstCall call, ResolvedType function, State state, Context context)
    {
        if (call.Arguments.Length != function.ParameterModes.Length || call.Arguments.Any(a => a.Name is not null || a.IsTrailingLambda))
        { Report("SOL2221", "Function-value calls require positional arguments matching the signature arity.", call); return new(null); }
        for (int index = 0; index < call.Arguments.Length; index++)
        {
            var argument = call.Arguments[index];
            var mode = function.ParameterModes[index];
            var expectedMode = mode == AstPassingMode.Ref ? AstArgumentMode.Ref : mode == AstPassingMode.Out ? AstArgumentMode.Out : AstArgumentMode.Value;
            if (argument.Mode != expectedMode) { Report("SOL2244", "Function-value argument passing mode mismatch.", argument.Value); }
            var value = Expression(argument.Value as AstExpression, state, context, function.Arguments[index], mode != AstPassingMode.Out);
            if (mode == AstPassingMode.Value) { Require(value, function.Arguments[index], argument.Value, state); }
            else
            {
                var storageType = value.Local is null ? value.Type : LocalType(value.Local);
                if (!value.Storage || !value.Writable || storageType != function.Arguments[index])
                { Report("SOL2244", "Function-value ref/out argument requires exact writable storage.", argument.Value); }
                if (value.Local is not null) { state.Assigned.Add(value.Local); state.Narrowed.Remove(value.Local); }
            }
        }
        state.Effects.UnionWith(function.Effects);
        return new(function.Arguments[^1]);
    }
}
