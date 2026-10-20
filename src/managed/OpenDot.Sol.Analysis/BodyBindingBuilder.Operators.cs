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

    private Value OperatorCall(AstNode location, string name, Value[] operands, State state, Context context)
    {
        if (operands.Any(v => v.Type is null)) { return new(null); }
        var candidates = operands.Select(v => v.Type!).Distinct().SelectMany(t => Operators(t, name)).Distinct()
            .Where(p => p.Function.Parameters.Length == operands.Length && p.Function.Parameters.All(a => a.Type is not null)
                && p.Function.Parameters.Select((parameter, index) => Converts(operands[index].Type!, Written(parameter.Type!))).All(b => b)).ToArray();
        if (candidates.Length != 1)
        {
            Report(candidates.Length == 0 ? "SOL2207" : "SOL2222", candidates.Length == 0
                ? "Operator is not defined for these operand types." : "Ambiguous operator overload.", location);
            return new(null);
        }
        var function = candidates[0].Function;
        if (Sources.Declarations.GetDeclaredSymbol(function) is { } symbol && !Accessible(symbol, location))
        { Report("SOL2214", "The operator is inaccessible.", location); }
        state.Effects.UnionWith(function.ThrownTypes.Select(Written));
        return new(function.ReturnType is null ? CheckBody(function).ReturnType : Written(function.ReturnType));
    }
}
