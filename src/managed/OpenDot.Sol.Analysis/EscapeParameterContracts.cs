// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal enum CallArgumentEscapeContract
{
    Unknown,
    NonEscaping,
    Escaping,
}

/// <summary>Resolves only source contracts that can be proved before overload binding.</summary>
internal static class EscapeParameterContracts
{
    internal static CallArgumentEscapeContract Resolve(AstCall call, int argumentIndex,
        Dictionary<AstAtom, NameBinding> bindings, AnalysisSession declarations, TypeAnalysis types)
    {
        if ((uint)argumentIndex >= (uint)call.Arguments.Length || call.Target is null)
        { return CallArgumentEscapeContract.Unknown; }

        AstExpression target = call.Target;
        if (target is AstGenericApplication generic && generic.Target is AstExpression genericTarget)
        { target = genericTarget; }
        var candidates = target switch
        {
            AstAtom atom => AtomTargets(atom, bindings, declarations),
            AstMember member => MemberTargets(call, member, bindings, declarations, types),
            _ => [],
        };
        candidates = candidates.Distinct(ReferenceEqualityComparer.Instance).Cast<AstCallable>().ToList();
        bool hasUserDefinedImplicitConversion = HasUserDefinedImplicitConversion(declarations);
        candidates = candidates.Where(candidate => MayAcceptKnownPrimitiveArguments(call, argumentIndex,
            candidate.Parameters, bindings, hasUserDefinedImplicitConversion, types)).ToList();
        var undominated = candidates.Where(candidate => !candidates.Any(other =>
            !ReferenceEquals(candidate, other) && IsDefinitelyBetter(call, argumentIndex, other, candidate,
                bindings, types))).ToList();
        if (undominated.Count != 0) { candidates = undominated; }
        if (candidates.Count == 0) { return CallArgumentEscapeContract.Unknown; }

        bool sawEscaping = false;
        foreach (var candidate in candidates)
        {
            if (!TryMapArgument(call, argumentIndex, candidate.Parameters, out int parameterIndex))
            { return CallArgumentEscapeContract.Unknown; }
            var parameter = candidate.Parameters[parameterIndex];
            if (!IsFunctionType(parameter, types)) { return CallArgumentEscapeContract.Unknown; }
            sawEscaping |= parameter.IsEscaping;
        }
        return sawEscaping ? CallArgumentEscapeContract.Escaping : CallArgumentEscapeContract.NonEscaping;
    }

    private static bool MayAcceptKnownPrimitiveArguments(AstCall call, int callbackArgumentIndex,
        System.Collections.Immutable.ImmutableArray<AstParameter> parameters,
        Dictionary<AstAtom, NameBinding> bindings, bool hasUserDefinedImplicitConversion, TypeAnalysis types)
    {
        for (int argumentIndex = 0; argumentIndex < call.Arguments.Length; argumentIndex++)
        {
            if (argumentIndex == callbackArgumentIndex) { continue; }
            var argument = call.Arguments[argumentIndex];
            if (argument.Mode != AstArgumentMode.Value
                || argument.Value is not AstExpression expression
                || !TryMapArgument(call, argumentIndex, parameters, out int parameterIndex)
                || parameters[parameterIndex].Type is not { } parameterType
                || !TryKnownArgumentType(expression, bindings, types, out var actualType, out var numericLiteral))
            { continue; }

            var expectedType = types.GetType(parameterType);
            if (numericLiteral is not null)
            {
                if (expectedType.Kind == ResolvedTypeKind.Primitive && expectedType.Name != "quad"
                    && !CallBindingBuilder.NumericLiteralFits(numericLiteral, expectedType))
                { return false; }
                continue;
            }
            if (actualType.Kind == ResolvedTypeKind.Primitive
                && expectedType.Kind == ResolvedTypeKind.Primitive
                && actualType != expectedType
                && new TypeSpecialization(types).ClassifyConversion(actualType, expectedType)
                    == IntrinsicConversion.None)
            { return false; }

            // Nominally unrelated overloads can also be excluded when the compilation contains
            // no user-defined implicit conversions. If one exists, keep the candidate here: this
            // pre-binding lifetime pass cannot prove whether that operator applies to this pair.
            if (actualType.Kind == ResolvedTypeKind.Nominal
                && expectedType.Kind == ResolvedTypeKind.Nominal
                && !hasUserDefinedImplicitConversion)
            {
                try
                {
                    if (new TypeSpecialization(types).ClassifyConversion(actualType, expectedType)
                        == IntrinsicConversion.None)
                    { return false; }
                }
                catch (TypeSpecializationException)
                {
                    // An incomplete hierarchy cannot exclude an overload for a lifetime proof.
                }
            }
        }

        return true;
    }

    private static bool HasUserDefinedImplicitConversion(AnalysisSession declarations)
        => declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>().Any(function => function.Name == "implicit"
                && function.Modifiers.Contains("operator"));

