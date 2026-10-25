// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
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

    private Value? LiftNullableUnary(AstNode location, string name, Value operand, State state, Context context)
    {
        if (operand.Type?.IsNullable != true || !IsLiftableValueType(operand.Type!.Arguments[0])) { return null; }
        var underlying = operand with { Type = operand.Type.Arguments[0] };
        if (name is ("+" or "-") && IsNumeric(underlying.Type!)) { return new(operand.Type); }
        if (name == "~" && IsInteger(underlying.Type!)) { return new(operand.Type); }
        if (name is ("++" or "--") && IsNumeric(underlying.Type!)) { return new(operand.Type); }
        if (name == "!" && underlying.Type == Bool) { return new(Types.GetNullable(Bool)); }
        if (!HasDeclaredOperator(name, underlying.Type!)) { return null; }
        var result = OperatorCall(location, name, [underlying], state, context);
        return result.Type is null ? new(null) : new(Types.GetNullable(result.Type));
    }

    private Value? LiftNullableBinary(AstNode location, string name, Value left, Value right, State state, Context context)
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
            var result = OperatorCall(location, name, [unwrappedLeft, unwrappedRight], state, context);
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
        if (Sources.Declarations.GetDeclaredSymbol(selected.Function) is { } symbol && !Accessible(symbol, location))
        { Report("SOL2214", "The user conversion is inaccessible.", location); }
        state.Effects.UnionWith(selected.Function.ThrownTypes.Select(Written));
        return true;
    }

    private Value OperatorCall(AstNode location, string name, Value[] operands, State state, Context context,
        bool exactOperands = false)
    {
        if (operands.Any(v => v.Type is null)) { return new(null); }
        var candidates = operands.Select(v => v.Type!).Distinct().SelectMany(t => Operators(t, name)).Distinct()
            .Where(p => p.Function.Parameters.Length == operands.Length && p.Function.Parameters.All(a => a.Type is not null)
                && p.Function.Parameters.Select((parameter, index) => exactOperands
                    ? operands[index].Type == Written(parameter.Type!)
                    : ConversionRank(operands[index].Type!, Written(parameter.Type!)) >= 0).All(b => b)).ToArray();
        var checkedCandidates = candidates.Where(candidate => candidate.Function.IsCheckedOperator).ToArray();
        if (checkedCandidates.Length != 0) { candidates = checkedCandidates; }
        var ranked = candidates.Select(candidate => (Candidate: candidate, Ranks: candidate.Function.Parameters
            .Select((parameter, index) => ConversionRank(operands[index].Type!, Written(parameter.Type!))).ToArray())).ToArray();
        var best = ranked.Where(candidate => !ranked.Any(other =>
            !ReferenceEquals(other.Candidate.Function, candidate.Candidate.Function)
            && BetterOperatorRanks(other.Ranks, candidate.Ranks))).ToArray();
        if (best.Length != 1)
        {
            Report(best.Length == 0 ? "SOL2207" : "SOL2222", best.Length == 0
                ? "Operator is not defined for these operand types." : "Ambiguous operator overload.", location);
            return new(null);
        }
        var function = best[0].Candidate.Function;
        for (int index = 0; index < operands.Length; index++)
        {
            var target = Written(function.Parameters[index].Type!);
            if (Types.ClassifyConversion(operands[index].Type!, target, _cancellation) == IntrinsicConversion.None)
            { UserConversion(operands[index].Type!, target, location, state, true); }
        }
        if (Sources.Declarations.GetDeclaredSymbol(function) is { } symbol && !Accessible(symbol, location))
        { Report("SOL2214", "The operator is inaccessible.", location); }
        state.Effects.UnionWith(function.ThrownTypes.Select(Written));
        return new(function.ReturnType is null ? CheckBody(function).ReturnType : Written(function.ReturnType));
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
