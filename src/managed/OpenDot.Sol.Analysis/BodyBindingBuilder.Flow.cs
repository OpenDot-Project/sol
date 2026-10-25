// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private Value Statements(IEnumerable<AstNode> statements, State state, Context context, bool resultContext)
    {
        var nodes = statements.ToArray();
        Value result = new(Empty);
        for (int index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            bool tail = resultContext && index == nodes.Length - 1;
            if (!state.Reachable) { Report("SOL2245", "Unreachable statement.", node); }
            switch (node)
            {
                case AstExpressionStatement expression:
                    result = Expression(expression.Expression, state, context, tail ? context.Result : null);
                    if (tail && context.Result is not null && context.Result != Empty) { Require(result, context.Result, node, state); }
                    if (tail && result.Type?.IsRefLike == true) { CheckEscape(result, node, context); }
                    break;
                case AstProperty property:
                    var type = property.Type is null ? null : Written(property.Type);
                    Value? value = property.Initializer is null ? null : Expression(property.Initializer, state, context, type);
                    type ??= value?.Type;
                    if (value is not null) { Require(value, type, property, state); }
                    foreach (var local in Sources.Lexical.Scopes.SelectMany(s => s.Symbols).Where(s => ReferenceEquals(s.Origin.Element, property.Origin.Element)))
                    {
                        if (type is not null) { _localTypes[local] = type; }
                        if (value is not null) { state.Assigned.Add(local); }
                    }
                    if (property.Delegate is not null)
                    {
                        var contract = Delegate(property);
                        if (contract is null) { Defer(property); }
                        else { state.Effects.UnionWith(contract.InitEffects); }
                    }
                    if (property.BindingNames.Count() != 1) { Defer(property); }
                    result = new(Empty);
                    break;
                case AstAssignment assignment:
                    Assignment(assignment, state, context); result = new(Empty); break;
                case AstReturn ret:
                    if (context.Cleanup) { Report("SOL2254", "Cleanup cannot transfer control outwards.", ret); }
                    if (context.Lambda is not null && (ret.Label is null || Ancestors(context.Lambda).OfType<AstLabelledExpression>().FirstOrDefault()?.Label != ret.Label))
                    { Report("SOL2225", "A lambda return must target its own explicit label.", ret); }
                    var returned = ret.Value is null ? new Value(Empty) : Expression(ret.Value, state, context, context.Result);
                    Require(returned, context.Result, ret, state);
                    if (returned.Type?.IsRefLike == true || ret.IsByReference) { CheckEscape(returned, ret, context); }
                    context.Exits?.Add(state.Clone());
                    state.Reachable = false; result = new(Types.GetBottom());
                    break;
                case AstIf conditional:
                    Require(Expression(conditional.Condition, state, context, Bool), Bool, conditional.Condition ?? node);
                    var left = state.Clone(); var right = state.Clone();
                    Refine(conditional.Condition, left, true); Refine(conditional.Condition, right, false);
                    if (conditional.Then is not null) { Statements(conditional.Then.Statements, left, context, false); }
                    if (conditional.Else is AstBlock alternative) { Statements(alternative.Statements, right, context, false); }
                    else if (conditional.Else is { } otherwise) { Statements([otherwise], right, context, false); }
                    state.Join(left, right); result = new(Empty); break;
                case AstBlock block:
                    result = Statements(block.Statements, state, context, tail); break;
                case AstFor loop:
                    Loop(loop, state, context); result = new(Empty); break;
                case AstSwitch selection:
                    Switch(selection, state, context); result = new(Empty); break;
                case AstTry attempt:
                    result = Try(attempt, state, context, tail ? context.Result : null); break;
                case AstFunction:
                    // A declaration's body is checked independently, not executed in this scope.
                    result = new(Empty); break;
                default:
                    if (node.SyntaxKind == NodeKind.ThrowStatement) { result = Throw(node, state, context); }
                    else if (node.SyntaxKind is NodeKind.BreakStatement or NodeKind.ContinueStatement)
                    {
                        if (context.LoopDepth == 0) { Report("SOL2246", "Loop control requires an enclosing loop.", node); }
                        if (context.Cleanup) { Report("SOL2254", "Cleanup cannot transfer control outwards.", node); }
                        if (node.SyntaxKind == NodeKind.BreakStatement) { context.Breaks?.Add(state.Clone()); }
                        state.Reachable = false; result = new(Types.GetBottom());
                    }
                    else if (node.SyntaxKind is NodeKind.UnsafeStatement or NodeKind.DeferStatement)
                    {
                        var nested = node.Children.OfType<AstBlock>().FirstOrDefault();
                        if (nested is not null)
                        {
                            // Cleanup execution at each normal/exceptional exit needs a later FIR proof.
                            var cleanup = state.Clone();
                            Statements(nested.Statements, cleanup, context with { Cleanup = node.SyntaxKind == NodeKind.DeferStatement }, false);
                            state.Effects.UnionWith(cleanup.Effects);
                            if (node.SyntaxKind == NodeKind.DeferStatement) { Defer(node); }
                        }
                        else { Defer(node); }
                    }
                    else { Defer(node); }
                    break;
            }
        }
        if (resultContext && state.Reachable && context.Result is { } expected && expected != Empty)
        {
            if (nodes.LastOrDefault() is not (AstExpressionStatement or AstBlock or AstTry))
            { Report("SOL2247", "A reachable normal path has no result value.", nodes.LastOrDefault() ?? (AstNode?)context.Callable ?? context.Lambda!); }
            else { Require(result, expected, nodes[^1], state); }
        }
        return result;
    }

    private void Assignment(AstAssignment assignment, State state, Context context)
    {
        bool compound = assignment.Operation != "=";
        var location = Expression(assignment.Location, state, context, read: compound);
        var value = Expression(assignment.Value, state, context, location.Type);
        var assignedValue = value;
        if (!location.Writable) { Report("SOL2241", "Assignment requires a writable location.", assignment); }
        if (compound && location.Type is not null && value.Type is not null)
        {
            string operation = assignment.Operation[..^1];
            var liftedCompound = LiftNullableBinary(assignment, operation, location, value, state, context);
            if (liftedCompound is not null) { assignedValue = liftedCompound; }
            else
            {
                bool builtin = IsNumeric(location.Type) && location.Type == value.Type
                    && operation is ("+" or "-" or "*" or "/" or "%")
                    || IsInteger(location.Type) && location.Type == value.Type && operation is ("&" or "|" or "^")
                    || IsInteger(location.Type) && value.Type == Types.GetPrimitive("int") && operation is ("<<" or ">>")
                    || location.Type == Bool && value.Type == Bool && operation is ("&" or "|" or "^")
                    || operation == "+" && location.Type == Core("String") && value.Type == location.Type;
                assignedValue = builtin ? new(location.Type) : OperatorCall(assignment, operation, [location, value], state, context);
            }
            Require(assignedValue, location.Type, assignment, state);
        }
        else { Require(value, location.Type, assignment, state); }
        if (location.Local is { } local)
        {
            state.Assigned.Add(local); state.Narrowed.Remove(local);
            if (assignedValue.Type?.IsRefLike == true && local.Kind != LocalSymbolKind.Variable) { CheckEscape(assignedValue, assignment, context); }
        }
        else if (assignedValue.Type?.IsRefLike == true) { Report("SOL2260", "Ref-like values cannot be stored in heap properties.", assignment); }
        if (location.Symbol?.Parts[0] is AstProperty property)
        {
            if (context.Callable is AstConstructor)
            {
                bool own = assignment.Location is AstMember member ? IsOwnConstructorReceiver(member.Receiver, context)
                    : assignment.Location is AstAtom;
                if (own) { state.Fields.Add(property); }
            }
            var setter = property.Accessors.FirstOrDefault(a => a.Name is "set" or "init");
            if (setter is not null) { state.Effects.UnionWith(setter.ThrownTypes.Select(t => Specialize(t, location.Receiver))); }
            if (property.Delegate is not null && Delegate(property) is { } contract) { state.Effects.UnionWith(contract.SetEffects); }
        }
    }

    private void Refine(AstExpression? condition, State state, bool positive)
    {
        if (condition is not AstOperation operation) { return; }
        var operands = operation.Operands.ToArray();
        if (operation.Operation == "!" && operands.Length == 1) { Refine(operands[0], state, !positive); return; }
        if ((operation.Operation == "&&" && positive || operation.Operation == "||" && !positive) && operands.Length == 2)
        { Refine(operands[0], state, positive); Refine(operands[1], state, positive); return; }
        if (operands.FirstOrDefault() is not AstAtom { SyntaxKind: NodeKind.NameExpression } atom) { return; }
        var locals = Sources.Lexical.Lookup(atom, atom.Spelling).Locals;
        if (locals is not [var local] || !state.Assigned.Contains(local)) { return; }
        bool captured = Sources.Lexical.Bindings.Any(b => b.CapturesOuterLocal && b.Lookup.Locals.Contains(local));
        if (local.IsMutable && captured) { return; }
        if (operation.TargetType is not null && operation.SyntaxKind == NodeKind.TypeTestExpression)
        {
            if (positive == (operation.Operation == "is")) { state.Narrowed[local] = Written(operation.TargetType); }
        }
        else if (operands.Length == 2 && operands[1] is AstAtom { Spelling: "null" }
            && (operation.Operation is "!=" or "!==" && positive || operation.Operation is "==" or "===" && !positive))
        {
            var type = LocalType(local);
            if (type?.IsNullable == true) { state.Narrowed[local] = type.Arguments[0]; }
        }
    }

    private void Loop(AstFor loop, State state, Context context)
    {
        if (loop.Initializer is { } initializer) { Statements([initializer], state, context, false); }
        if (loop.Condition is not null) { Require(Expression(loop.Condition, state, context, Bool), Bool, loop.Condition); }
        var body = state.Clone();
        // A backedge may change local values; only immutable facts survive entry to the loop.
        foreach (var local in body.Narrowed.Keys.Where(l => l.IsMutable).ToArray()) { body.Narrowed.Remove(local); }
        Refine(loop.Condition, body, true);
        if (loop.Iterable is not null)
        {
            var iterable = Expression(loop.Iterable, state, context);
            if (iterable.Type?.Kind is ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray)
            {
                foreach (var local in OwnLocals(loop).Where(l => l.Kind == LocalSymbolKind.Variable))
                { _localTypes[local] = iterable.Type.Arguments[0]; body.Assigned.Add(local); }
            }
            else { Defer(loop); }
        }
        var breaks = new List<State>();
        if (loop.Body is not null) { Statements(loop.Body.Statements, body, context with { LoopDepth = context.LoopDepth + 1, Breaks = breaks }, false); }
        if (loop.Update is not null && body.Reachable) { Assignment(loop.Update, body, context); }
        state.Effects.UnionWith(body.Effects);
        foreach (var local in state.Narrowed.Keys.Where(l => l.IsMutable).ToArray()) { state.Narrowed.Remove(local); }
        if (loop.Form == AstForForm.Infinite || loop.Condition is AstAtom { Spelling: "true" })
        {
            state.Reachable = false;
            foreach (var exit in breaks) { var before = state.Clone(); state.Join(before, exit); }
        }
    }

    private void Switch(AstSwitch selection, State state, Context context)
    {
        var value = Expression(selection.Value, state, context);
        var branches = new List<State>();
        foreach (var @case in selection.Cases)
        {
            var branch = state.Clone();
            if (@case.Pattern is AstExpression pattern) { Require(Expression(pattern, branch, context, value.Type), value.Type, pattern); }
            else if (!@case.IsDefault) { Defer(@case); }
            Statements(@case.Statements, branch, context, false); branches.Add(branch);
        }
        bool exhaustive = selection.Cases.Any(c => c.IsDefault)
            || value.Type == Bool && selection.Cases.Any(c => c.Pattern?.Origin.Text == "true") && selection.Cases.Any(c => c.Pattern?.Origin.Text == "false");
        if (!exhaustive) { branches.Add(state.Clone()); Defer(selection); }
        state.Reachable = false;
        foreach (var branch in branches) { var previous = state.Clone(); state.Join(previous, branch); }
    }

    private Value Throw(AstNode node, State state, Context context)
    {
        if (context.Cleanup) { Report("SOL2254", "Cleanup cannot throw outwards.", node); }
        var expression = node.Children.OfType<AstExpression>().FirstOrDefault();
        var value = Expression(expression, state, context);
        if (value.Type is { } type) { CheckThrowable(type, node); state.Effects.Add(type); }
        state.Reachable = false;
        return new(Types.GetBottom());
    }

    private Value Try(AstTry attempt, State state, Context context, ResolvedType? expected)
    {
        var incoming = state.Clone();
        var body = incoming.Clone(); body.Effects.Clear();
        var exits = new List<State>();
        var nested = context with { Exits = exits, Result = expected ?? context.Result };
        var value = attempt.Body is null ? new Value(null) : Statements(attempt.Body.Statements, body, nested, attempt.IsValue);
        var effects = new HashSet<ResolvedType>(body.Effects);
        foreach (var exit in exits) { effects.UnionWith(exit.Effects); }
        var alternatives = new List<State> { body };
        var catchTypes = new List<ResolvedType>();
        foreach (var handler in attempt.Catches)
        {
            var caught = handler.ExceptionType is null ? null : Written(handler.ExceptionType);
            if (caught is not null)
            {
                CheckThrowable(caught, handler);
                if (catchTypes.Any(t => Converts(caught, t))) { Report("SOL2252", "An earlier catch already handles this type.", handler); }
                catchTypes.Add(caught);
                effects.RemoveWhere(t => Converts(t, caught));
                foreach (var exit in exits) { exit.Effects.RemoveWhere(t => Converts(t, caught)); }
            }
            var branch = incoming.Clone(); branch.Effects.Clear();
            foreach (var local in OwnLocals(handler)) { branch.Assigned.Add(local); if (caught is not null) { _localTypes[local] = caught; } }
            var other = handler.Body is null ? new Value(null) : Statements(handler.Body.Statements, branch, nested, attempt.IsValue);
            if (attempt.IsValue) { value = Common(value, other, attempt); }
            alternatives.Add(branch);
        }
        foreach (var branch in alternatives.Skip(1)) { effects.UnionWith(branch.Effects); }
        state.Reachable = false;
        foreach (var branch in alternatives) { var previous = state.Clone(); state.Join(previous, branch); }
        state.Effects.Clear(); state.Effects.UnionWith(incoming.Effects); state.Effects.UnionWith(effects);
        if (attempt.Finally is { } cleanup)
        {
            var normal = state.Clone();
            normal.Reachable = true;
            Statements(cleanup.Statements, normal, context with { Cleanup = true }, false);
            state.Assigned.Clear(); state.Assigned.UnionWith(normal.Assigned);
            state.Fields.Clear(); state.Fields.UnionWith(normal.Fields);
            state.Narrowed.Clear(); state.Effects.UnionWith(normal.Effects);
            foreach (var exit in exits) { Statements(cleanup.Statements, exit, context with { Cleanup = true }, false); }
        }
        context.Exits?.AddRange(exits);
        return attempt.IsValue ? value : new(Empty);
    }

    private Value Common(Value left, Value right, AstNode node)
    {
        if (left.Type is null || right.Type is null) { Defer(node); return new(null); }
        if (Converts(left.Type, right.Type)) { return right; }
        if (Converts(right.Type, left.Type)) { return left; }
        Report("SOL2205", "Branches have incompatible result types.", node); return new(null);
    }

    private void CheckOut(State state, Context context, AstNode location)
    {
        if (context.Callable is null) { return; }
        foreach (var parameter in context.Callable.Parameters.Where(p => p.PassingMode == AstPassingMode.Out))
        {
            var local = OwnLocals(context.Callable).FirstOrDefault(l => ReferenceEquals(l.Origin.Element, parameter.NameOrigin.Element));
            if (local is not null && !state.Assigned.Contains(local)) { Report("SOL2242", "Out parameter must be assigned on every normal exit: " + parameter.Name, location); }
        }
    }

    private void CheckEscape(Value value, AstNode location, Context context)
    {
        // Until borrow origins are proven, returning any ref-like/ref value remains a proof gap.
        // A known local address is definitely invalid; a caller-owned borrow needs finer origin data.
        if (value.Local?.Kind == LocalSymbolKind.Variable && location is AstReturn { IsByReference: true })
        { Report("SOL2261", "A local borrow cannot escape its defining scope.", location); }
        else { Defer(location); }
    }

    private void ValidateStorage()
    {
        foreach (var property in Files.Keys.OfType<AstProperty>())
        {
            if (Ancestors(property).Any(n => n is AstCallable or AstLambda)) { continue; }
            var type = property.Type is null ? null : Written(property.Type);
            if (type is not null && MayContainBorrow(type) && ContainingType(property)?.IsRefLike != true)
            { Report("SOL2260", "Heap or ordinary value storage cannot contain a ref-like field.", property); }
        }
        foreach (var type in Files.Keys.OfType<AstTypeDeclaration>())
        {
            if (type.Modifiers.Contains("data") || type.Bases.Any(b => b.Children.Any(c => c.SyntaxKind == NodeKind.Delegation))) { Defer(type); }
        }
        foreach (var initializer in Files.Keys.OfType<AstInitializer>()) { Defer(initializer); }
        foreach (var syntax in Files.Keys.OfType<AstType>())
        {
            var type = Written(syntax);
            if (type.Kind == ResolvedTypeKind.Array && MayContainBorrow(type.Arguments[0]))
            { Report("SOL2260", "A managed array cannot contain potentially ref-like generic values.", syntax); }
        }
    }

    private static bool MayContainBorrow(ResolvedType type)
    {
        var pending = new Stack<ResolvedType>(); var visited = new HashSet<ResolvedType>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) { continue; }
            if (current.IsRefLike) { return true; }
            if (current.Parameter is { } identity && identity.Owner.Children.Where(n => n.SyntaxKind == NodeKind.WhereClause)
                .SelectMany(n => n.Children).Any(c => c.Children.Any(n => n.SyntaxKind == NodeKind.Name && n.Origin.Text == current.Name)
                    && c.Children.Any(n => n.SyntaxKind == NodeKind.AntiConstraint))) { return true; }
            if (current.Kind is ResolvedTypeKind.Tuple or ResolvedTypeKind.Nullable or ResolvedTypeKind.FixedArray)
            { foreach (var child in current.Arguments) { pending.Push(child); } }
        }
        return false;
    }
}