    private static bool IsDefinitelyBetter(AstCall call, int callbackArgumentIndex,
        AstCallable better, AstCallable worse, Dictionary<AstAtom, NameBinding> bindings, TypeAnalysis types)
    {
        if (!TryMapArgument(call, callbackArgumentIndex, better.Parameters, out int betterCallback)
            || !TryMapArgument(call, callbackArgumentIndex, worse.Parameters, out int worseCallback)
            || better.Parameters[betterCallback].Type is not { } betterCallbackType
            || worse.Parameters[worseCallback].Type is not { } worseCallbackType
            || types.GetType(betterCallbackType) != types.GetType(worseCallbackType))
        { return false; }

        bool strictlyBetter = false;
        for (int index = 0; index < call.Arguments.Length; index++)
        {
            if (index == callbackArgumentIndex) { continue; }
            if (!TryMapArgument(call, index, better.Parameters, out int betterParameter)
                || !TryMapArgument(call, index, worse.Parameters, out int worseParameter)
                || !TryKnownConversionRank(call.Arguments[index], better.Parameters[betterParameter], bindings,
                    types, out int betterRank)
                || !TryKnownConversionRank(call.Arguments[index], worse.Parameters[worseParameter], bindings,
                    types, out int worseRank)
                || betterRank > worseRank)
            { return false; }
            strictlyBetter |= betterRank < worseRank;
        }

        return strictlyBetter;
    }

    private static bool TryKnownConversionRank(AstArgument argument, AstParameter parameter,
        Dictionary<AstAtom, NameBinding> bindings, TypeAnalysis types, out int rank)
    {
        rank = 0;
        if (argument.Mode != AstArgumentMode.Value || parameter.PassingMode != AstPassingMode.Value
            || argument.Value is not AstExpression expression || parameter.Type is not { } parameterType
            || !TryKnownArgumentType(expression, bindings, types, out var actualType, out var numericLiteral))
        { return false; }

        var expectedType = types.GetType(parameterType);
        if (expectedType.Kind == ResolvedTypeKind.Parameter || actualType.Kind == ResolvedTypeKind.Parameter)
        { return false; }
        IntrinsicConversion conversion;
        try { conversion = new TypeSpecialization(types).ClassifyConversion(actualType, expectedType); }
        catch (TypeSpecializationException) { return false; }

        if (conversion != IntrinsicConversion.None)
        {
            rank = (int)conversion;
            return true;
        }
        if (numericLiteral is not null && expectedType.Kind == ResolvedTypeKind.Primitive
            && expectedType.Name != "quad" && CallBindingBuilder.NumericLiteralFits(numericLiteral, expectedType))
        {
            rank = 1;
            return true;
        }

        // A user conversion may make this candidate applicable, but its overload rank is not
        // available in this pre-binding lifetime analysis.
        return false;
    }

    private static bool TryKnownArgumentType(AstExpression expression,
        Dictionary<AstAtom, NameBinding> bindings, TypeAnalysis types, out ResolvedType type,
        out string? numericLiteral)
    {
        while (expression is AstSequence { IsGrouping: true } grouping
            && grouping.Elements.Take(2).ToArray() is [var child])
        { expression = child; }

        numericLiteral = null;
        if (expression is AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.LiteralExpression } literal)
        {
            var token = literal.Origin.Element.Tokens.FirstOrDefault();
            if (token?.Kind is OpenDot.Sol.Cst.SyntaxKind.IntegerLiteralToken
                or OpenDot.Sol.Cst.SyntaxKind.FloatLiteralToken)
            {
                type = types.Types.FirstOrDefault(candidate => candidate.Kind == ResolvedTypeKind.Primitive
                    && candidate.Name == (token.Kind == OpenDot.Sol.Cst.SyntaxKind.IntegerLiteralToken ? "int" : "double"))!;
                if (type is null) { return false; }
                numericLiteral = literal.Spelling;
                return true;
            }
            if (token?.Kind is OpenDot.Sol.Cst.SyntaxKind.TrueKeyword or OpenDot.Sol.Cst.SyntaxKind.FalseKeyword)
            {
                type = types.Types.FirstOrDefault(candidate => candidate.Kind == ResolvedTypeKind.Primitive
                    && candidate.Name == "bool")!;
                return type is not null;
            }
        }

        if (expression is AstAtom { SyntaxKind: OpenDot.Sol.Cst.NodeKind.NameExpression } atom
            && bindings.TryGetValue(atom, out var binding)
            && binding.Lookup.Locals.SingleOrDefault()?.WrittenType is { } writtenType)
        {
            type = types.GetType(writtenType);
            return !type.IsError;
        }

