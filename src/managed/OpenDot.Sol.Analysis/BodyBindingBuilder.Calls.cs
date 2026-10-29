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
    private sealed record Target(AstCallable Callable, DeclarationSymbol? Symbol, ResolvedType? Receiver,
        ResolvedType? Constructed = null, ResolvedType? AccessReceiver = null);
    private sealed record Applicable(Target Target, ArgumentMapping Mapping, ImmutableArray<ResolvedType> TypeArguments,
        ImmutableArray<ResolvedType> Parameters, ResolvedType? Result, ImmutableArray<ResolvedType> Effects,
        ImmutableArray<int> Ranks, bool Generic);
    private const int MaximumLambdaInferencePasses = 128;

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
            if (function.Type is { IsNullable: true } nullable
                && nullable.Arguments[0].Kind == ResolvedTypeKind.Function)
            {
                Report("SOL2243", "Nullable function value must be checked before invocation.", call);
                return new(nullable.Arguments[0].Arguments[^1]);
            }
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
        var candidates = targets.Select(t => ApplicableCall(call, t, explicitTypes, values, expected, state, context))
            .OfType<Applicable>().ToArray();
        var best = candidates.Where(c => !candidates.Any(other => !ReferenceEquals(c, other) && Better(other, c))).ToArray();
        if (best.Length != 1)
        {
            if (best.Length == 0 && ReportEscapingClosureArguments(call, targets)) { return new(null); }
            bool dependsOnOpenParameter = context.Callable is { } owner && owner.TypeParameters.Any()
                && (values.OfType<Value>().Any(value => value.Type is { } type && DependsOnCallableParameter(type, owner))
                    || !explicitTypes.IsDefault && explicitTypes.Any(type => DependsOnCallableParameter(type, owner))
                    || expected is not null && DependsOnCallableParameter(expected, owner));
            if (dependsOnOpenParameter)
            {
                // Candidate ranking and constraints can change after the enclosing function closes T.
                Defer(call);
                return new(null);
            }
            Report(best.Length == 0 ? "SOL2221" : "SOL2222", best.Length == 0 ? "No applicable callable signature." : "Ambiguous callable signature.", call);
            return new(null);
        }
        var selected = best[0];
        Calls[call] = new(call, selected.Target.Callable, selected.Target.Receiver, selected.TypeArguments,
            selected.Parameters, selected.Result, selected.Mapping.Arguments, selected.Mapping.Defaults)
        { EmptyVarargParameters = selected.Mapping.EmptyVarargs, SourceReceiver = selected.Target.AccessReceiver };
        foreach (var parameter in selected.Parameters) { ValidateFunctionEffects(parameter, call); }
        if (selected.Result is not null) { ValidateFunctionEffects(selected.Result, call); }
        if (context.Callable is AstConstructor && targetExpression is AstMember { Receiver: AstAtom { Spelling: "this" } }
            && (selected.Target.Callable.Modifiers.Contains("open") || selected.Target.Callable.Modifiers.Contains("override")))
        { Report("SOL2272", "A constructor cannot perform virtual dispatch on its uninitialized receiver.", call); }
        if (selected.Target.Symbol is { } symbol)
        {
            if (!Accessible(symbol, call)) { Report("SOL2214", "The callable is inaccessible here.", call); }
            else if (symbol.Parts[0].Modifiers.Contains("protected"))
            {
                AstNode accessLocation = call.Target is AstGenericApplication { Target: { } genericTarget }
                    ? genericTarget : call.Target ?? call;
                if (!ProtectedReceiverAllowed(selected.Target.AccessReceiver ?? selected.Target.Receiver,
                        ContainingType(symbol.Parts[0]), accessLocation))
                { Report("SOL2214", "Protected access requires a receiver of the current derived type.", call); }
            }
        }
        if (selected.Target.Callable.Modifiers.Contains("private") && selected.Target.Constructed is { } constructed
            && ContainingType(call)?.Declaration != constructed.Declaration) { Report("SOL2214", "The constructor is private.", call); }
        var assignedAfterCall = new HashSet<LocalSymbol>();
        var writableArguments = new HashSet<LocalSymbol>();
        var outArguments = new HashSet<LocalSymbol>();
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
                if (local is not null)
                {
                    _localTypes[local] = type;
                    assignedAfterCall.Add(local);
                    writableArguments.Add(local);
                    outArguments.Add(local);
                }
                continue;
            }
            var value = values[mapping.ArgumentIndex];
            if (value is null || value.Literal is not null)
            { value = Expression(argument.Value as AstExpression, state, context, type, mode != AstPassingMode.Out); }
            if (mode is AstPassingMode.Ref or AstPassingMode.Out)
            {
                if (!value.Storage || !value.Writable || value.Type != type) { Report("SOL2244", "Ref/out requires writable storage of exactly the parameter type.", argument.Value); }
                if (value.Local is not null)
                {
                    writableArguments.Add(value.Local);
                    state.Narrowed.Remove(value.Local);
                    if (mode == AstPassingMode.Out)
                    {
                        assignedAfterCall.Add(value.Local);
                        outArguments.Add(value.Local);
                    }
                }
            }
            else
            {
                if (context.Callable is AstConstructor && argument.Value is AstAtom { Spelling: "this" })
                { Report("SOL2273", "An uninitialized receiver cannot escape through a call argument.", argument.Value); }
                Require(value, type, argument.Value, state);
                if (value.Type?.IsRefLike == true && selected.Target.Constructed is not null) { Defer(call); }
            }
        }
        foreach (var local in outArguments) { state.Assigned.Remove(local); }
        foreach (var local in writableArguments) { state.Narrowed.Remove(local); }
        // An escaping call may modify any mutable local captured by its target. Drop those
        // refinements before recording exceptional edges as well as normal continuation.
        foreach (var local in state.Narrowed.Keys.Where(l => l.IsMutable).ToArray()) { state.Narrowed.Remove(local); }
        InvalidateMutableCleanupNarrowings(state);
        RecordPotentialCallEdges(state, selected.Effects);
        state.Effects.UnionWith(selected.Effects);
        state.Assigned.UnionWith(assignedAfterCall);
        var result = selected.Result;
        if (targetExpression is AstMember { IsSafe: true } && result is not null) { result = Types.GetNullable(result); }
        return new(result);
    }

    private IEnumerable<Target> Targets(AstExpression? expression, AstCall call, State state, Context context, ImmutableArray<ResolvedType> explicitTypes)
    {
        IEnumerable<MemberBinding> members = [];
        ImmutableArray<DeclarationSymbol> declarations = [];
        ResolvedType? accessReceiver = null;
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
                accessReceiver = receiver;
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
                accessReceiver = receiver;
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
            if (member.Symbol.Parts[0] is AstFunction function)
            { result.Add(new(function, member.Symbol, member.DeclaringType, AccessReceiver: accessReceiver)); }
        }
        return result;
    }

    private Applicable? ApplicableCall(AstCall call, Target target, ImmutableArray<ResolvedType> explicitTypes,
        Value?[] values, ResolvedType? expected, State state, Context context)
    {
        var callable = target.Callable;
        var defaults = Sources.Defaults.GetDefaults(callable);
        var mapping = CallArgumentMapper.Map(call, callable, defaults, _cancellation);
        if (mapping.Error is not null || !defaults.IsComplete || callable.Parameters.Any(p => p.Type is null)) { return null; }
        var substitution = Substitution(target.Receiver);
        var parameters = Sources.Types.GetTypeParameters(callable);
        // A recursive call names the same parameter declarations but starts a new instance.
        // Keep captured caller parameters, while allowing the callee's own vectors to replace
        // its method/owner parameters exactly once from the original signature graph.
        var calleeParameters = parameters.ToHashSet();
        if (target.Constructed?.Declaration is { } constructedOwner)
        { calleeParameters.UnionWith(Sources.Types.GetTypeParameters(constructedOwner.Parts[0])); }
        foreach (var pair in _bodySubstitution)
        {
            if (!calleeParameters.Contains(pair.Key)) { substitution.TryAdd(pair.Key, pair.Value); }
        }
        if (target.Constructed is null && !explicitTypes.IsDefault)
        {
            if (parameters.Length != explicitTypes.Length) { return null; }
            for (int index = 0; index < parameters.Length; index++) { substitution[parameters[index]] = explicitTypes[index]; }
        }
        var inferParameters = target.Constructed is not null && explicitTypes.IsDefault
            ? Sources.Types.GetTypeParameters(target.Constructed.Declaration!.Parts[0]) : parameters;
        foreach (var parameter in inferParameters)
        {
            if (substitution.TryGetValue(parameter, out var current) && ReferenceEquals(current, parameter))
            {
                substitution.Remove(parameter);
            }
        }
        inferParameters = inferParameters.Where(parameter => !substitution.ContainsKey(parameter)).ToImmutableArray();
        var inference = ImmutableArray.CreateBuilder<GenericInferenceConstraint>();
        var arrayLiteralNumericEvidence = ImmutableArray.CreateBuilder<GenericInferenceConstraint>();
        foreach (var entry in mapping.Arguments)
        {
            var actual = values[entry.ArgumentIndex]?.Type;
            if (actual is null) { continue; }
            if (entry.IsArrayArgument || entry.IsSpread)
            {
                if (actual.Kind != ResolvedTypeKind.Array) { return null; }
                actual = actual.Arguments[0];
            }
            var relation = entry.IsArrayArgument || entry.IsSpread
                || callable.Parameters[entry.ParameterIndex].PassingMode is AstPassingMode.Ref or AstPassingMode.Out
                ? GenericInferenceRelation.Exact : GenericInferenceRelation.Input;
            inference.Add(new(Sources.Types.GetType(callable.Parameters[entry.ParameterIndex].Type!), actual, relation,
                "argument " + entry.ArgumentIndex));
        }
        if (!inferParameters.IsEmpty)
        {
            foreach (var entry in mapping.Arguments)
            {
                if (call.Arguments[entry.ArgumentIndex].Value is not AstSequence
                    { SyntaxKind: NodeKind.ArrayExpression } array) { continue; }
                var formal = Sources.Types.GetType(callable.Parameters[entry.ParameterIndex].Type!);
                bool exactArray = entry.IsArrayArgument || entry.IsSpread;
                if (exactArray) { formal = Types.GetArray(formal); }
                if (!AddArrayLiteralEvidence(inference, arrayLiteralNumericEvidence, formal, array, exactArray, state, context,
                        "argument " + entry.ArgumentIndex, out _)) { return null; }
            }
        }
        if (expected is not null && !inferParameters.IsEmpty)
        {
            var returnTemplate = callable.ReturnType is { } declaredReturn
                ? Sources.Types.GetType(declaredReturn)
                : callable is AstFunction { Body.Kind: AstBodyKind.Expression } expressionFunction
                    ? target.Receiver is { Kind: ResolvedTypeKind.Nominal, Declaration.Arity: > 0 } templateOwner
                        && !ContainsTypeParameter(templateOwner) && ClosedOwnerReturnTemplate is not null
                            ? ClosedOwnerReturnTemplate(expressionFunction, templateOwner, _cancellation)
                            : InferredFunctionReturnTemplate(expressionFunction, state)
                    : null;
            if (returnTemplate is not null)
            {
                // Non-null results widen to the payload of a nullable expected type; the final
                // selected result is still checked against the complete nullable target.
                var expectedTemplate = expected.IsNullable && !returnTemplate.IsNullable
                    ? expected.Arguments[0] : expected;
                inference.Add(new(returnTemplate, expectedTemplate, GenericInferenceRelation.Expected, "expected result"));
            }
        }
        if (!inferParameters.IsEmpty)
        {
            var genericInference = GenericInference.Solve(inferParameters, substitution, inference);
            if (genericInference.Status == GenericInferenceStatus.ResourceLimit)
            {
                Report("SOL2290", genericInference.Reason ?? "Generic inference exceeded its resource budget.", call);
                Defer(call);
                return null;
            }
            if (genericInference.Status is GenericInferenceStatus.ConflictingEvidence
                or GenericInferenceStatus.IncompatibleShape)
            { return null; }
            substitution.Clear();
            foreach (var pair in genericInference.Substitution) { substitution.Add(pair.Key, pair.Value); }

            var unresolved = genericInference.UnresolvedParameters;
            for (int pass = 0; !unresolved.IsEmpty && pass < MaximumLambdaInferencePasses; pass++)
            {
                var before = substitution.ToArray();
                foreach (var entry in mapping.Arguments)
                {
                    var argument = call.Arguments[entry.ArgumentIndex];
                    var lambda = argument.Value as AstLambda
                        ?? (argument.Value as AstLabelledExpression)?.Lambda;
                    if (lambda is null) { continue; }
                    var formal = Types.Substitute(Sources.Types.GetType(callable.Parameters[entry.ParameterIndex].Type!),
                        substitution, _cancellation);
                    if (entry.IsSpread || entry.IsArrayArgument) { continue; }
                    if (formal.Kind != ResolvedTypeKind.Function || !LambdaShape(lambda, formal)) { return null; }
                    var actual = AnalyzeLambdaCandidate(lambda, state, context, formal, unresolved,
                        out bool invalidLambda);
                    if (invalidLambda) { return null; }
                    if (actual.Type?.Kind != ResolvedTypeKind.Function)
                    {
                        Defer(call);
                        return null;
                    }
                    AddLambdaEvidence(inference, lambda,
                        formal, actual.Type, unresolved, entry.ArgumentIndex);
                }

                genericInference = GenericInference.Solve(unresolved, substitution, inference);
                if (genericInference.Status == GenericInferenceStatus.ResourceLimit)
                {
                    Report("SOL2290", genericInference.Reason ?? "Generic lambda inference exceeded its resource budget.", call);
                    Defer(call);
                    return null;
                }
                if (genericInference.Status is GenericInferenceStatus.ConflictingEvidence
                    or GenericInferenceStatus.IncompatibleShape)
                { return null; }
                substitution.Clear();
                foreach (var pair in genericInference.Substitution) { substitution.Add(pair.Key, pair.Value); }
                var nextUnresolved = genericInference.UnresolvedParameters;
                if (nextUnresolved.IsEmpty)
                {
                    unresolved = nextUnresolved;
                    break;
                }
                bool substitutionChanged = before.Length != substitution.Count
                    || before.Any(pair => !substitution.TryGetValue(pair.Key, out var current) || current != pair.Value);
                bool unresolvedChanged = !unresolved.SequenceEqual(nextUnresolved);
                unresolved = nextUnresolved;
                if (!substitutionChanged && !unresolvedChanged) { break; }
                if (pass == MaximumLambdaInferencePasses - 1)
                {
                    Report("SOL2290", "Generic lambda inference exceeded the 128-pass fixed-point budget.", call);
                    Defer(call);
                    return null;
                }
            }
            if (!unresolved.IsEmpty && arrayLiteralNumericEvidence.Count != 0)
            {
                // Numeric literal defaults are fallback evidence. Give other arguments, expected
                // results, and candidate-local lambda facts the chance to select T first so each
                // array literal can be checked for representability under the completed element type.
                var fallback = arrayLiteralNumericEvidence.Where(constraint =>
                    ContainsInferenceParameter(constraint.Template, unresolved)).ToArray();
                if (fallback.Length != 0)
                {
                    inference.AddRange(fallback);
                    genericInference = GenericInference.Solve(unresolved, substitution, inference);
                    if (genericInference.Status == GenericInferenceStatus.ResourceLimit)
                    {
                        Report("SOL2290", genericInference.Reason ?? "Generic array inference exceeded its resource budget.", call);
                        Defer(call);
                        return null;
                    }
                    if (genericInference.Status is GenericInferenceStatus.ConflictingEvidence
                        or GenericInferenceStatus.IncompatibleShape)
                    { return null; }
                    substitution.Clear();
                    foreach (var pair in genericInference.Substitution) { substitution.Add(pair.Key, pair.Value); }
                    unresolved = genericInference.UnresolvedParameters;
                }
            }
            if (!unresolved.IsEmpty) { Defer(call); return null; }
        }

        if (!ConstraintsSatisfied(callable, substitution, call)) { return null; }
        if (target.Constructed?.Declaration is { } constructedDefinition
            && !ConstraintsSatisfied(constructedDefinition.Parts[0], substitution, call)) { return null; }
        var types = callable.Parameters.Select(p => Types.Substitute(Sources.Types.GetType(p.Type!), substitution, _cancellation)).ToImmutableArray();
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
                var candidateLambda = AnalyzeLambdaCandidate(actualLambda, state, context, type, [], out bool invalidLambda);
                if (invalidLambda) { return null; }
                if (candidateLambda.Type?.Kind != ResolvedTypeKind.Function)
                { Defer(call); return null; }
                ranks.Add(0); continue;
            }
            if (argument.Value is AstSequence { SyntaxKind: NodeKind.ArrayExpression })
            {
                if (type.Kind is not (ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)) { return null; }
                _ = Speculate(state, () => Expression(argument.Value as AstExpression, state.Clone(), context, type),
                    out bool invalidArray);
                if (invalidArray) { return null; }
                ranks.Add(0);
                continue;
            }
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
            : callable.ReturnType is not null ? Types.Substitute(Sources.Types.GetType(callable.ReturnType), substitution, _cancellation)
                : CheckBody(callable).ReturnType is { } inferred ? Types.Substitute(inferred, substitution, _cancellation) : null;
        var effects = callable.ThrownTypes.Select(t => Types.Substitute(Sources.Types.GetType(t), substitution, _cancellation)).ToImmutableArray();
        var typeArguments = parameters.Select(parameter => substitution.GetValueOrDefault(parameter, parameter)).ToImmutableArray();
        if (callable is AstFunction { ReturnType: null } inferredMember
            && target.Receiver is { Kind: ResolvedTypeKind.Nominal, Declaration.Arity: > 0 } memberOwner
            && !ContainsTypeParameter(memberOwner) && typeArguments.All(type => !ContainsTypeParameter(type))
            && ClosedMemberReturn is not null)
        {
            result = ClosedMemberReturn(inferredMember, memberOwner, typeArguments, _cancellation);
            if (result is null) { Defer(call); }
        }
        return new(target, mapping, typeArguments, types, result, effects, ranks.ToImmutable(), inferParameters.Length != 0);
    }

    private void AddLambdaEvidence(ImmutableArray<GenericInferenceConstraint>.Builder constraints,
        AstLambda lambda, ResolvedType formal, ResolvedType actual,
        ImmutableArray<ResolvedType> inferenceParameters, int argumentIndex)
    {
        var parameters = lambda.Parameters.ToArray();
        for (int index = 0; index < parameters.Length; index++)
        {
            var written = parameters[index].Children.OfType<AstType>().FirstOrDefault();
            if (written is null || index >= formal.ParameterModes.Length) { continue; }
            var relation = formal.ParameterModes[index] is AstPassingMode.Ref or AstPassingMode.Out
                ? GenericInferenceRelation.Exact : GenericInferenceRelation.Expected;
            constraints.Add(new(formal.Arguments[index], Written(written), relation,
                "argument " + argumentIndex + " lambda parameter " + index));
        }

        var actualResult = actual.Arguments[^1];
        if (!ContainsInferenceParameter(actualResult, inferenceParameters))
        {
            constraints.Add(new(formal.Arguments[^1], actualResult, GenericInferenceRelation.Input,
                "argument " + argumentIndex + " lambda result"));
        }

        if (!formal.Effects.Any(effect => ContainsInferenceParameter(effect, inferenceParameters))) { return; }
        var marker = Types.GetPrimitive("int");
        var formalEffectSet = Types.GetFunction([], marker, [], formal.Effects);
        var actualEffectSet = Types.GetFunction([], marker, [], actual.Effects);
        constraints.Add(new(formalEffectSet, actualEffectSet, GenericInferenceRelation.Input,
            "argument " + argumentIndex + " lambda checked effects"));
    }

    private ResolvedType? InferredFunctionReturnTemplate(AstFunction function, State state)
    {
        var summary = Speculate(state, () => CheckBody(function), out bool reportedDiagnostic);
        if (reportedDiagnostic || summary.Status != BodyBindingStatus.Bound) { return null; }
        return summary.ReturnType;
    }

    private bool AddArrayLiteralEvidence(ImmutableArray<GenericInferenceConstraint>.Builder constraints,
        ImmutableArray<GenericInferenceConstraint>.Builder numericLiteralEvidence, ResolvedType template,
        AstSequence literal, bool exactArray, State state, Context context, string origin, out string? error)
    {
        error = null;
        if (template.Kind is not (ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray))
        {
            var commonElement = ArrayLiteralCommonType(literal, state, context);
            if (commonElement is null)
            {
                if (literal.Elements.Any()) { error = "Array literal has no unique standalone element type."; return false; }
                return true;
            }
            constraints.Add(new(template, Types.GetArray(commonElement), GenericInferenceRelation.Input, origin));
            return true;
        }
        if (template.Kind == ResolvedTypeKind.FixedArray
            && (!int.TryParse(template.Name, out int length) || length != literal.Elements.Count()))
        { error = "Fixed array length differs from the literal element count."; return false; }
        var elementTemplate = template.Arguments[0];
        if (exactArray)
        {
            var common = ArrayLiteralCommonType(literal, state, context);
            if (common is null)
            {
                if (literal.Elements.Any()) { error = "Named or spread array literal has no unique element type."; return false; }
                return true;
            }
            constraints.Add(new(elementTemplate, common, GenericInferenceRelation.Exact, origin + " array element type"));
            return true;
        }
        int index = 0;
        foreach (var element in literal.Elements)
        {
            _cancellation.ThrowIfCancellationRequested();
            string elementOrigin = origin + " element " + index++;
            if (element is AstSequence { SyntaxKind: NodeKind.ArrayExpression } nested
                && elementTemplate.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)
            {
                if (!AddArrayLiteralEvidence(constraints, numericLiteralEvidence, elementTemplate, nested, false, state, context,
                        elementOrigin, out error)) { return false; }
                continue;
            }
            if (element is not AstExpression expression)
            { error = "Array literal contains a syntax node without an expression type."; return false; }
            var value = AnalyzeExpressionCandidate(expression, state, context, out bool invalid);
            if (invalid || value.Type is null)
            { error = "Array literal element typing is incomplete."; return false; }
            var evidence = new GenericInferenceConstraint(elementTemplate, value.Type,
                GenericInferenceRelation.Input, elementOrigin);
            if (value.Literal is not null && value.Type.Kind == ResolvedTypeKind.Primitive)
            { numericLiteralEvidence.Add(evidence); }
            else { constraints.Add(evidence); }
        }
        return true;
    }

    private bool ConstraintsSatisfied(AstDeclaration definition,
        IReadOnlyDictionary<ResolvedType, ResolvedType> substitution, AstCall call)
    {
        var parameters = Sources.Types.GetTypeParameters(definition);
        if (parameters.IsEmpty) { return true; }
        var arguments = parameters.Select(parameter => substitution.GetValueOrDefault(parameter, parameter)).ToArray();
        var parameterSet = parameters.ToHashSet();
        var enclosingSubstitution = substitution.Where(pair => !parameterSet.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var check = Types.CheckConstraintsWithSubstitution(definition, arguments, enclosingSubstitution, _cancellation);
        if (check.Status == ConstraintStatus.Unsatisfied) { return false; }
        if (check.Status == ConstraintStatus.Indeterminate)
        {
            Defer(call);
            return false;
        }
        return true;
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
        ValidateFunctionEffects(function, call);
        if (call.Arguments.Length != function.ParameterModes.Length || call.Arguments.Any(a => a.Name is not null || a.IsTrailingLambda))
        { Report("SOL2221", "Function-value calls require positional arguments matching the signature arity.", call); return new(null); }
        var assignedAfterCall = new HashSet<LocalSymbol>();
        var writableArguments = new HashSet<LocalSymbol>();
        var outArguments = new HashSet<LocalSymbol>();
        for (int index = 0; index < call.Arguments.Length; index++)
        {
            var argument = call.Arguments[index];
            var mode = function.ParameterModes[index];
            var expectedMode = mode == AstPassingMode.Ref ? AstArgumentMode.Ref : mode == AstPassingMode.Out ? AstArgumentMode.Out : AstArgumentMode.Value;
            if (argument.Mode == AstArgumentMode.OutVariable)
            {
                if (mode != AstPassingMode.Out)
                {
                    Report("SOL2244", "An out variable requires an out function-value parameter.", argument.Value);
                    continue;
                }
                var local = Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols).FirstOrDefault(symbol =>
                    argument.Value.Origin.Span.Start <= symbol.Origin.Span.Start
                    && symbol.Origin.Span.End <= argument.Value.Origin.Span.End
                    && ReferenceEquals(symbol.Origin.Source, argument.Origin.Source));
                if (local is null)
                {
                    Report("SOL2244", "The out variable does not resolve to a local storage location.", argument.Value);
                    continue;
                }
                _localTypes[local] = function.Arguments[index];
                writableArguments.Add(local);
                assignedAfterCall.Add(local);
                outArguments.Add(local);
                continue;
            }
            if (argument.Mode != expectedMode) { Report("SOL2244", "Function-value argument passing mode mismatch.", argument.Value); }
            var value = Expression(argument.Value as AstExpression, state, context, function.Arguments[index], mode != AstPassingMode.Out);
            if (mode == AstPassingMode.Value) { Require(value, function.Arguments[index], argument.Value, state); }
            else
            {
                var storageType = value.Local is null ? value.Type : LocalType(value.Local);
                if (!value.Storage || !value.Writable || storageType != function.Arguments[index])
                { Report("SOL2244", "Function-value ref/out argument requires exact writable storage.", argument.Value); }
                if (value.Local is not null)
                {
                    writableArguments.Add(value.Local);
                    state.Narrowed.Remove(value.Local);
                    if (mode == AstPassingMode.Out)
                    {
                        assignedAfterCall.Add(value.Local);
                        outArguments.Add(value.Local);
                    }
                }
            }
        }
        foreach (var local in outArguments) { state.Assigned.Remove(local); }
        foreach (var local in writableArguments) { state.Narrowed.Remove(local); }
        // Function values may be closures even while native closure lowering is unavailable.
        // Keep source flow conservative on both normal and exceptional call edges.
        foreach (var local in state.Narrowed.Keys.Where(local => local.IsMutable).ToArray()) { state.Narrowed.Remove(local); }
        InvalidateMutableCleanupNarrowings(state);
        RecordPotentialCallEdges(state, function.Effects);
        state.Effects.UnionWith(function.Effects);
        state.Assigned.UnionWith(assignedAfterCall);
        return new(function.Arguments[^1]);
    }

    private void ValidateFunctionEffects(ResolvedType root, AstNode location)
    {
        var pending = new Stack<(ResolvedType Type, int Depth)>();
        var visited = new HashSet<int>();
        pending.Push((root, 0));
        while (pending.TryPop(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (current.Depth > 128) { Defer(location); continue; }
            if (!visited.Add(current.Type.Id)) { continue; }
            if (current.Type.Kind == ResolvedTypeKind.Function)
            {
                foreach (var effect in current.Type.Effects) { CheckThrowable(effect, location); }
            }
            foreach (var nested in current.Type.Arguments.Concat(current.Type.Effects))
            { pending.Push((nested, current.Depth + 1)); }
        }
    }

    private void RecordPotentialCallEdges(State state, IEnumerable<ResolvedType> effects)
    {
        var possibleEffects = effects;
        if (Core("RuntimeException") is { } runtime) { possibleEffects = possibleEffects.Append(runtime); }
        foreach (var effect in possibleEffects.Distinct()) { state.RecordExceptionalEdge(effect); }
    }
}
