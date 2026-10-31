// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private static ImmutableArray<AstArgument> ConstructionArguments(AstNode node)
    {
        var arguments = ImmutableArray.CreateBuilder<AstArgument>();
        foreach (var argument in node.Children.FirstOrDefault(child => child.SyntaxKind == NodeKind.ArgumentList)?.Children ?? [])
        {
            if (argument is AstExpression direct) { arguments.Add(new(null, AstArgumentMode.Value, direct, direct.Origin)); continue; }
            var value = argument.Children.LastOrDefault();
            if (value is null) { continue; }
            var mode = value.SyntaxKind switch
            {
                NodeKind.RefArgument => AstArgumentMode.Ref, NodeKind.OutArgument => AstArgumentMode.Out,
                NodeKind.OutVariable => AstArgumentMode.OutVariable, NodeKind.SpreadArgument => AstArgumentMode.Spread,
                _ => AstArgumentMode.Value,
            };
            if (mode is AstArgumentMode.Ref or AstArgumentMode.Out or AstArgumentMode.Spread)
            { value = value.Children.FirstOrDefault() ?? value; }
            arguments.Add(new(argument.Children.FirstOrDefault(child => child.SyntaxKind == NodeKind.Name)?.Origin.Text,
                mode, value, argument.Origin));
        }
        return arguments.ToImmutable();
    }

    private void ExecuteConstructionEdge(ResolvedType owner, AstNode site, ImmutableArray<AstArgument> arguments,
        State state, Context context, int depth, bool isThis, bool implicitBase = false)
    {
        var constructors = Constructors(owner).ToArray();
        if (constructors.Length == 0)
        {
            if (!arguments.IsEmpty) { Report("SOL2274", "An implicit constructor has no parameters.", site); return; }
            ExecuteSelectedConstructionEdge(owner, null, site, state, depth, isThis);
            return;
        }
        if (implicitBase) { constructors = constructors.Where(constructor => constructor.Parameters.IsEmpty).ToArray(); }
        var values = Speculate(state, () =>
        {
            var probe = state.Clone();
            return arguments.Select(argument => argument.Mode == AstArgumentMode.OutVariable
                || argument.Value is AstLambda or AstLabelledExpression or AstSequence { SyntaxKind: NodeKind.ArrayExpression }
                    ? new Value(null) : InterfaceIndexArgumentValue(argument, probe.Reachable ? probe : probe.Clone(), context)).ToArray();
        }, out _);
        var candidates = constructors.Select(constructor => ApplicableCall(site, arguments,
            new(constructor, null, owner, owner), owner.Arguments, values, null, state, context)).OfType<Applicable>().ToArray();
        var best = candidates.Where(candidate => !candidates.Any(other => !ReferenceEquals(candidate, other) && Better(other, candidate))).ToArray();
        if (best.Length != 1)
        {
            if (ContainsTypeParameter(owner)) { Defer(site); return; }
            Report("SOL2274", best.Length == 0 ? "No applicable constructor delegation target." : "Ambiguous constructor delegation target.", site);
            return;
        }
        var selected = best[0];
        var constructorTarget = (AstConstructor)selected.Target.Callable;
        if (!ConstructionConstructorAccessible(constructorTarget, owner, site, delegation: true))
        { Report("SOL2214", "The selected constructor is inaccessible.", site); }
        EvaluateConstructionArguments(site, arguments, selected, values, state, context);
        if (!state.Reachable) { return; }
        EvaluateConstructionDefaults(selected, site, state);
        if (!state.Reachable) { return; }
        RecordPotentialCallEdges(state, selected.Effects);
        ExecuteSelectedConstructionEdge(owner, constructorTarget, site, state, depth, isThis);
        state.Effects.UnionWith(selected.Effects);
        if (state.Reachable) { AssignConstructionOutArguments(arguments, selected, state); }
        ConstructionProofs[(site, owner.Id, constructorTarget)] = new(
            new(site, owner, constructorTarget, selected.Mapping.Arguments, selected.Mapping.Defaults, isThis),
            Diagnostics.Count != 0 ? BodyBindingStatus.Invalid : _deferred.Count != 0 ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound,
            state.Reachable, state.Construction.Slots, [], selected.Effects, null);
    }

    private bool ConstructionConstructorAccessible(AstConstructor constructor, ResolvedType owner, AstNode site, bool delegation)
    {
        var current = ContainingType(site);
        bool ownScope = Ancestors(site).OfType<AstTypeDeclaration>()
            .Any(type => Sources.Declarations.GetDeclaredSymbol(type) == owner.Declaration);
        if (constructor.Modifiers.Contains("private")) { return ownScope; }
        if (constructor.Modifiers.Contains("protected"))
        { return ownScope || delegation && current is not null && Converts(current, owner); }
        return true;
    }

    private void ExecuteSelectedConstructionEdge(ResolvedType owner, AstConstructor? constructor, AstNode site,
        State state, int depth, bool isThis)
    {
        var worker = ConstructionWorker(owner, new(owner, constructor, _constructionFrame!.Receiver), _constructionSession!);
        var priorEffects = state.Effects.ToArray();
        state.Effects.Clear();
        worker.ExecuteConstructor(owner, constructor, site, state, depth + 1);
        ReplayConstruction(worker);
        ConstructionOriginAt(site, isThis ? "this delegation" : "base constructor", state);
        state.Effects.UnionWith(priorEffects);
    }

    private void EvaluateConstructionArguments(AstNode site, ImmutableArray<AstArgument> arguments,
        Applicable selected, Value?[] values, State state, Context context)
    {
        foreach (var mapping in selected.Mapping.Arguments.OrderBy(mapping => mapping.ArgumentIndex))
        {
            if (!state.Reachable) { break; }
            var argument = arguments[mapping.ArgumentIndex];
            var expected = selected.Parameters[mapping.ParameterIndex];
            if (mapping.IsSpread || mapping.IsArrayArgument) { expected = Types.GetArray(expected); }
            if (argument.Mode == AstArgumentMode.OutVariable) { continue; }
            var value = Expression(argument.Value as AstExpression, state, context, expected, argument.Mode != AstArgumentMode.Out);
            if (argument.Mode is AstArgumentMode.Out or AstArgumentMode.Ref)
            {
                if (!value.Storage || !value.Writable || value.Type != expected)
                { Report("SOL2244", "Constructor ref/out arguments require exact writable storage.", argument.Value); }
                if (value.Local is { } local)
                { state.Narrowed.Remove(local); if (argument.Mode == AstArgumentMode.Out) { state.Assigned.Remove(local); } }
            }
            else { Require(value, expected, argument.Value, state); }
            if (ConstructionAlias(argument.Value, state))
            { Report("SOL2273", "Constructor delegation arguments cannot expose an unpublished receiver.", argument.Value); }
        }
        ConstructionOriginAt(site, "explicit arguments", state);
    }

    private void AssignConstructionOutArguments(ImmutableArray<AstArgument> arguments, Applicable selected, State state)
    {
        var before = CaptureCleanupFacts(state);
        foreach (var mapping in selected.Mapping.Arguments)
        {
            var argument = arguments[mapping.ArgumentIndex];
            if (argument.Mode is not (AstArgumentMode.Out or AstArgumentMode.OutVariable)) { continue; }
            var local = argument.Value is AstAtom atom ? Sources.Lexical.Lookup(atom, atom.Spelling).Locals.SingleOrDefault()
                : Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols).FirstOrDefault(local =>
                ReferenceEquals(local.Origin.Source, argument.Origin.Source)
                && argument.Value.Origin.Span.Start <= local.Origin.Span.Start && local.Origin.Span.End <= argument.Value.Origin.Span.End);
            if (local is not null) { state.Assigned.Add(local); _localTypes[local] = selected.Parameters[mapping.ParameterIndex]; }
        }
        UpdateCleanupFacts(state, before, CaptureCleanupFacts(state));
    }

    private BodyBindingStatus EvaluateConstructionDefaults(Applicable selected, AstNode site, State state)
    {
        if (selected.Mapping.Defaults.IsEmpty) { return BodyBindingStatus.Bound; }
        var owner = selected.Target.Constructed!;
        var session = _constructionSession ?? new ConstructionSession();
        var key = (owner.Id, selected.Target.Callable);
        _cancellation.ThrowIfCancellationRequested();
        Types.ValidateOwnership(owner);
        if (session.ActiveDefaults.Count + session.Chain.Count + session.ActiveHelpers.Count >= 128
            || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()
            || !session.ActiveDefaults.Add(key))
        { Defer(site); MarkConstructionUnknown(state); return BodyBindingStatus.Deferred; }
        try
        {
            var worker = ConstructionWorker(owner, new(owner, (AstConstructor)selected.Target.Callable, owner), session);
            var defaultState = new State { ExceptionSink = [] };
            var defaults = Sources.Defaults.GetDefaults(selected.Target.Callable);
            worker.SeedConstructionParameters(selected.Target.Callable, defaultState);
            foreach (int index in selected.Mapping.Defaults)
            {
                if (!defaultState.Reachable) { break; }
                if (defaults.Parameters[index]?.Expression is not AstExpression expression) { worker.Defer(site); continue; }
                var expected = selected.Parameters[index];
                if (selected.Target.Callable.Parameters[index].PassingMode == AstPassingMode.Vararg) { expected = Types.GetArray(expected); }
                worker.ConstructionPhase(expression, "default argument", defaultState, () =>
                    worker.Require(worker.Expression(expression, defaultState, new(selected.Target.Callable, null), expected), expected, expression, defaultState));
            }
            foreach (var effect in defaultState.Effects.Where(worker.IsChecked))
            {
                if (!selected.Effects.Any(budget => worker.Converts(effect, budget)))
                { worker.Report("SOL2250", "Constructor default effect is not declared: " + effect.Name, selected.Target.Callable); }
            }
            ReplayConstruction(worker);
            foreach (var edge in defaultState.ExceptionSink ?? []) { state.RecordExceptionalEdge(edge.Type, state, edge.Origin); }
            state.Effects.UnionWith(defaultState.Effects);
            state.Reachable &= defaultState.Reachable;
            return worker._errorGeneration != 0 ? BodyBindingStatus.Invalid
                : worker._deferred.Count != 0 ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound;
        }
        finally { session.ActiveDefaults.Remove(key); }
    }
}
