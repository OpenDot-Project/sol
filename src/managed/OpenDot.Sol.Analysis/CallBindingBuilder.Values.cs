// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class CallBindingBuilder
{
    private sealed record ArgumentValue(ResolvedType? Type, bool Writable, string? Literal,
        ImmutableArray<ArgumentValue> ArrayElements = default, bool IsArrayLiteral = false);

    private ArgumentValue Value(AstNode expression, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (depth > 128) { return new(null, false, null); }
        if (expression is AstCall call) { return new(Bind(call, depth + 1).Selected?.ReturnType, false, null); }
        if (expression is AstSequence { IsGrouping: true } group && group.Elements.Take(2).ToArray() is [var element])
        {
            return Value(element, depth + 1);
        }
        if (expression is AstSequence { SyntaxKind: NodeKind.ArrayExpression } array)
        {
            var elements = array.Elements.Select(element => Value(element, depth + 1)).ToImmutableArray();
            var elementType = UniqueCommonType(elements);
            return new(elementType is null ? null : Types.GetArray(elementType), false, null, elements, true);
        }
        if (expression is AstSequence { SyntaxKind: NodeKind.TupleExpression } tuple)
        {
            var elements = tuple.Elements.Select(e => Value(e, depth + 1).Type).ToArray();
            return new(elements.Any(t => t is null) ? null : Types.GetTuple(elements.Select(t => t!)), false, null);
        }
        if (expression is not AstAtom atom) { return new(null, false, null); }
        if (atom.SyntaxKind == NodeKind.LiteralExpression)
        {
            var token = atom.Origin.Element.Tokens.FirstOrDefault();
            var type = token?.Kind switch
            {
                SyntaxKind.IntegerLiteralToken => Types.GetPrimitive("int"),
                SyntaxKind.FloatLiteralToken => Types.GetPrimitive("double"),
                SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => Types.GetPrimitive("bool"),
                SyntaxKind.NullKeyword => Types.GetNullable(Types.GetBottom()),
                SyntaxKind.StringLiteralToken => CoreType("String"),
                SyntaxKind.RuneLiteralToken => CoreType("Rune"),
                _ => null,
            };
            string? literal = token?.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken ? atom.Spelling : null;
            return new(type, false, literal);
        }
        if (atom.SyntaxKind != NodeKind.NameExpression || string.IsNullOrWhiteSpace(atom.Spelling)) { return new(null, false, null); }
        var lookup = Sources.Lexical.Lookup(atom, atom.Spelling);
        if (lookup.Locals.Length == 1)
        {
            var local = lookup.Locals[0];
            if (local.Kind == LocalSymbolKind.ConstructorProperty) { return new(null, false, null); }
            if (local.Kind == LocalSymbolKind.Function)
            {
                var function = _files[atom].Nodes.OfType<AstFunction>().FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Origin.Element, local.Origin.Element));
                if (function is null || function.TypeParameters.Any() || function.ReturnType is not { } returnType
                    || function.Parameters.Any(parameter => parameter.Type is null))
                { return new(null, false, null); }
                var result = Sources.Types.GetType(returnType);
                var parameters = function.Parameters.Select(parameter =>
                {
                    var type = Sources.Types.GetType(parameter.Type!);
                    return parameter.PassingMode == AstPassingMode.Vararg ? Types.GetArray(type) : type;
                }).ToArray();
                var modes = function.Parameters.Select(parameter => parameter.PassingMode == AstPassingMode.Vararg
                    ? AstPassingMode.Value : parameter.PassingMode);
                return new(Types.GetFunction(parameters, result, modes,
                    function.ThrownTypes.Select(Sources.Types.GetType)), false, null);
            }
            if (local.WrittenType is { } written)
            {
                var type = Sources.Types.GetType(written);
                bool vararg = local.Kind == LocalSymbolKind.Parameter && _files[atom].Nodes.OfType<AstCallable>()
                    .SelectMany(c => c.Parameters).Any(p => p.PassingMode == AstPassingMode.Vararg
                        && ReferenceEquals(p.NameOrigin.Element, local.Origin.Element));
                return new(vararg ? Types.GetArray(type) : type, local.IsMutable, null);
            }
            var declaration = _files[atom].Nodes.OfType<AstProperty>().FirstOrDefault(p => ReferenceEquals(p.Origin.Element, local.Origin.Element));
            if (declaration?.Initializer is { } initializer)
            {
                return Value(initializer, depth + 1) with { Writable = local.IsMutable, Literal = null };
            }
        }
        // Ordinary properties are values, but cannot impersonate writable ref/out storage.
        if (!lookup.IsLocal && lookup.Declarations.Candidates.Length == 1
            && lookup.Declarations.Candidates[0].Parts[0] is AstProperty { Type: not null } property)
        {
            return new(Sources.Types.GetType(property.Type), false, null);
        }
        return new(null, false, null);
    }

    private ResolvedType? UniqueCommonType(ImmutableArray<ArgumentValue> values)
    {
        if (values.IsEmpty || values.Any(value => value.Type is null)) { return null; }
        var inputs = values.Select(value => value.Type!).Distinct().ToImmutableArray();
        if (inputs.Length == 1) { return inputs[0]; }

        var candidates = new HashSet<ResolvedType>();
        var pending = new Queue<(ResolvedType Type, int Depth)>();
        foreach (var input in inputs) { pending.Enqueue((input, 0)); }
        if (inputs.All(input => !input.IsRefLike && input.Kind != ResolvedTypeKind.Parameter)
            && CoreType("Object") is { } objectType)
        { candidates.Add(objectType); }

        while (pending.TryDequeue(out var item))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (item.Depth > 128)
            {
                throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                    "Array literal common-type projection exceeds the 128-level budget.");
            }
            if (!candidates.Add(item.Type)) { continue; }
            if (candidates.Count > 4096)
            {
                throw new TypeSpecializationException(SpecializationFailure.ResourceLimit,
                    "Array literal common-type projection exceeds the 4096-type budget.");
            }
            if (item.Type.IsNullable)
            {
                pending.Enqueue((item.Type.Arguments[0], item.Depth + 1));
            }
            else if (item.Type.Kind == ResolvedTypeKind.Nominal)
            {
                foreach (var parent in Types.GetDirectBases(item.Type, _cancellation))
                { pending.Enqueue((parent, item.Depth + 1)); }
            }
            else if (item.Type.Kind == ResolvedTypeKind.Parameter
                && item.Type.Parameter is { } identity)
            {
                foreach (var bound in ParameterBounds(identity))
                { pending.Enqueue((Sources.Types.GetType(bound), item.Depth + 1)); }
            }
        }

        if (inputs.Any(input => input.IsNullable))
        {
            foreach (var candidate in candidates.ToArray())
            {
                if (!candidate.IsNullable) { candidates.Add(Types.GetNullable(candidate)); }
            }
        }

        var viable = candidates.Where(candidate => inputs.All(input =>
            Types.ClassifyConversion(input, candidate, _cancellation) != IntrinsicConversion.None)).ToArray();
        var mostSpecific = viable.Where(candidate => !viable.Any(other => other != candidate
            && Types.ClassifyConversion(other, candidate, _cancellation) != IntrinsicConversion.None
            && Types.ClassifyConversion(candidate, other, _cancellation) == IntrinsicConversion.None))
            .ToArray();
        return mostSpecific.Length == 1 ? mostSpecific[0] : null;
    }

    private static IEnumerable<AstType> ParameterBounds(GenericParameterIdentity identity)
    {
        var parameters = identity.Owner switch
        {
            AstCallable callable => callable.TypeParameters.ToArray(),
            AstTypeDeclaration type => type.TypeParameters.ToArray(),
            _ => [],
        };
        var parameter = identity.Ordinal >= 0 && identity.Ordinal < parameters.Length
            ? parameters[identity.Ordinal] : null;
        if (parameter?.Constraint is { } direct) { yield return direct; }
        if (parameter?.Name is not { } name) { yield break; }
        foreach (var clause in identity.Owner.Children.Where(node => node.SyntaxKind == NodeKind.WhereClause))
        {
            foreach (var entry in clause.Children.Where(child => child.Children.Any(token =>
                         token.SyntaxKind == NodeKind.Name && token.Origin.Text == name)))
            {
                foreach (var type in entry.Children.OfType<AstType>()) { yield return type; }
            }
        }
    }

    private ResolvedType? CoreType(string name)
    {
        if (!Sources.Declarations.Modules.TryGetValue("std.core", out var core)) { return null; }
        var symbols = core.Declarations.Where(s => s.Kind == SymbolKind.Type && s.Name == name && s.Arity == 0).Take(2).ToArray();
        return symbols.Length == 1 ? Types.Nominal(symbols[0], []) : null;
    }

    private static bool LiteralFits(string spelling, ResolvedType target)
    {
        if (target.Kind == ResolvedTypeKind.Nullable) { target = target.Arguments[0]; }
        if (target.Kind != ResolvedTypeKind.Primitive) { return false; }
        string text = spelling.Replace("_", "", StringComparison.Ordinal);
        if (TryInteger(text, out var integer))
        {
            return target.Name switch
            {
                "sbyte" => integer >= sbyte.MinValue && integer <= sbyte.MaxValue,
                "byte" => integer >= byte.MinValue && integer <= byte.MaxValue,
                "short" => integer >= short.MinValue && integer <= short.MaxValue,
                "int" => integer >= int.MinValue && integer <= int.MaxValue,
                "long" => integer >= long.MinValue && integer <= long.MaxValue,
                "uint" => integer >= uint.MinValue && integer <= uint.MaxValue,
                "ulong" => integer >= ulong.MinValue && integer <= ulong.MaxValue,
                "half" => Half.TryParse(integer.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var roundedHalf) && Half.IsFinite(roundedHalf),
                "single" => float.IsFinite((float)integer),
                "double" => double.IsFinite((double)integer),
                _ => false,
            };
        }
        return target.Name switch
        {
            "half" => Half.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var half) && Half.IsFinite(half),
            "single" => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) && float.IsFinite(single),
            "double" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value),
            _ => false,
        };
    }

    internal static bool NumericLiteralFits(string spelling, ResolvedType target) => LiteralFits(spelling, target);

    private static bool TryInteger(string spelling, out BigInteger value)
    {
        if (spelling.StartsWith('-'))
        {
            bool success = TryInteger(spelling[1..], out value);
            value = -value;
            return success;
        }
        int radix = spelling.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16
            : spelling.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2
            : spelling.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : 10;
        value = 0;
        var digits = radix == 10 ? spelling.AsSpan() : spelling.AsSpan(2);
        if (digits.IsEmpty) { return false; }
        foreach (char character in digits)
        {
            int digit = character is >= '0' and <= '9' ? character - '0'
                : character is >= 'a' and <= 'f' ? character - 'a' + 10
                : character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
            if (digit < 0 || digit >= radix) { return false; }
            value = value * radix + digit;
            if (value.GetBitLength() > 16384) { return false; }
        }
        return true;
    }
}
