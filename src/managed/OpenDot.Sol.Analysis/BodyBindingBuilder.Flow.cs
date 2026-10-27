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
    private Value StatementsCore(IEnumerable<AstNode> statements, State state, Context context, bool resultContext)
    {
        var nodes = statements.ToArray();
        Value result = new(Empty);
        for (int index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            bool tail = resultContext && index == nodes.Length - 1;
            if (!state.Reachable) { Report("SOL2245", "Unreachable statement.", node); }
            var cleanupFactsBefore = CaptureCleanupFacts(state);
            switch (node)
            {
                case AstExpressionStatement expression:
                    result = Expression(expression.Expression, state, context,
                        tail && context.Result != Empty ? context.Result : null);
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
                        var contract = Delegate(property, state, context);
                        if (contract is null) { Defer(property); }
                        else
                        {
                            state.Effects.UnionWith(contract.InitEffects);
                            foreach (var local in Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols)
                                .Where(symbol => ReferenceEquals(symbol.Origin.Element, property.Origin.Element)))
                            { state.Assigned.Add(local); }
                        }
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
                    if (ret.Value is not null && ContainsCall(ret.Value)) { InvalidateMutableCleanupNarrowings(state); }
                    UpdateCleanupFacts(state, cleanupFactsBefore, CaptureCleanupFacts(state));
                    context.Exits?.Add(state.Clone());
                    state.Reachable = false; result = new(Types.GetBottom());
                    break;
                case AstIf conditional:
                    Require(Expression(conditional.Condition, state, context, Bool), Bool, conditional.Condition ?? node);
                    UpdateCleanupFacts(state, cleanupFactsBefore, CaptureCleanupFacts(state));
                    if (conditional.Condition is not null && ContainsCall(conditional.Condition))
                    { InvalidateMutableCleanupNarrowings(state); }
                    var left = state.Clone(); var right = state.Clone();
                    var leftFacts = CaptureCleanupFacts(left);
                    var rightFacts = CaptureCleanupFacts(right);
                    Refine(conditional.Condition, left, true); Refine(conditional.Condition, right, false);
                    UpdateCleanupFacts(left, leftFacts, CaptureCleanupFacts(left));
                    UpdateCleanupFacts(right, rightFacts, CaptureCleanupFacts(right));
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
                        if (context.Cleanup && (context.CleanupLoopDepth is not { } cleanupLoopDepth
                            || context.LoopDepth <= cleanupLoopDepth))
                        { Report("SOL2254", "Cleanup cannot transfer control outwards.", node); }
                        if (node.SyntaxKind == NodeKind.BreakStatement) { context.Breaks?.Add(state.Clone()); }
                        else { context.Continues?.Add(state.Clone()); }
                        state.Reachable = false; result = new(Types.GetBottom());
                    }
                    else if (node.SyntaxKind == NodeKind.UnsafeStatement)
                    {
                        var nested = node.Children.OfType<AstBlock>().FirstOrDefault();
                        if (nested is not null)
                        {
                            Statements(nested.Statements, state, context, false);
                        }
                        else { Defer(node); }
                    }
                    else if (node.SyntaxKind == NodeKind.DeferStatement)
                    {
                        var nested = node.Children.OfType<AstBlock>().FirstOrDefault();
                        if (nested is not null) { RegisterDefer(node, nested, state, context); }
                        else { Defer(node); }
                    }
                    else { Defer(node); }
                    break;
            }
            if (node is not (AstIf or AstBlock or AstFor or AstSwitch or AstTry or AstReturn)
                && node.SyntaxKind is not (NodeKind.ThrowStatement or NodeKind.BreakStatement or NodeKind.ContinueStatement or NodeKind.UnsafeStatement))
            {
                UpdateCleanupFacts(state, cleanupFactsBefore, CaptureCleanupFacts(state));
                if (node is AstAssignment || (node.SyntaxKind != NodeKind.DeferStatement && ContainsCall(node)))
                { InvalidateMutableCleanupNarrowings(state); }
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
            var liftedCompound = LiftNullableBinary(assignment, operation, location, value, state, context,
                location.Type);
            if (liftedCompound is not null) { assignedValue = liftedCompound; }
            else
            {
                bool builtin = IsNumeric(location.Type) && location.Type == value.Type
                    && operation is ("+" or "-" or "*" or "/" or "%")
                    || IsInteger(location.Type) && location.Type == value.Type && operation is ("&" or "|" or "^")
                    || IsInteger(location.Type) && value.Type == Types.GetPrimitive("int") && operation is ("<<" or ">>")
                    || location.Type == Bool && value.Type == Bool && operation is ("&" or "|" or "^")
                    || operation == "+" && location.Type == Core("String") && value.Type == location.Type;
                assignedValue = builtin ? new(location.Type)
                    : OperatorCall(assignment, operation, [location, value], state, context, expected: location.Type);
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
            bool initializesOwnField = false;
            if (context.Callable is AstConstructor)
            {
                initializesOwnField = assignment.Location is AstMember member ? IsOwnConstructorReceiver(member.Receiver, context)
                    : assignment.Location is AstAtom;
            }
            var setter = property.Accessors.FirstOrDefault(a => a.Name is "set" or "init");
            if (setter is not null)
            {
                foreach (var effect in setter.ThrownTypes.Select(t => Specialize(t, location.Receiver)))
                { state.RecordExceptionalEdge(effect); state.Effects.Add(effect); }
                if (Core("RuntimeException") is { } runtime)
                { state.RecordExceptionalEdge(runtime); state.Effects.Add(runtime); }
            }
            if (property.Delegate is not null && Delegate(property) is { } contract)
            {
                foreach (var effect in contract.SetEffects) { state.RecordExceptionalEdge(effect); state.Effects.Add(effect); }
                if (Core("RuntimeException") is { } runtime)
                { state.RecordExceptionalEdge(runtime); state.Effects.Add(runtime); }
            }
            if (initializesOwnField) { state.Fields.Add(property); }
        }
    }

    private void Refine(AstExpression? condition, State state, bool positive)
    {
        if (condition is not AstOperation operation) { return; }
        var operands = operation.Operands.ToArray();
        if (operation.Operation == "!" && operands.Length == 1) { Refine(operands[0], state, !positive); return; }
        if ((operation.Operation == "&&" && positive || operation.Operation == "||" && !positive) && operands.Length == 2)
        { Refine(operands[0], state, positive); Refine(operands[1], state, positive); return; }
        AstAtom? atom = operands.FirstOrDefault() as AstAtom;
        bool comparesWithNull = operands.Length == 2 && operands[1] is AstAtom { Spelling: "null" }
            && operands[0] is AstAtom { SyntaxKind: NodeKind.NameExpression }
            || operands.Length == 2 && operands[0] is AstAtom { Spelling: "null" }
            && operands[1] is AstAtom { SyntaxKind: NodeKind.NameExpression };
        if (comparesWithNull)
        {
            atom = operands[0] is AstAtom { Spelling: "null" } ? operands[1] as AstAtom : operands[0] as AstAtom;
        }
        if (atom is not { SyntaxKind: NodeKind.NameExpression }) { return; }
        var locals = Sources.Lexical.Lookup(atom, atom.Spelling).Locals;
        if (locals is not [var local] || !state.Assigned.Contains(local)) { return; }
        bool captured = Sources.Lexical.Bindings.Any(b => b.CapturesOuterLocal && b.Lookup.Locals.Contains(local));
        if (local.IsMutable && captured) { return; }
        if (operation.TargetType is not null && operation.SyntaxKind == NodeKind.TypeTestExpression)
        {
            if (positive == (operation.Operation == "is")) { state.Narrowed[local] = Written(operation.TargetType); }
        }
        else if (comparesWithNull
            && (operation.Operation is "!=" or "!==" && positive || operation.Operation is "==" or "===" && !positive))
        {
            var type = LocalType(local);
            if (type?.IsNullable == true) { state.Narrowed[local] = type.Arguments[0]; }
        }
    }

    private void Loop(AstFor loop, State state, Context context)
    {
        if (loop.Initializer is { } initializer) { Statements([initializer], state, context, false); }
        if (loop.Condition is { } condition)
        {
            var beforeCondition = CaptureCleanupFacts(state);
            Require(Expression(condition, state, context, Bool), Bool, condition);
            UpdateCleanupFacts(state, beforeCondition, CaptureCleanupFacts(state));
            if (ContainsCall(condition)) { InvalidateMutableCleanupNarrowings(state); }
        }
        bool bodyMayExecute = state.Reachable && loop.Condition is not AstAtom { Spelling: "false" };
        bool emptyArrayLiteral = false;
        ResolvedType? iterationElementType = null;
        if (loop.Iterable is { } iterableExpression)
        {
            var beforeIterable = CaptureCleanupFacts(state);
            var iterable = Expression(iterableExpression, state, context);
            var afterIterable = CaptureCleanupFacts(state);
            UpdateCleanupFacts(state, beforeIterable, afterIterable);
            if (ContainsCall(iterableExpression))
            {
                InvalidateMutableCleanupNarrowings(state);
            }
            // A bottom-valued/throwing source expression cannot enter the iteration body.
            bodyMayExecute &= state.Reachable;
            emptyArrayLiteral = IsEmptyArrayLiteral(iterableExpression);
            if (emptyArrayLiteral) { bodyMayExecute = false; }
            if (iterable.Type is { Kind: ResolvedTypeKind.Array or ResolvedTypeKind.FixedArray } iterableType)
            {
                var elementType = iterableType.Arguments[0];
                iterationElementType = elementType;
                foreach (var local in OwnLocals(loop).Where(l => l.Kind == LocalSymbolKind.Variable))
                { _localTypes[local] = elementType; }
            }
            else { Defer(loop); }
        }
        // The iterable is evaluated once before the loop; its assigned/type facts are visible in the body.
        var body = state.Clone();
        // A backedge may change local values; only immutable facts survive entry to the loop.
        if (bodyMayExecute)
        {
            var beforeNarrowing = CaptureCleanupFacts(body);
            foreach (var local in body.Narrowed.Keys.Where(l => l.IsMutable).ToArray()) { body.Narrowed.Remove(local); }
            UpdateCleanupFacts(body, beforeNarrowing, CaptureCleanupFacts(body));
            InvalidateMutableCleanupNarrowings(body);
        }
        var beforeRefinement = CaptureCleanupFacts(body);
        Refine(loop.Condition, body, true);
        UpdateCleanupFacts(body, beforeRefinement, CaptureCleanupFacts(body));
        if (loop.Form == AstForForm.Each)
        {
            var beforeIterationVariable = CaptureCleanupFacts(body);
            foreach (var local in OwnLocals(loop).Where(l => l.Kind == LocalSymbolKind.Variable))
            {
                if (iterationElementType is not null || emptyArrayLiteral) { body.Assigned.Add(local); }
            }
            UpdateCleanupFacts(body, beforeIterationVariable, CaptureCleanupFacts(body));
        }
        var breaks = new List<State>();
        var continues = new List<State>();
        // Keep a non-executable body available for diagnostics without exporting its flow edges.
        var bodyExits = bodyMayExecute ? context.Exits : new List<State>();
        if (!bodyMayExecute) { body.ExceptionSink = null; }
        if (loop.Body is not null)
        {
            Statements(loop.Body.Statements, body, context with
            {
                LoopDepth = context.LoopDepth + 1,
                Breaks = breaks,
                Continues = continues,
                Exits = bodyExits,
            }, false);
        }
        if (loop.Update is not null && body.Reachable)
        {
            var beforeUpdate = CaptureCleanupFacts(body);
            Assignment(loop.Update, body, context);
            UpdateCleanupFacts(body, beforeUpdate, CaptureCleanupFacts(body));
            InvalidateMutableCleanupNarrowings(body);
        }
        if (loop.Update is not null)
        {
            foreach (var continued in continues.Where(exit => exit.Reachable))
            {
                var beforeUpdate = CaptureCleanupFacts(continued);
                Assignment(loop.Update, continued, context);
                UpdateCleanupFacts(continued, beforeUpdate, CaptureCleanupFacts(continued));
                InvalidateMutableCleanupNarrowings(continued);
            }
        }
        if (bodyMayExecute)
        {
            state.Effects.UnionWith(body.Effects);
            foreach (var exit in breaks.Concat(continues)) { state.Effects.UnionWith(exit.Effects); }
            foreach (var local in state.Narrowed.Keys.Where(l => l.IsMutable).ToArray()) { state.Narrowed.Remove(local); }
        }
        if (loop.Form == AstForForm.Infinite || loop.Condition is AstAtom { Spelling: "true" })
        {
            state.Reachable = false;
            foreach (var exit in breaks) { var before = state.Clone(); state.Join(before, exit); }
        }
    }

    private static bool IsEmptyArrayLiteral(AstExpression expression)
    {
        while (expression is AstSequence { IsGrouping: true } grouping)
        {
            var elements = grouping.Elements.Take(2).ToArray();
            if (elements.Length != 1) { return false; }
            expression = elements[0];
        }
        return expression is AstSequence { SyntaxKind: NodeKind.ArrayExpression } array && !array.Elements.Any();
    }

    private void Switch(AstSwitch selection, State state, Context context)
    {
        var beforeValue = CaptureCleanupFacts(state);
        var value = Expression(selection.Value, state, context);
        UpdateCleanupFacts(state, beforeValue, CaptureCleanupFacts(state));
        if (selection.Value is not null && ContainsCall(selection.Value))
        { InvalidateMutableCleanupNarrowings(state); }
        var branches = new List<State>();
        foreach (var @case in selection.Cases)
        {
            var branch = state.Clone();
            if (@case.Pattern is AstExpression pattern)
            {
                var beforePattern = CaptureCleanupFacts(branch);
                Require(Expression(pattern, branch, context, value.Type), value.Type, pattern);
                UpdateCleanupFacts(branch, beforePattern, CaptureCleanupFacts(branch));
                if (ContainsCall(pattern)) { InvalidateMutableCleanupNarrowings(branch); }
            }
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
        var expression = node.Children.OfType<AstExpression>().FirstOrDefault();
        var beforeExpression = CaptureCleanupFacts(state);
        var value = Expression(expression, state, context);
        if (expression is not null && ContainsCall(expression)) { InvalidateMutableCleanupNarrowings(state); }
        UpdateCleanupFacts(state, beforeExpression, CaptureCleanupFacts(state));
        if (value.Type is { } type)
        {
            CheckThrowable(type, node);
            state.RecordExceptionalEdge(type, origin: node, fromCleanup: context.Cleanup);
            state.Effects.Add(type);
        }
        state.Reachable = false;
        return new(Types.GetBottom());
    }

    private Value Try(AstTry attempt, State state, Context context, ResolvedType? expected)
    {
        var incoming = state.Clone();
        var outerExceptionSink = incoming.ExceptionSink;
        int breakStart = context.Breaks?.Count ?? 0;
        int continueStart = context.Continues?.Count ?? 0;
        var exceptionEdges = new List<State.ExceptionEdge>();
        var body = incoming.Clone(); body.Effects.Clear(); body.ExceptionSink = exceptionEdges;
        var exits = new List<State>();
        var nested = context with { Exits = exits, Result = expected ?? context.Result };
        var value = attempt.Body is null ? new Value(null) : Statements(attempt.Body.Statements, body, nested, attempt.IsValue);
        var effects = new HashSet<ResolvedType>(body.Effects);
        foreach (var exit in exits) { effects.UnionWith(exit.Effects); }
        var bodyExits = exits.ToArray();
        AddUnknownRuntimeEdge(attempt.Body, incoming, body, exceptionEdges);
        var alternatives = new List<State> { body };
        var catchTypes = new List<ResolvedType>();
        bool priorCatchAll = false;
        var unhandledEdges = exceptionEdges.ToList();
        var handlerExceptionEdges = new List<State.ExceptionEdge>();
        foreach (var handler in attempt.Catches)
        {
            var caught = handler.ExceptionType is null ? null : Written(handler.ExceptionType);
            bool alreadyCovered = priorCatchAll || caught is not null && catchTypes.Any(t => Converts(caught, t));
            if (alreadyCovered) { Report("SOL2252", "An earlier catch already handles this type.", handler); }
            if (caught is not null)
            {
                CheckThrowable(caught, handler);
                catchTypes.Add(caught);
                effects.RemoveWhere(t => Converts(t, caught));
                foreach (var exit in bodyExits) { exit.Effects.RemoveWhere(t => Converts(t, caught)); }
            }
            else { priorCatchAll = true; }

            var matchingEdges = unhandledEdges.Where(edge => caught is null || MayOverlap(edge.Type, caught)).ToArray();
            bool reachable = matchingEdges.Length != 0;
            if (!reachable && !alreadyCovered)
            { Report("SOL2255", "Catch clause cannot match an exception from this try body.", handler); }
            State branch;
            if (reachable)
            {
                branch = matchingEdges[0].State.Clone();
                for (int index = 1; index < matchingEdges.Length; index++)
                {
                    var previous = branch.Clone();
                    branch.Join(previous, matchingEdges[index].State);
                }
                branch.Effects.Clear();
                branch.ExceptionSink = handlerExceptionEdges;
            }
            else
            {
                branch = incoming.Clone();
                branch.Reachable = false;
                branch.Effects.Clear();
                branch.ExceptionSink = null;
            }
            var branchFacts = CaptureCleanupFacts(branch);
            foreach (var local in OwnLocals(handler)) { branch.Assigned.Add(local); if (caught is not null) { _localTypes[local] = caught; } }
            UpdateCleanupFacts(branch, branchFacts, CaptureCleanupFacts(branch));
            var other = handler.Body is null ? new Value(null)
                : Statements(handler.Body.Statements, branch, reachable ? nested : nested with { Exits = null }, attempt.IsValue);
            if (reachable)
            {
                if (attempt.IsValue) { value = Common(value, other, attempt); }
                alternatives.Add(branch);
            }

            if (caught is null)
            {
                unhandledEdges.Clear();
            }
            else
            {
                unhandledEdges.RemoveAll(edge => Converts(edge.Type, caught));
            }
        }
        var escapingEdges = unhandledEdges.Concat(handlerExceptionEdges).ToArray();
        foreach (var branch in alternatives.Skip(1)) { effects.UnionWith(branch.Effects); }
        state.Reachable = false;
        foreach (var branch in alternatives) { var previous = state.Clone(); state.Join(previous, branch); }
        state.Effects.Clear(); state.Effects.UnionWith(incoming.Effects); state.Effects.UnionWith(effects);
        state.ExceptionSink = outerExceptionSink;
        if (attempt.Finally is { } cleanup)
        {
            var cleanupContext = context with
            {
                Cleanup = true,
                CleanupLoopDepth = context.LoopDepth,
                CleanupBoundary = true,
                Exits = null,
                Breaks = null,
                Continues = null,
            };
            if (state.Reachable)
            {
                var normal = state.Clone();
                Statements(cleanup.Statements, normal, cleanupContext, false);
                CopyState(state, normal);
            }
            foreach (var exit in exits)
            {
                if (!exit.Reachable) { continue; }
                exit.ExceptionSink = outerExceptionSink;
                exit.Reachable = true;
                Statements(cleanup.Statements, exit, cleanupContext, false);
            }
            ApplyFinallyTransfers(context.Breaks, breakStart, cleanup, cleanupContext, outerExceptionSink);
            ApplyFinallyTransfers(context.Continues, continueStart, cleanup, cleanupContext, outerExceptionSink);
            foreach (var edge in escapingEdges)
            {
                var exceptional = edge.State.Clone();
                exceptional.ExceptionSink = outerExceptionSink;
                exceptional.Reachable = true;
                exceptional.Effects.Clear();
                Statements(cleanup.Statements, exceptional, cleanupContext with
                { ActiveExceptionUnwind = true, SuppressCleanupFailures = true }, false);
                state.Effects.UnionWith(exceptional.Effects);
                state.RecordExceptionalEdge(edge.Type, exceptional, edge.Origin, edge.FromCleanup);
            }
        }
        else
        {
            foreach (var edge in escapingEdges)
            { state.RecordExceptionalEdge(edge.Type, edge.State, edge.Origin, edge.FromCleanup); }
        }
        foreach (var exit in exits) { exit.ExceptionSink = outerExceptionSink; }
        context.Exits?.AddRange(exits);
        return attempt.IsValue ? value : new(Empty);
    }

    private bool MayOverlap(ResolvedType thrown, ResolvedType caught) => Converts(thrown, caught) || Converts(caught, thrown);

    private void AddUnknownRuntimeEdge(AstBlock? body, State incoming, State normal, List<State.ExceptionEdge> edges)
    {
        if (Core("RuntimeException") is not { } runtime || !MayContainPotentiallyThrowingExpression(body)
            || edges.Any(edge => Converts(edge.Type, runtime))) { return; }

        // Some built-in operations can raise RuntimeException without a declared call effect. Their
        // exact instruction snapshots are not available until FIR, so retain only facts true both
        // before and after the try body. This is conservative and never grants a smart cast.
        var snapshot = incoming.Clone();
        snapshot.Assigned.IntersectWith(normal.Assigned);
        snapshot.Fields.IntersectWith(normal.Fields);
        snapshot.Narrowed.Clear();
        snapshot.Effects.Clear();
        snapshot.ExceptionSink = null;
        snapshot.Reachable = true;
        edges.Add(new(runtime, snapshot));
    }

    private bool MayContainPotentiallyThrowingExpression(AstNode? root)
    {
        if (root is null) { return false; }
        var pending = new Stack<AstNode>();
        var visited = new HashSet<AstNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(node)) { continue; }
            if (visited.Count > 65536) { Defer(root); return true; }
            if (node is AstCall or AstMember || node.SyntaxKind == NodeKind.IndexExpression
                || node is AstOperation { Operation: not ("&&" or "||" or "?:") }) { return true; }
            foreach (var child in node.Children) { pending.Push(child); }
        }
        return false;
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
