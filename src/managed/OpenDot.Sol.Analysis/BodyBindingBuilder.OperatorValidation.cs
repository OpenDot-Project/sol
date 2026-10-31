// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private static readonly HashSet<string> PairedOperators = ["==", "!=", "<", ">", "<=", ">=", "true", "false"];
    private static readonly HashSet<string> UnaryOperators = ["!", "~", "++", "--", "true", "false"];
    private static readonly HashSet<string> BinaryOperators = ["*", "/", "%", "&", "|", "^", "<<", ">>", "==", "!=", "<", ">", "<=", ">="];
    private static readonly HashSet<string> CheckedOperators = ["+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>"];
    private static readonly HashSet<string> ConversionOperatorNames = ["implicit", "explicit"];
    private static readonly HashSet<string> DelegationOperators = ["getValue", "setValue", "provideDelegate"];

    private void ValidateOperatorContracts()
    {
        var byOwner = new Dictionary<DeclarationSymbol, List<AstFunction>>(ReferenceEqualityComparer.Instance);
        foreach (var function in Files.Keys.OfType<AstFunction>().Where(f => f.Modifiers.Contains("operator", StringComparer.Ordinal)))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (DelegationOperators.Contains(function.Name ?? "")) { continue; }

            var containingTypes = Ancestors(function).OfType<AstTypeDeclaration>().ToArray();
            if (containingTypes.Length < 2 || !containingTypes[0].IsCompanion)
            {
                Report("SOL2207", "Token operators must be declared in their owner's companion object.", function);
                continue;
            }

            var owner = Sources.Declarations.GetDeclaredSymbol(containingTypes[1]);
            if (owner is null)
            {
                Report("SOL2207", "An operator must belong to a declared nominal owner.", function);
                continue;
            }

            ValidateOperatorShape(function, owner);
            if (!byOwner.TryGetValue(owner, out var functions)) { functions = []; byOwner.Add(owner, functions); }
            if (!functions.Contains(function, ReferenceEqualityComparer.Instance)) { functions.Add(function); }
        }

        foreach (var functions in byOwner.Values) { ValidateOperatorPairs(functions); }
    }

    private void ValidateOperatorShape(AstFunction function, DeclarationSymbol owner)
    {
        string name = function.Name ?? "";
        bool validName = name is "+" or "-" || UnaryOperators.Contains(name) || BinaryOperators.Contains(name)
            || ConversionOperatorNames.Contains(name);
        if (!validName)
        {
            Report("SOL2207", "The operator name is not part of the supported operator contract.", function);
            return;
        }

        bool checkedVariant = function.IsCheckedOperator;
        if (checkedVariant && (!CheckedOperators.Contains(name) || ConversionOperatorNames.Contains(name)))
        { Report("SOL2207", "Checked variants are only valid for arithmetic and bitwise operators.", function); }

        var parameters = function.Parameters.ToArray();
        var parameterTypes = parameters.Select(p => p.Type is null ? null : Written(p.Type)).ToArray();
        var result = function.ReturnType is null ? null : Written(function.ReturnType);
        if (result is null || result.IsError)
        { Report("SOL2207", "An operator must declare a valid return type.", function); }
        if (parameters.Any(p => p.PassingMode != AstPassingMode.Value))
        { Report("SOL2207", "Operator operands must be passed by value.", function); }
        if (parameterTypes.Any(t => t is null || t.IsError))
        { Report("SOL2207", "Operator parameter types must be valid and explicit.", function); }

        int arity = parameters.Length;
        bool validArity = name is "+" or "-" ? arity is 1 or 2
            : ConversionOperatorNames.Contains(name) || UnaryOperators.Contains(name) ? arity == 1
            : BinaryOperators.Contains(name) && arity == 2;
        if (!validArity) { Report("SOL2207", "Operator has an invalid operand count.", function); }

        bool hasOwnerOperand = parameterTypes.OfType<ResolvedType>().Any(type =>
            type.Kind == ResolvedTypeKind.Nominal && ReferenceEquals(type.Declaration, owner));
        if (!hasOwnerOperand) { Report("SOL2207", "At least one operator operand must have the owner's type.", function); }

        if (name is "!" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "true" or "false")
        {
            if (result is not null && result != Types.GetPrimitive("bool"))
            { Report("SOL2207", "Boolean and comparison operators must return bool.", function); }
        }
        else if (name is "<<" or ">>" && arity == 2 && parameterTypes[1] is { } shiftType
            && shiftType != Types.GetPrimitive("int"))
        { Report("SOL2207", "The right operand of a shift operator must have type int.", function); }
        else if (name is "++" or "--")
        {
            if (result?.Kind != ResolvedTypeKind.Nominal || !ReferenceEquals(result.Declaration, owner))
            { Report("SOL2207", "Increment and decrement operators must return the owner's type.", function); }
        }
        else if (ConversionOperatorNames.Contains(name))
        {
            if (arity == 1 && parameterTypes[0] is { } source && result is not null
                && source.Declaration != owner && result.Declaration != owner)
            { Report("SOL2207", "A conversion operator must use the owner as its source or result type.", function); }
        }
    }

    private void ValidateOperatorPairs(List<AstFunction> functions)
    {
        foreach (var function in functions.Where(f => PairedOperators.Contains(f.Name ?? "")))
        {
            _cancellation.ThrowIfCancellationRequested();
            string counterpart = function.Name switch
            {
                "==" => "!=", "!=" => "==", "<" => ">", ">" => "<",
                "<=" => ">=", ">=" => "<=", "true" => "false", "false" => "true", _ => "",
            };
            bool hasPair = functions.Where(f => f.Name == counterpart)
                .Any(candidate => candidate.IsCheckedOperator == function.IsCheckedOperator
                    && SameOperatorParameters(function, candidate));
            if (!hasPair)
            {
                Report("SOL2207", "Operator " + function.Name + " requires a matching " + counterpart + " overload.", function);
            }
        }
    }

    private bool SameOperatorParameters(AstFunction left, AstFunction right)
    {
        if (left.TypeParameters.Count() != right.TypeParameters.Count() || left.Parameters.Length != right.Parameters.Length) { return false; }
        var leftParameters = left.Parameters.ToArray();
        var rightParameters = right.Parameters.ToArray();
        for (int index = 0; index < leftParameters.Length; index++)
        {
            var a = leftParameters[index]; var b = rightParameters[index];
            if (a.PassingMode != b.PassingMode || a.Type is null || b.Type is null
                || !SameOperatorType(Written(a.Type), Written(b.Type), left, right, 0)) { return false; }
        }
        return true;
    }

    private bool SameOperatorType(ResolvedType left, ResolvedType right, AstFunction leftOwner,
        AstFunction rightOwner, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (depth > 128 || left.Kind != right.Kind || left.Name != right.Name
            || !ReferenceEquals(left.Declaration, right.Declaration)
            || left.Arguments.Length != right.Arguments.Length
            || !left.ParameterModes.SequenceEqual(right.ParameterModes) || left.Effects.Length != right.Effects.Length)
        { return false; }

        if (left.Kind == ResolvedTypeKind.Parameter)
        {
            if (left.Parameter is not { } a || right.Parameter is not { } b) { return false; }
            bool correspondingMethodParameters = ReferenceEquals(a.Owner, leftOwner) && ReferenceEquals(b.Owner, rightOwner);
            return a.Ordinal == b.Ordinal && (correspondingMethodParameters || ReferenceEquals(a.Owner, b.Owner));
        }

        return left.Arguments.Zip(right.Arguments).All(pair =>
                SameOperatorType(pair.First, pair.Second, leftOwner, rightOwner, depth + 1))
            && left.Effects.Zip(right.Effects).All(pair =>
                SameOperatorType(pair.First, pair.Second, leftOwner, rightOwner, depth + 1));
    }
}
