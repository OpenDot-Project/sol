// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private FirExpression Expression(AstExpression expression)
    {
        _cancellation.ThrowIfCancellationRequested();
        var binding = BoundExpression(expression);
        var type = binding?.Type is { } boundType ? Specialize(boundType) : null;
        if (type is null || binding!.Status != BodyBindingStatus.Bound) { return Invalid(expression); }
        switch (expression)
        {
            case AstAtom { SyntaxKind: NodeKind.PositionalExpression } atom:
                if (_activeLambda is { Form: AstLambdaForm.Positional }
                    && int.TryParse(atom.Spelling.AsSpan(1), out int position)
                    && (uint)position < (uint)_activeLambdaParameters.Length)
                {
                    var parameter = _activeLambdaParameters[position];
                    return new FirRead(parameter.Id, parameter.Type, atom.Origin);
                }
                return Invalid(atom);
            case AstAtom { SyntaxKind: NodeKind.LiteralExpression } atom:
                return new FirConstant(atom.Spelling, type, atom.Origin);
            case AstAtom { SyntaxKind: NodeKind.NameExpression } atom:
                if (binding.FunctionValueTarget is not null)
                {
                    return FunctionReference(atom, binding.FunctionValueTarget, type);
                }
                var locals = _analysis.Sources.Lexical.Lookup(atom, atom.Spelling).Locals;
                if (locals is [var capturedParameter] && _callValueReferences is { } references
                    && references.TryGetValue(capturedParameter, out int evaluationIndex))
                { return new FirCallValueReference(evaluationIndex, type, atom.Origin); }
                if (locals is [var local] && _slots.TryGetValue(local, out var slot)) { return new FirRead(slot.Id, slot.Type, atom.Origin); }
                return Invalid(atom);
            case AstMember member when binding.FunctionValueTarget is { } functionTarget:
                return FunctionReference(member, functionTarget, type);
            case AstLambda lambda:
                return LambdaReference(lambda, type);
            case AstSequence { IsGrouping: true } grouping when grouping.Elements.SingleOrDefault() is { } inner:
                return Expression(inner);
            case AstSequence { SyntaxKind: NodeKind.ArrayExpression } array:
                return new FirArrayLiteral(array.Elements.OfType<AstExpression>().Select(Expression).ToImmutableArray(), type, array.Origin);
            case AstSequence { SyntaxKind: NodeKind.TupleExpression } empty when !empty.Elements.Any():
                return new FirConstant("()", Empty, empty.Origin);
            case AstOperation operation:
                var values = operation.Operands.ToArray();
                if (operation.Operation == "-" && values is [AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal])
                { return new FirConstant("-" + literal.Spelling, type, operation.Origin); }
                if (operation.TargetType is not null || operation.Operation is "++" or "--" or "!!" or "?:") { return Invalid(operation); }
                if (BoundOperator(operation) is { } selectedOperator)
                {
                    if (!IsSupportedOperator(operation.Operation, values.Length)
                        || selectedOperator.Operator != operation.Operation
                        || values.Any(value => value is not AstExpression))
                    { Unsupported(operation, "Operator syntax is outside the verified direct-call subset"); return Invalid(operation); }
                    var operands = values.Cast<AstExpression>().Select(Expression).ToArray();
                    return LowerOperator(operation, operation.Operation, operands, type, operation.Origin) ?? Invalid(operation);
                }
                if (values.Length == 1) { return new FirUnary(operation.Operation, Expression(values[0]), type, operation.Origin); }
                if (values.Length == 2) { return new FirBinary(operation.Operation, Expression(values[0]), Expression(values[1]), type, operation.Origin); }
                return Invalid(operation);
            case AstMember { Name: "length", Receiver: not null, IsSafe: false } member:
                return new FirArrayLength(Expression(member.Receiver), type, member.Origin);
            case AstCall call:
                if (BoundCall(call) is not { } selected)
                {
                    if (call.Target is { } callable && BoundExpression(callable)?.Type is { Kind: ResolvedTypeKind.Function } functionType)
                    { return IndirectCall(call, callable, Specialize(functionType), type); }
                    return Invalid(call);
                }
                if (call.Target is AstMember { IsSafe: true }) { return Invalid(call); }
                if (selected.Target is AstFunction intrinsicTarget && _catalog.Intrinsic(intrinsicTarget) is { } intrinsic)
                {
                    var inputs = ImmutableArray.CreateBuilder<FirExpression>();
                    var order = ImmutableArray.CreateBuilder<int>();
                    if (intrinsic != FirIntrinsicKind.GcCollect && call.Target is AstMember { Receiver: not null } method)
                    { inputs.Add(Expression(method.Receiver)); order.Add(0); }
                    int receiverCount = inputs.Count;
                    foreach (var mapping in selected.Arguments)
                    {
                        var argument = call.Arguments[mapping.ArgumentIndex];
                        if (argument.Value is not AstExpression input || mapping.IsArrayArgument || mapping.IsSpread) { return Invalid(call); }
                        inputs.Add(Expression(input)); order.Add(mapping.ParameterIndex + receiverCount);
                    }
                    return new FirIntrinsic(intrinsic, inputs.ToImmutable(), type, call.Origin) { ParameterOrder = order.ToImmutable() };
                }
                var typeArguments = selected.TypeArguments.Select(Specialize).ToImmutableArray();
                if (selected.Target is AstFunction genericMethod && genericMethod.TypeParameters.Any()
                    && GenericMethodSourcePolicy.IsSupportedGenericObjectMethod(genericMethod, _analysis.Sources)
                    && !IsSupportedGenericMethodCall(call, genericMethod, selected))
                { Unsupported(call, "Generic object method receiver is not a direct, verified singleton reference"); return Invalid(call); }
                int target = SpecializationTarget(selected.Target, typeArguments, call);
                if (target < 0) { return Invalid(call); }
                if (!selected.DefaultParameters.IsEmpty)
                { return CallWithDefaults(call, selected, target, typeArguments, type); }
                var arguments = ImmutableArray.CreateBuilder<FirArgument>();
                bool runtimeVararg = selected.Target is AstFunction { Body.Kind: AstBodyKind.Missing } runtimeTarget
                    && _catalog.Owns(runtimeTarget);
                var handledVarargs = new System.Collections.Generic.HashSet<int>();
                foreach (var mapping in selected.Arguments)
                {
                    var parameter = selected.Target.Parameters[mapping.ParameterIndex];
                    if (parameter.PassingMode == AstPassingMode.Vararg && !runtimeVararg)
                    {
                        if (!handledVarargs.Add(mapping.ParameterIndex)) { continue; }
                        var group = selected.Arguments.Where(candidate => candidate.ParameterIndex == mapping.ParameterIndex).ToArray();
                        if (group.Length == 1 && (group[0].IsArrayArgument || group[0].IsSpread))
                        {
                            var sourceArgument = call.Arguments[group[0].ArgumentIndex];
                            if (sourceArgument.Value is not AstExpression arrayExpression) { return Invalid(call); }
                            arguments.Add(new(mapping.ParameterIndex, AstPassingMode.Value, Expression(arrayExpression)));
                            continue;
                        }
                        if (group.Any(candidate => !candidate.IsExpandedVararg || candidate.IsArrayArgument || candidate.IsSpread))
                        {
                            Unsupported(call, "Mixed scalar and array vararg arguments");
                            return new FirConstant("()", Empty, call.Origin);
                        }
                        var elementType = Specialize(selected.ParameterTypes[mapping.ParameterIndex]);
                        var elements = ImmutableArray.CreateBuilder<FirExpression>(group.Length);
                        foreach (var element in group)
                        {
                            if (call.Arguments[element.ArgumentIndex].Value is not AstExpression elementExpression)
                            { return Invalid(call); }
                            elements.Add(Expression(elementExpression));
                        }
                        var arrayType = _analysis.Types.GetArray(elementType);
                        arguments.Add(new(mapping.ParameterIndex, AstPassingMode.Value,
                            new FirArrayLiteral(elements.ToImmutable(), arrayType, call.Origin)));
                        continue;
                    }
                    var argument = call.Arguments[mapping.ArgumentIndex];
                    if (argument.Value is not AstExpression argumentExpression || mapping.IsArrayArgument || mapping.IsSpread && !runtimeVararg) { return Invalid(call); }
                    var mode = parameter.PassingMode;
                    arguments.Add(new(mapping.ParameterIndex, mode == AstPassingMode.Vararg ? AstPassingMode.Value : mode, Expression(argumentExpression)));
                }
                if (!runtimeVararg)
                {
                    foreach (int parameterIndex in selected.EmptyVarargParameters)
                    {
                        if (parameterIndex < 0 || parameterIndex >= selected.Target.Parameters.Length
                            || parameterIndex >= selected.ParameterTypes.Length || handledVarargs.Contains(parameterIndex)
                            || selected.Target.Parameters[parameterIndex].PassingMode != AstPassingMode.Vararg)
                        { return Invalid(call); }
                        var elementType = Specialize(selected.ParameterTypes[parameterIndex]);
                        var arrayType = _analysis.Types.GetArray(elementType);
                        arguments.Add(new(parameterIndex, AstPassingMode.Value,
                            new FirArrayLiteral([], arrayType, call.Origin)));
                    }
                }
                return new FirCall(target, arguments.ToImmutable(), type, call.Origin)
                { TypeArguments = selected.Target is AstFunction { TypeParameters: var typeParameters } && typeParameters.Any() ? typeArguments : [] };
            default:
                if (expression.SyntaxKind == NodeKind.IndexExpression)
                {
                    var receiver = expression.Children.OfType<AstExpression>().FirstOrDefault();
                    var indices = expression.Children.Where(n => n.SyntaxKind == NodeKind.ArgumentList).SelectMany(n => n.Children)
                        .SelectMany(n => n is AstExpression e ? new[] { e } : n.Children.OfType<AstExpression>()).ToArray();
                    if (receiver is not null && indices is [var index])
                    { return new FirArrayIndex(Expression(receiver), Expression(index), type, expression.Origin); }
                }
                return Invalid(expression);
        }
    }

    private FirBlock Block(AstBlock block, bool functionTail = false)
    {
        var statements = ImmutableArray.CreateBuilder<FirStatement>();
        for (int index = 0; index < block.Statements.Length; index++)
        {
            var node = block.Statements[index];
            bool tail = functionTail && index == block.Statements.Length - 1
                && BoundBody(_current)?.ReturnType != Empty;
            if (tail && node is AstExpressionStatement { Expression: not null } expression)
            { statements.Add(new FirReturn(Expression(expression.Expression), node.Origin)); }
            else if (Statement(node) is { } statement) { statements.Add(statement); }
        }
        return new(statements.ToImmutable(), block.Origin);
    }

    private FirStatement? Statement(AstNode node)
    {
        switch (node)
        {
            case AstBlock block: return Block(block);
            case AstExpressionStatement { Expression: not null } expression: return new FirEvaluate(Expression(expression.Expression), node.Origin);
            case AstProperty property:
                var local = _analysis.Sources.Lexical.Scopes.SelectMany(s => s.Symbols).FirstOrDefault(s => ReferenceEquals(s.Origin.Element, property.Origin.Element));
                var initializer = property.Initializer is null ? null : Expression(property.Initializer);
                var type = property.Type is null ? initializer?.Type : Specialize(_analysis.Sources.Types.GetType(property.Type));
                if (local is null || type is null || property.BindingNames.Count() != 1 || property.Delegate is not null) { Unsupported(node, "Local binding"); return null; }
                var slot = new FirSlot(_slots.Count, local.Name, type, local.IsMutable, AstPassingMode.Value, false, property.Origin);
                _slots[local] = slot;
                return new FirDeclare(slot.Id, initializer, node.Origin);
            case AstAssignment assignment when assignment.Location is AstAtom location && assignment.Value is not null:
                var candidates = _analysis.Sources.Lexical.Lookup(location, location.Spelling).Locals;
                if (candidates is not [var assigned] || !_slots.TryGetValue(assigned, out var stored)) { Unsupported(node, "Assignment location"); return null; }
                var value = Expression(assignment.Value);
                if (assignment.Operation != "=")
                {
                    string operation = assignment.Operation[..^1];
                    if (BoundOperator(assignment) is { } selectedOperator)
                    {
                        if (selectedOperator.Operator != operation || !IsSupportedOperator(operation, 2))
                        { Unsupported(assignment, "Compound operator syntax is outside the verified direct-call subset"); return null; }
                        value = LowerOperator(assignment, operation,
                            [new FirRead(stored.Id, stored.Type, location.Origin), value], stored.Type, node.Origin)
                            ?? Invalid(assignment);
                    }
                    else
                    { value = new FirBinary(operation, new FirRead(stored.Id, stored.Type, location.Origin), value, stored.Type, node.Origin); }
                }
                return new FirStore(stored.Id, value, node.Origin);
            case AstReturn ret:
                if (ret.Label is not null || ret.IsByReference) { Unsupported(ret, "Labelled/ref return"); }
                return new FirReturn(ret.Value is null ? null : Expression(ret.Value), node.Origin);
            case AstIf { Condition: not null, Then: not null } conditional:
                var alternative = conditional.Else is AstBlock other ? Block(other)
                    : conditional.Else is { } nested && Statement(nested) is { } nestedStatement ? new FirBlock([nestedStatement], nested.Origin) : null;
                return new FirIf(Expression(conditional.Condition), Block(conditional.Then), alternative, node.Origin);
            case AstFor { Body: not null } loop:
                if (loop.Form == AstForForm.Each) { return ForEach(loop); }
                return new FirLoop(loop.Initializer is null ? null : Statement(loop.Initializer), loop.Condition is null ? null : Expression(loop.Condition),
                    Block(loop.Body), loop.Update is null ? null : Statement(loop.Update), node.Origin);
            default:
                if (node.SyntaxKind == NodeKind.BreakStatement) { return new FirBreak(node.Origin); }
                if (node.SyntaxKind == NodeKind.ContinueStatement) { return new FirContinue(node.Origin); }
                Unsupported(node, "Statement shape"); return null;
        }
    }
}
