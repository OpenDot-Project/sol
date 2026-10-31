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
    private Value Expression(AstExpression? expression, State state, Context context, ResolvedType? expected = null, bool read = true)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (expression is null) { return new(null); }
        if (++_depth > 128)
        {
            --_depth; Report("SOL2290", "Body analysis nesting limit exceeded.", expression); Defer(expression); return new(null);
        }
        int errors = Diagnostics.Count;
        var incomingEffects = state.Effects.ToArray();
        state.Effects.Clear();
        try
        {
            var value = expression switch
            {
                AstAtom atom => Atom(atom, state, context, expected, read),
                AstGenericApplication application when application.Target is AstAtom { Spelling: "super" }
                    => QualifiedInterfaceSuper(application),
                AstMember member => Member(member, state, context, read, expected),
                AstCall call => SafeCall(call, state, context, expected),
                AstOperation operation => Operation(operation, state, context, expected),
                AstSequence sequence => Sequence(sequence, state, context, expected),
                AstLambda lambda => Lambda(lambda, state, context, expected),
                AstAnonymousFunction anonymous => AnonymousFunction(anonymous, state, context, expected),
                AstLabelledExpression labelled when labelled.Lambda is not null => Lambda(labelled.Lambda, state, context, expected),
                AstTry attempt => Try(attempt, state, context, expected),
                _ when expression.SyntaxKind == NodeKind.ThrowExpression => Throw(expression, state, context),
                _ when expression.SyntaxKind == NodeKind.IndexExpression => Index(expression, state, context, read),
                _ => Unsupported(expression),
            };
            if (value.Type is null) { Defer(expression); }
            var status = Diagnostics.Count != errors ? BodyBindingStatus.Invalid
                : value.Type is null || _deferred.Contains(expression) ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound;
            Expressions[expression] = new(expression, value.Type, status, value.Writable, value.Storage,
                state.Effects.OrderBy(t => t.Id).ToImmutableArray())
            {
                FunctionValueTarget = value.Type?.Kind == ResolvedTypeKind.Function
                    ? value.Symbol?.Parts[0] as AstFunction : null,
                FunctionValueTypeArguments = value.Type?.Kind == ResolvedTypeKind.Function
                    && !value.FunctionValueTypeArguments.IsDefault
                    ? value.FunctionValueTypeArguments : [],
            };
            if (value.Type?.Kind == ResolvedTypeKind.Bottom) { state.Reachable = false; }
            return value;
        }
        catch (TypeSpecializationException error)
        {
            Report("SOL2290", error.Message, expression); Defer(expression); return new(null);
        }
        finally { state.Effects.UnionWith(incomingEffects); --_depth; }
    }

    private Value Unsupported(AstExpression expression) { Defer(expression); return new(null); }

    private Value Atom(AstAtom atom, State state, Context context, ResolvedType? expected, bool read)
    {
        if (atom.SyntaxKind == NodeKind.PositionalExpression)
        {
            if (context.Lambda is not { Form: AstLambdaForm.Positional } || context.LambdaType is null
                || !int.TryParse(atom.Spelling.AsSpan(1), out int position) || position >= context.LambdaType.ParameterModes.Length)
            { Report("SOL2220", "Positional parameter requires an expected function type and an in-range index.", atom); return new(null); }
            return new(context.LambdaType.Arguments[position]);
        }
        if (atom.SyntaxKind == NodeKind.LiteralExpression)
        {
            var token = atom.Origin.Element.Tokens.FirstOrDefault();
            var target = expected?.IsNullable == true ? expected.Arguments[0] : expected;
            if (token?.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken)
            {
                target = target?.Kind == ResolvedTypeKind.Primitive && target.Name != "bool" ? target
                    : Types.GetPrimitive(token.Kind == SyntaxKind.IntegerLiteralToken ? "int" : "double");
                if (target.Name == "quad") { Defer(atom); }
                else if (!CallBindingBuilder.NumericLiteralFits(atom.Spelling, target)) { Report("SOL2201", "Numeric literal is outside the target domain.", atom); }
                return new(target, Literal: atom.Spelling);
            }
            return new(token?.Kind switch
            {
                SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => Bool,
                SyntaxKind.NullKeyword => Types.GetNullable(Types.GetBottom()),
                SyntaxKind.StringLiteralToken => Core("String"),
                SyntaxKind.RuneLiteralToken => Core("Rune"),
                _ => null,
            });
        }
        if (string.IsNullOrWhiteSpace(atom.Spelling)) { Defer(atom); return new(null); }
        if (atom.Spelling == "field" && context.Callable is AstAccessor accessor && ParentProperty(accessor) is { Type: not null } property)
        { return new(Written(property.Type), accessor.Name is "set" or "init", true); }
        if (atom.Spelling is "this" or "super")
        {
            var owner = ContainingType(atom);
            if (owner is null) { Report("SOL2210", "An instance receiver is unavailable here.", atom); return new(null); }
            if (atom.Spelling == "super") { owner = Types.GetDirectBases(owner, _cancellation).FirstOrDefault(t => t.Declaration?.Parts[0].SyntaxKind == NodeKind.ClassDeclaration); }
            return new(owner);
        }
        var lookup = Sources.Lexical.Lookup(atom, atom.Spelling);
        if (lookup.Locals is [var local])
        {
            if (local.Kind == LocalSymbolKind.Function)
            {
                var function = Files[atom].Nodes.OfType<AstFunction>().FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.Origin.Element, local.Origin.Element));
                if (function is null)
                { Defer(atom); return new(null, Local: local); }
                return LocalFunctionValue(atom, function, local, expected);
            }
            var type = read ? state.Narrowed.GetValueOrDefault(local) ?? LocalType(local) : LocalType(local);
            bool tracked = local.Kind is LocalSymbolKind.Parameter or LocalSymbolKind.Variable or LocalSymbolKind.AccessorValue;
            if (read && tracked && state.Reachable && !state.Assigned.Contains(local))
            {
                bool contextualCapture = context.Callable is AstFunction function
                    && !ReferenceEquals(_lexicalEntry?.Function, function)
                    && Sources.Lexical.GetLocalFunctionCaptures(function).Any(capture => ReferenceEquals(capture.Local, local));
                if (contextualCapture) { Defer(atom); }
                else { Report("SOL2240", "Variable may be read before assignment: " + local.Name, atom); }
            }
            return new(type, local.IsMutable, local.Kind != LocalSymbolKind.ConstructorProperty, local);
        }
        var symbols = lookup.Declarations.Candidates;
        if (symbols.Length == 1)
        { return SymbolValue(symbols[0], ContainingType(atom), atom, state, context, read, expected: expected); }
        if (symbols.IsEmpty && ContainingType(atom) is { } receiver)
        {
            var inherited = FindMembers(receiver, atom.Spelling, atom).ToArray();
            if (inherited.Length == 1) { return SymbolValue(inherited[0].Symbol, inherited[0].DeclaringType, atom, state, context, read); }
            if (inherited.Length > 1 && inherited.All(member => member.Symbol.Kind == SymbolKind.Function))
            { Defer(atom); return new(null); }
            if (inherited.Length > 1) { Report("SOL2212", "Ambiguous inherited value name.", atom); return new(null); }
        }
        if (symbols.IsEmpty) { Report("SOL2211", "Unresolved value name: " + atom.Spelling, atom); }
        else if (symbols.Any(s => s.Kind != SymbolKind.Function)) { Report("SOL2212", "Ambiguous value name.", atom); }
        else { return OverloadedFunctionValue(atom, symbols, expected); }
        return new(null);
    }

    private ResolvedType? LocalType(LocalSymbol local)
    {
        if (_localTypes.TryGetValue(local, out var type)) { return type; }
        if (local.WrittenType is not null) { return Written(local.WrittenType); }
        return null;
    }

    private Value Sequence(AstSequence sequence, State state, Context context, ResolvedType? expected)
    {
        var elements = sequence.Elements.ToArray();
        if (sequence.IsGrouping && elements.Length == 1) { return Expression(elements[0], state, context, expected); }
        if (sequence.SyntaxKind == NodeKind.ArrayExpression)
        {
            var elementType = expected?.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray ? expected.Arguments[0] : null;
            foreach (var element in elements)
            {
                var value = Expression(element, state, context, elementType);
                elementType ??= value.Type;
                Require(value, elementType, element);
            }
            if (elementType is null) { Report("SOL2202", "An empty array literal needs an element type.", sequence); return new(null); }
            if (expected?.Kind == ResolvedTypeKind.FixedArray)
            {
                if (expected.Name != elements.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Report("SOL2203", "Inline array length does not match the literal.", sequence); }
                return new(expected);
            }
            return new(Types.GetArray(elementType));
        }
        var values = elements.Select((e, i) => Expression(e, state, context,
            expected?.Kind == ResolvedTypeKind.Tuple && i < expected.Arguments.Length ? expected.Arguments[i] : null)).ToArray();
        return new(values.Any(v => v.Type is null) ? null : Types.GetTuple(values.Select(v => v.Type!)));
    }

    private Value Index(AstExpression expression, State state, Context context, bool read)
    {
        var operands = expression.Children.OfType<AstExpression>().ToArray();
        var receiver = operands.Length == 0 ? new Value(null) : Expression(operands[0], state, context);
        if (receiver.Type is { Kind: ResolvedTypeKind.Nominal or ResolvedTypeKind.Parameter } nominal)
        { return InterfaceIndex(expression, nominal, state, context, read); }
        var indices = expression.Children.Where(n => n.SyntaxKind == NodeKind.ArgumentList).SelectMany(n => n.Children)
            .SelectMany(n => n is AstExpression e ? new[] { e } : n.Children.OfType<AstExpression>()).ToArray();
        if (indices.Length == 0) { indices = operands.Skip(1).ToArray(); }
        foreach (var index in indices) { Require(Expression(index, state, context, Types.GetPrimitive("int")), Types.GetPrimitive("int"), index); }
        if (receiver.Type?.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)
        {
            if (expression.Children.Where(node => node.SyntaxKind == NodeKind.ArgumentList).SelectMany(node => node.Children)
                .Any(node => node.Children.Any(child => child.SyntaxKind is NodeKind.Name or NodeKind.RefArgument
                    or NodeKind.OutArgument or NodeKind.OutVariable or NodeKind.SpreadArgument)))
            { Report("SOL2213", "Array indexing requires a positional value index.", expression); }
            if (indices.Length != 1) { Report("SOL2213", "Array indexing requires one int index.", expression); }
            return new(receiver.Type.Arguments[0], true, true);
        }
        if (receiver.Type?.IsNullable == true) { Report("SOL2243", "Nullable receiver must be checked before indexing.", expression); }
        Defer(expression); return new(null);
    }

    private Value Operation(AstOperation operation, State state, Context context, ResolvedType? expected)
    {
        var operands = operation.Operands.ToArray();
        if (operands.Length == 0) { return Unsupported(operation); }
        string op = operation.Operation;
        if (operands.Length == 1 && op == "-" && operands[0] is AstAtom { SyntaxKind: NodeKind.LiteralExpression } negative
            && negative.Origin.Element.Tokens.FirstOrDefault()?.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken)
        {
            var target = expected?.IsNullable == true ? expected.Arguments[0] : expected;
            target = target is not null && IsNumeric(target) ? target : Types.GetPrimitive(negative.Spelling.Contains('.', StringComparison.Ordinal) ? "double" : "int");
            if (!CallBindingBuilder.NumericLiteralFits("-" + negative.Spelling, target)) { Report("SOL2201", "Negative literal is outside the target domain.", operation); }
            return new(target, Literal: "-" + negative.Spelling);
        }
        var left = Expression(operands[0], state, context, op is "+" or "-" or "*" or "/" or "%" ? expected : null);
        if (operation.TargetType is { } syntax)
        {
            var target = Written(syntax);
            if (operation.SyntaxKind == NodeKind.TypeTestExpression) { return new(Bool); }
            if (left.Type is not null && !Converts(left.Type, target) && !Converts(target, left.Type)
                && !(IsNumeric(left.Type) && IsNumeric(target)) && !UserConversion(left.Type, target, operation, state, false))
            { Report("SOL2204", "No permitted explicit conversion exists.", operation); }
            return new(op.Contains('?', StringComparison.Ordinal) ? Types.GetNullable(target) : target);
        }
        if (operands.Length == 1)
        {
            if (op == "!!") { return new(left.Type?.IsNullable == true ? left.Type.Arguments[0] : left.Type); }
            if (op is not ("++" or "--") && left.Type?.IsNullable == true
                && LiftNullableUnary(operation, op, left, state, context, expected) is { } liftedUnary)
            { return liftedUnary; }
            if (op == "!" && left.Type == Bool) { return new(Bool); }
            if (op is "+" or "-" && left.Type is not null && IsNumeric(left.Type)) { return new(left.Type); }
            if (op == "~" && left.Type is not null && IsInteger(left.Type)) { return new(left.Type); }
            if (op is "++" or "--")
            {
                if (!left.Writable) { Report("SOL2241", "Increment requires a writable location.", operation); }
                if (left.Local is not null) { state.Narrowed.Remove(left.Local); }
                if (left.Type?.IsNullable == true
                    && LiftNullableUnary(operation, op, left, state, context) is { } liftedIncrement)
                { return liftedIncrement; }
                if (left.Type is not null && IsNumeric(left.Type)) { return new(left.Type); }
            }
            var unaryExpected = op is "++" or "--" ? left.Type : expected;
            return OperatorCall(operation, op, [left], state, context, expected: unaryExpected);
        }
        var rightState = state;
        if (op is "&&" or "||" or "?:")
        {
            rightState = state.Clone();
            if (op is "&&" or "||") { Refine(operands[0], rightState, op == "&&"); }
        }
        var right = Expression(operands[1], rightState, context,
            op is "+" or "-" or "*" or "/" or "%" ? left.Type : null);
        if (!ReferenceEquals(rightState, state))
        {
            if (op == "?:" && !rightState.Reachable && operands[0] is AstAtom name)
            {
                var locals = Sources.Lexical.Lookup(name, name.Spelling).Locals;
                if (locals is [var local] && !local.IsMutable && left.Type?.IsNullable == true) { state.Narrowed[local] = left.Type.Arguments[0]; }
            }
            state.Join(state.Clone(), rightState);
        }
        if (left.Type is null || right.Type is null) { return new(null); }
        if (op == "?:")
        {
            var nonnull = left.Type.IsNullable ? left.Type.Arguments[0] : left.Type;
            if (!Converts(right.Type, nonnull)) { Report("SOL2205", "Elvis alternatives have incompatible types.", operation); }
            return new(nonnull);
        }
        if (op is not ("&&" or "||") && (left.Type.IsNullable || right.Type.IsNullable)
            && LiftNullableBinary(operation, op, left, right, state, context, expected) is { } liftedBinary)
        { return liftedBinary; }
        if (op is "&&" or "||")
        {
            if (left.Type != Bool || right.Type != Bool)
            {
                string truthOperator = op == "&&" ? "false" : "true";
                string bitwiseOperator = op == "&&" ? "&" : "|";
                bool hasTruth = HasDeclaredOperator(truthOperator, left.Type);
                bool hasBitwise = HasDeclaredOperator(bitwiseOperator, left.Type, right.Type);
                if (hasTruth || hasBitwise)
                {
                    var truth = OperatorCall(operation, truthOperator, [left], state, context,
                        exactOperands: true, expected: Bool);
                    if (truth.Type != Bool)
                    { Report("SOL2207", "Conditional logical truth operators must return bool.", operation); }
                    var logical = OperatorCall(operation, bitwiseOperator, [left, right], state, context,
                        exactOperands: true, expected: left.Type);
                    if (logical.Type != left.Type)
                    { Report("SOL2207", "Conditional logical bitwise operators must accept and return the same operand type.", operation); }
                    return logical.Type == left.Type ? logical : new(null);
                }
            }
            Require(left, Bool, operands[0]); Require(right, Bool, operands[1]); return new(Bool);
        }
        if (op is "===" or "!==")
        {
            if (IsNumeric(left.Type) || IsNumeric(right.Type) || left.Type.IsRefLike || right.Type.IsRefLike)
            { Report("SOL2207", "Identity comparison requires reference operands.", operation); }
            return new(Bool);
        }
        if (op is "==" or "!=" && HasDeclaredOperator(op, left.Type, right.Type))
        { return OperatorCall(operation, op, [left, right], state, context, expected: Bool); }
        if (op is "==" or "!=" && operands.Length == 2
            && (IsNullLiteral(operands[0]) && CanBeNull(right.Type)
                || IsNullLiteral(operands[1]) && CanBeNull(left.Type)))
        { return new(Bool); }
        if (op is "==" or "!=" && (Converts(left.Type, right.Type) || Converts(right.Type, left.Type))) { return new(Bool); }
        if (left.Type == right.Type && IsNumeric(left.Type) && (op is "+" or "-" or "*" or "/" or "%" or "<" or ">" or "<=" or ">="
            || IsInteger(left.Type) && op is "&" or "|" or "^"))
        {
            return new(op is "<" or ">" or "<=" or ">=" ? Bool : left.Type);
        }
        if (op is "<<" or ">>" && IsInteger(left.Type) && right.Type == Types.GetPrimitive("int")) { return new(left.Type); }
        if (left.Type == Bool && right.Type == Bool && op is "&" or "|" or "^") { return new(Bool); }
        if (op == "+" && left.Type == Core("String") && right.Type == left.Type) { return new(left.Type); }
        var binaryExpected = op is "<" or ">" or "<=" or ">=" ? Bool : expected;
        return OperatorCall(operation, op, [left, right], state, context, expected: binaryExpected);
    }

    private static bool IsNumeric(ResolvedType type) => type.Kind == ResolvedTypeKind.Primitive && type.Name != "bool";
    private static bool IsInteger(ResolvedType type) => IsNumeric(type) && type.Name is not ("half" or "single" or "double" or "quad");
    private static bool IsNullLiteral(AstExpression expression)
    {
        while (expression is AstSequence { IsGrouping: true } grouping)
        {
            var elements = grouping.Elements.Take(2).ToArray();
            if (elements.Length != 1) { return false; }
            expression = elements[0];
        }
        return expression is AstAtom { SyntaxKind: NodeKind.LiteralExpression, Spelling: "null" };
    }

    private static bool CanBeNull(ResolvedType type) => type.IsNullable
        || type.Kind is ResolvedTypeKind.Function or ResolvedTypeKind.Array
        || type.Kind == ResolvedTypeKind.Nominal && type.Declaration?.Parts.FirstOrDefault()?.SyntaxKind
            is NodeKind.ClassDeclaration or NodeKind.ObjectDeclaration;

    private void Require(Value value, ResolvedType? expected, AstNode node, State? state = null)
    {
        if (expected is null || value.Type is null || expected.IsError || value.Type.IsError) { Defer(node); return; }
        if (!Converts(value.Type, expected) && !(state is not null && UserConversion(value.Type, expected, node, state, true)))
        {
            var genericOwner = Ancestors(node).OfType<AstFunction>().FirstOrDefault(function => function.TypeParameters.Any());
            if (genericOwner is not null && (DependsOnCallableParameter(value.Type, genericOwner)
                || DependsOnCallableParameter(expected, genericOwner)))
            { Defer(node); }
            else { Report("SOL2206", "Expression type is not assignable to the expected type.", node); }
        }
    }
}
