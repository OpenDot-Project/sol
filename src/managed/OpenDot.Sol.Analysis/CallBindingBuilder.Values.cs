// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class CallBindingBuilder
{
    private sealed record ArgumentValue(ResolvedType? Type, bool Writable, string? Literal);

    private ArgumentValue Value(AstNode expression, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (depth > 128) { return new(null, false, null); }
        if (expression is AstCall call) { return new(Bind(call, depth + 1).Selected?.ReturnType, false, null); }
        if (expression is AstSequence { IsGrouping: true } group && group.Elements.Take(2).ToArray() is [var element])
        {
            return Value(element, depth + 1);
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