        type = null!;
        return false;
    }

    private static List<AstCallable> AtomTargets(AstAtom atom, Dictionary<AstAtom, NameBinding> bindings,
        AnalysisSession declarations)
    {
        if (!bindings.TryGetValue(atom, out var targetBinding)) { return []; }
        var result = new List<AstCallable>();
        foreach (var local in targetBinding.Lookup.Locals.Where(local => local.Kind == LocalSymbolKind.Function))
        {
            result.AddRange(declarations.Files.SelectMany(file => file.Nodes).OfType<AstFunction>()
                .Where(function => ReferenceEquals(function.Origin.Element, local.Origin.Element)));
        }
        foreach (var symbol in targetBinding.Lookup.Declarations.Candidates)
        { AddCallables(symbol, result); }
        return result;
    }

    private static List<AstCallable> MemberTargets(AstCall call, AstMember member,
        Dictionary<AstAtom, NameBinding> bindings, AnalysisSession declarations, TypeAnalysis types)
    {
        if (member.Name is null || member.IsSafe || member.Receiver is not AstAtom receiver) { return []; }
        var specialization = new TypeSpecialization(types);
        DeclarationSymbol? owner = null;
        ResolvedType? receiverType = null;
        if (receiver.Spelling == "this")
        {
            var file = declarations.Files.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Psi, call.Origin.Element.Snapshot));
            for (AstNode? current = call; file is not null && current is not null; current = file.GetParent(current))
            {
                if (current is AstTypeDeclaration type)
                {
                    owner = declarations.GetDeclaredSymbol(type);
                    if (owner is not null)
                    {
                        receiverType = specialization.Nominal(owner, types.GetTypeParameters(type));
                    }
                    break;
                }
            }
        }
        else if (bindings.TryGetValue(receiver, out var binding))
        {
            var local = binding.Lookup.Locals.SingleOrDefault();
            if (local?.WrittenType is { } written)
            {
                receiverType = types.GetType(written);
                owner = receiverType.Declaration;
            }
            if (owner is null)
            { owner = binding.Lookup.Declarations.Candidates.FirstOrDefault(symbol => symbol.Kind == SymbolKind.Type); }
        }

        if (owner is null) { return []; }
        receiverType ??= specialization.Nominal(owner, types.GetTypeParameters(owner.Parts[0]));
        var members = new List<AstCallable>();
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        var visited = new HashSet<int>();
        pending.Enqueue((receiverType, 0));
        while (pending.TryDequeue(out var current))
        {
            if (current.Depth > 128) { return []; }
            if (!visited.Add(current.Type.Id)) { continue; }
            if (visited.Count > 4096) { return []; }
            if (current.Type.Declaration is { } declaration)
            {
                foreach (var symbol in declaration.FindMembers(member.Name)) { AddCallables(symbol, members); }
            }
            try
            {
                foreach (var parent in specialization.GetDirectBases(current.Type))
                { pending.Enqueue((parent, current.Depth + 1)); }
            }
            catch (TypeSpecializationException)
            {
                // A partial hierarchy is not enough to prove the call's lifetime contract.
                return [];
            }
        }
        return members;
    }

    private static void AddCallables(DeclarationSymbol symbol, List<AstCallable> target)
    {
        foreach (var part in symbol.Parts)
        {
            if (part is AstCallable callable) { target.Add(callable); }
            else if (part is AstTypeDeclaration type && type.PrimaryConstructor is { } constructor)
            { target.Add(constructor); }
        }
    }

    private static bool IsFunctionType(AstParameter parameter, TypeAnalysis types)
    {
        if (parameter.Type is null || parameter.PassingMode != AstPassingMode.Value) { return false; }
        var type = types.GetType(parameter.Type);
        return type.Kind == ResolvedTypeKind.Function
            || type.IsNullable && type.Arguments.Length == 1
                && type.Arguments[0].Kind == ResolvedTypeKind.Function;
    }

    private static bool TryMapArgument(AstCall call, int requestedIndex,
        System.Collections.Immutable.ImmutableArray<AstParameter> parameters, out int mapped)
    {
        mapped = -1;
        if (parameters.IsEmpty) { return false; }
        int position = 0;
        bool requiresNamed = false;
        for (int index = 0; index <= requestedIndex; index++)
        {
            var argument = call.Arguments[index];
            int target;
            if (argument.IsTrailingLambda)
            {
                if (index != call.Arguments.Length - 1) { return false; }
                target = parameters.Length - 1;
                if (parameters[target].PassingMode != AstPassingMode.Value) { return false; }
            }
            else if (argument.Name is { } name)
            {
                target = -1;
                for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (parameters[parameterIndex].Name == name) { target = parameterIndex; break; }
                }
                if (target < 0) { return false; }
                if (target != position) { requiresNamed = true; }
            }
            else
            {
                if (requiresNamed || position >= parameters.Length) { return false; }
                target = position;
            }

            bool namedArray = parameters[target].PassingMode == AstPassingMode.Vararg
                && argument.Name is not null;
            if (target == position && (parameters[target].PassingMode != AstPassingMode.Vararg || namedArray))
            { position++; }
            if (parameters[target].PassingMode == AstPassingMode.Vararg && namedArray) { requiresNamed = true; }
            if (index == requestedIndex) { mapped = target; }
        }
        return mapped >= 0;
    }
}
