// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private IEnumerable<(AstFunction Function, ResolvedType Owner)> Operators(ResolvedType type, string name)
    {
        var companion = type.Declaration?.Members.FirstOrDefault(m => m.Parts[0] is AstTypeDeclaration { IsCompanion: true });
        if (companion is null) { return []; }
        return companion.Members.SelectMany(m => m.Parts).OfType<AstFunction>()
            .Where(f => f.Name == name && f.Modifiers.Contains("operator")).Select(f => (f, type));
    }

    private bool HasDeclaredOperator(string name, params ResolvedType[] operands) => operands.Distinct()
        .SelectMany(type => Operators(type, name)).Any();

    private Value? LiftNullableUnary(AstNode location, string name, Value operand, State state, Context context,
        ResolvedType? expected = null)
    {
        if (operand.Type?.IsNullable != true || !IsLiftableValueType(operand.Type!.Arguments[0])) { return null; }
        var underlying = operand with { Type = operand.Type.Arguments[0] };
        if (name is ("+" or "-") && IsNumeric(underlying.Type!)) { return new(operand.Type); }
        if (name == "~" && IsInteger(underlying.Type!)) { return new(operand.Type); }
        if (name is ("++" or "--") && IsNumeric(underlying.Type!)) { return new(operand.Type); }
        if (name == "!" && underlying.Type == Bool) { return new(Types.GetNullable(Bool)); }
        if (!HasDeclaredOperator(name, underlying.Type!)) { return null; }
        var result = OperatorCall(location, name, [underlying], state, context, expected: NullablePayload(expected),
            usesNullableLifting: true);
        return result.Type is null ? new(null) : new(Types.GetNullable(result.Type));
    }

    private Value? LiftNullableBinary(AstNode location, string name, Value left, Value right, State state,
        Context context, ResolvedType? expected = null)
    {
        if (left.Type?.IsNullable != true && right.Type?.IsNullable != true) { return null; }
        if (left.Type?.IsNullable == true && !IsLiftableValueType(left.Type.Arguments[0])
            || right.Type?.IsNullable == true && !IsLiftableValueType(right.Type.Arguments[0])) { return null; }

        var leftType = left.Type!.IsNullable ? left.Type.Arguments[0] : left.Type;
        var rightType = right.Type!.IsNullable ? right.Type.Arguments[0] : right.Type;
        var unwrappedLeft = left with { Type = leftType };
        var unwrappedRight = right with { Type = rightType };
        bool booleanResult = name is "==" or "!=" or "<" or ">" or "<=" or ">=";
        if (HasDeclaredOperator(name, leftType, rightType))
        {
            var operatorExpected = booleanResult ? Bool : NullablePayload(expected);
            var result = OperatorCall(location, name, [unwrappedLeft, unwrappedRight], state, context,
                expected: operatorExpected, usesNullableLifting: true);
            return booleanResult ? new(Bool)
                : result.Type is null ? new(null) : new(Types.GetNullable(result.Type));
        }

        if (name is ("==" or "!=") && (Converts(leftType, rightType) || Converts(rightType, leftType)))
        { return new(Bool); }
        if (name is ("<" or ">" or "<=" or ">=") && leftType == rightType && IsNumeric(leftType))
        { return new(Bool); }
        if (leftType == rightType && IsNumeric(leftType) && name is ("+" or "-" or "*" or "/" or "%"))
        { return new(Types.GetNullable(leftType)); }
        if (leftType == rightType && IsInteger(leftType) && name is ("&" or "|" or "^"))
        { return new(Types.GetNullable(leftType)); }
        if (leftType == Bool && rightType == Bool && name is ("&" or "|"))
        { return new(Types.GetNullable(Bool)); }
        if (IsInteger(leftType) && rightType == Types.GetPrimitive("int") && name is ("<<" or ">>"))
        { return new(Types.GetNullable(leftType)); }
        return null;
    }

    private static ResolvedType? NullablePayload(ResolvedType? type) =>
        type?.IsNullable == true ? type.Arguments[0] : type;

    private static bool IsLiftableValueType(ResolvedType type) => type.Kind is (ResolvedTypeKind.Primitive
        or ResolvedTypeKind.Tuple or ResolvedTypeKind.FixedArray)
        || type.Kind == ResolvedTypeKind.Nominal && type.Declaration?.Parts.FirstOrDefault()?.SyntaxKind
            is (OpenDot.Sol.Cst.NodeKind.StructDeclaration or OpenDot.Sol.Cst.NodeKind.RefStructDeclaration
                or OpenDot.Sol.Cst.NodeKind.EnumDeclaration);

    private int ConversionRank(ResolvedType source, ResolvedType target)
    {
        var conversion = Types.ClassifyConversion(source, target, _cancellation);
        if (conversion != IntrinsicConversion.None) { return (int)conversion; }
        return ConversionOperators(source, target, true).Take(2).Count() == 1 ? 3 : -1;
    }

    private IEnumerable<(AstFunction Function, ResolvedType Owner)> ConversionOperators(ResolvedType source, ResolvedType target, bool implicitOnly)
    {
        return new[] { source, target }.Distinct().SelectMany(t => Operators(t, "implicit")
            .Concat(implicitOnly ? [] : Operators(t, "explicit")))
            .Where(pair => pair.Function.Parameters is [{ Type: not null } parameter]
                && pair.Function.ReturnType is not null && Written(parameter.Type) == source
                && Written(pair.Function.ReturnType) == target).Distinct();
    }

    private bool UserConversion(ResolvedType source, ResolvedType target, AstNode location, State state, bool implicitOnly)
    {
        var candidates = ConversionOperators(source, target, implicitOnly).ToArray();
        if (candidates.Length > 1) { Report("SOL2222", "Ambiguous user conversion.", location); return false; }
        if (candidates.Length == 0) { return false; }
        var selected = candidates[0];
        foreach (var parameter in selected.Function.Parameters.Where(p => p.Type is not null))
        { ValidateFunctionEffects(Specialize(parameter.Type!, selected.Owner), location); }
        if (selected.Function.ReturnType is not null)
        { ValidateFunctionEffects(Specialize(selected.Function.ReturnType, selected.Owner), location); }
        if (Sources.Declarations.GetDeclaredSymbol(selected.Function) is { } symbol && !Accessible(symbol, location))
        { Report("SOL2214", "The user conversion is inaccessible.", location); }
        var effects = selected.Function.ThrownTypes.Select(t => Specialize(t, selected.Owner)).ToArray();
        RecordPotentialCallEdges(state, effects);
        state.Effects.UnionWith(effects);
        return true;
    }

    private Value OperatorCall(AstNode location, string name, Value[] operands, State state, Context context,
        bool exactOperands = false, ResolvedType? expected = null, bool usesNullableLifting = false)
    {
        if (operands.Any(v => v.Type is null)) { return new(null); }
        var declared = operands.Select(v => v.Type!).Distinct().SelectMany(t => Operators(t, name)).Distinct()
            .Where(candidate => candidate.Function.Parameters.Length == operands.Length
                && candidate.Function.Parameters.All(parameter => parameter.Type is not null)).ToArray();
        var candidates = new List<ResolvedOperatorCandidate>(declared.Length);
        bool unresolvedCandidate = false;
        foreach (var candidate in declared)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (TryCloseOperatorCandidate(location, candidate, operands, exactOperands, expected,
                    out var closed, out bool deferred))
            { candidates.Add(closed!); }
            unresolvedCandidate |= deferred;
        }
        if (unresolvedCandidate)
        {
            // An open operator parameter may still become applicable and change the winner.
            // Do not publish facts from the candidates that happened to close first.
            Defer(location);
            return new(null);
        }
        var checkedCandidates = candidates.Where(candidate => candidate.Function.IsCheckedOperator).ToArray();
        if (checkedCandidates.Length != 0) { candidates = checkedCandidates.ToList(); }
        var best = candidates.Where(candidate => !candidates.Any(other =>
            !ReferenceEquals(other.Function, candidate.Function) && BetterOperatorCandidate(other, candidate))).ToArray();
        if (best.Length != 1)
        {
            if (best.Length == 0 && context.Callable is { } callable && callable.TypeParameters.Any()
                && operands.Any(operand => DependsOnCallableParameter(operand.Type!, callable)))
            {
                // The open template cannot decide primitive or user-defined operator binding.
                // A closed generic instance must repeat this lookup with substituted operands.
                Defer(location);
                return new(null);
            }
            Report(best.Length == 0 ? "SOL2207" : "SOL2222", best.Length == 0
                ? "Operator is not defined for these operand types." : "Ambiguous operator overload.", location);
            return new(null);
        }
        var selected = best[0];
        var function = selected.Function;
        foreach (var parameter in function.Parameters.Where(p => p.Type is not null))
        { ValidateFunctionEffects(Types.Substitute(Written(parameter.Type!), selected.Substitution, _cancellation), location); }
        for (int index = 0; index < operands.Length; index++)
        {
            var target = selected.ParameterTypes[index];
            if (Types.ClassifyConversion(operands[index].Type!, target, _cancellation) == IntrinsicConversion.None)
            { UserConversion(operands[index].Type!, target, location, state, true); }
        }
        if (Sources.Declarations.GetDeclaredSymbol(function) is { } symbol && !Accessible(symbol, location))
        { Report("SOL2214", "The operator is inaccessible.", location); }
        var effects = function.ThrownTypes.Select(t => Types.Substitute(Written(t), selected.Substitution, _cancellation)).ToArray();
        RecordPotentialCallEdges(state, effects);
        state.Effects.UnionWith(effects);
        var resultType = function.ReturnType is null ? CheckBody(function).ReturnType
            : Types.Substitute(Written(function.ReturnType), selected.Substitution, _cancellation);
        if (resultType is not null) { ValidateFunctionEffects(resultType, location); }
        if (location is AstOperation operation && operation.Operation == name
            || location is AstAssignment assignment && assignment.Operation.Length > 1
                && assignment.Operation.EndsWith('=') && assignment.Operation[..^1] == name)
        {
            var typeParameters = Sources.Types.GetTypeParameters(function);
            var typeArguments = typeParameters.Select(parameter => selected.Substitution.GetValueOrDefault(parameter, parameter))
                .ToImmutableArray();
            OperatorBindings[location] = new(location, name, function, typeArguments,
                selected.ParameterTypes, resultType, effects.ToImmutableArray(), usesNullableLifting);
        }
        return new(resultType);
    }

    private bool DependsOnCallableParameter(ResolvedType type, AstCallable callable)
    {
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<ResolvedType>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current)) { continue; }
            if (current.Parameter is { } parameter && ReferenceEquals(parameter.Owner, callable)) { return true; }
            foreach (var argument in current.Arguments) { pending.Push(argument); }
            foreach (var effect in current.Effects) { pending.Push(effect); }
        }
        return false;
    }

    private static bool BetterOperatorRanks(int[] left, int[] right)
    {
        bool strictlyBetter = false;
        for (int index = 0; index < left.Length; index++)
        {
            if (left[index] > right[index]) { return false; }
            strictlyBetter |= left[index] < right[index];
        }
        return strictlyBetter;
    }
}
