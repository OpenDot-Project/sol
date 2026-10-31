// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private BodySummary CheckInferenceBody(AstCallable callable)
    {
        var frame = _constructionFrame;
        _constructionFrame = null;
        try { return CheckBody(callable); }
        finally { _constructionFrame = frame; }
    }

    private bool ApplyConstructionCall(AstCall call, Applicable selected, Value?[] values, State state, Context context,
        bool invalidCall = false)
    {
        if (_discoveringConstructionDependencies) { return false; }
        if (selected.Target.Constructed is { } constructed)
        {
            if (_constructionFrame is not null && call.Arguments.Any(argument => ConstructionAlias(argument.Value, state)))
            { Defer(call); MarkConstructionUnknown(state); }
            var priorSession = _constructionSession;
            _constructionSession ??= new();
            int originStart = _constructionSession.Origins.Count;
            try
            {
                _constructionSession.Origins.Add(new(call, "explicit arguments", call.Arguments
                    .Select(argument => argument.Value is AstExpression expression ? Expressions.GetValueOrDefault(expression) : null)
                    .OfType<ExpressionBinding>().SelectMany(binding => binding.Effects).Distinct().ToImmutableArray()));
                var defaultStatus = EvaluateConstructionDefaults(selected, call, state);
                if (!state.Reachable) { return true; }
                RecordPotentialCallEdges(state, selected.Effects);
                var proof = ProveConstruction(constructed, (AstConstructor)selected.Target.Callable, call,
                    selected.Mapping.Arguments, selected.Mapping.Defaults, state, originStart);
                if ((invalidCall || defaultStatus != BodyBindingStatus.Bound) && proof.Status != BodyBindingStatus.Invalid)
                {
                    proof = proof with { Status = invalidCall ? BodyBindingStatus.Invalid : defaultStatus,
                        Reason = "The selected constructor call or default argument proof is incomplete or invalid." };
                    ConstructionProofs[(call, constructed.Id, (AstConstructor)selected.Target.Callable)] = proof;
                    Defer(call);
                }
                state.Reachable &= proof.CanCompleteNormally;
                return true;
            }
            finally { _constructionSession = priorSession; }
        }
        if (_constructionFrame is null) { return false; }
        var targetExpression = call.Target is AstGenericApplication application ? application.Target : call.Target;
        bool ownReceiver = selected.Target.Receiver is not null
            && ConstructionReceiver((targetExpression as AstMember)?.Receiver, state);
        var aliases = selected.Mapping.Arguments.Where(mapping => ConstructionAlias(call.Arguments[mapping.ArgumentIndex].Value, state))
            .Select(mapping => mapping.ParameterIndex).ToImmutableHashSet();
        if (!ownReceiver && aliases.IsEmpty) { return false; }
        var target = selected.Target.Callable;
        if (ownReceiver && !target.Modifiers.Contains("private") && !target.Modifiers.Contains("final")
            && (target.Modifiers.Contains("open") || target.Modifiers.Contains("override"))
            && (_constructionFrame.Owner != _constructionFrame.Receiver
                || _constructionFrame.Receiver.Declaration?.Parts[0].Modifiers.Contains("open") == true)
            && targetExpression is not AstMember { Receiver: AstAtom { Spelling: "super" } })
        { Report("SOL2272", "Construction cannot dynamically dispatch on its unpublished receiver.", call); return true; }
        if (selected.Target.Receiver?.Declaration?.Parts[0].SyntaxKind == OpenDot.Sol.Cst.NodeKind.InterfaceDeclaration)
        { Report("SOL2272", "Construction cannot dispatch through an interface receiver.", call); return true; }
        if (target is not AstFunction { Body.Kind: not AstBodyKind.Missing } function)
        { Defer(call); MarkConstructionUnknown(state); return true; }
        if (!aliases.IsEmpty && selected.Target.Receiver is null && !target.Modifiers.Contains("private"))
        { Report("SOL2273", "An unpublished receiver cannot escape through a public function argument.", call); return true; }
        if (aliases.Any(index => target.Parameters[index].IsEscaping))
        { Report("SOL2273", "An unpublished receiver cannot reach an escaping parameter.", call); return true; }
        var owner = selected.Target.Receiver ?? _constructionFrame.Owner;
        var callbacks = selected.Mapping.Arguments.Select(mapping =>
            (mapping.ParameterIndex, Callback: ConstructionCallbackAt(call.Arguments[mapping.ArgumentIndex].Value, state)))
            .Where(pair => pair.Callback is not null).ToImmutableDictionary(pair => pair.ParameterIndex, pair => pair.Callback!);
        ApplyConstructionHelper(function, owner, selected, aliases, call, state, ownReceiver, callbacks);
        return true;
    }

    private sealed record HelperApproximation(ConstructionFacts Facts, bool Normal, BodyBindingStatus Status,
        ImmutableArray<State.ExceptionEdge> Exceptions, ImmutableArray<ResolvedType> Effects, bool ReturnsReceiver = false,
        ImmutableHashSet<LocalSymbol>? AssignedLocals = null);
    private sealed record HelperInstance(AstCallable Function, ResolvedType Owner, string MethodArguments,
        string Incoming, string Aliases, bool Receiver);

    private void ApplyConstructionHelper(AstCallable function, ResolvedType owner, Applicable selected,
        ImmutableHashSet<int> aliases, AstNode site, State state, bool receiver,
        ImmutableDictionary<int, ConstructionCallback>? callbacks = null)
    {
        var session = _constructionSession!;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        { Defer(site); MarkConstructionUnknown(state); return; }
        var dependencyStatus = DiscoverConstructionDependencies(function, owner, selected.TypeArguments, site);
        session.RecursiveHelpers |= session.RecursiveGroups.ContainsKey(new(function, owner,
            string.Join(",", selected.TypeArguments.Select(type => type.Id))));
        if (dependencyStatus == BodyBindingStatus.Invalid)
        { Report("SOL2277", "Construction depends on an invalid source helper graph.", site); }
        if (dependencyStatus != BodyBindingStatus.Bound) { Defer(site); }
        if (dependencyStatus == BodyBindingStatus.Deferred) { MarkConstructionUnknown(state); return; }
        string incoming = string.Join(";", state.Construction.Slots.OrderBy(pair => pair.Key.Owner.Id)
            .ThenBy(pair => pair.Key.Declaration.Id).ThenBy(pair => pair.Key.Name)
            .Select(pair => pair.Key.Owner.Id + ":" + pair.Key.Declaration.Id + ":" + pair.Key.Name + ":" + pair.Value));
        var key = new HelperInstance(function, owner, string.Join(",", selected.TypeArguments.Select(type => type.Id)),
            incoming, string.Join(",", aliases.Order()) + ":" + string.Join(";", callbacks?.OrderBy(pair => pair.Key)
                .Select(pair => pair.Key + ":" + pair.Value.Expression.Id + ":" + pair.Value.Owner.Id) ?? []), receiver);
        var helpers = session.Helpers;
        var active = session.ActiveHelpers;
        session.Dependencies.Add((site, owner.Id, function));
        session.Edges = session.Dependencies.Count;
        if (session.Edges > 8192 || active.Count >= 128
            || helpers.Count + session.ConstructorInstances.Count + session.CallbackInstances.Count >= 1024 && !helpers.ContainsKey(key))
        { Defer(site); MarkConstructionUnknown(state); return; }
        if (active.Contains(key))
        {
            session.RecursiveHelpers = true;
            if (helpers.TryGetValue(key, out var approximation))
            {
                ApplyHelperApproximation(approximation, state, site);
                if (approximation.Status == BodyBindingStatus.Invalid) { Report("SOL2277", "Invalid recursive construction helper.", site); }
                if (approximation.Status == BodyBindingStatus.Deferred) { Defer(site); }
            }
            return;
        }
        helpers.TryAdd(key, new(state.Construction, false, BodyBindingStatus.Bound, [], []));
        active.Add(key);
        try
        {
            HelperApproximation? previous = null;
            BodyBindingBuilder? lastWorker = null;
            for (int iteration = 0; iteration < 128; iteration++)
            {
                _cancellation.ThrowIfCancellationRequested();
                var worker = ConstructionWorker(owner, new(owner, null, _constructionFrame!.Receiver, Helper: true, OwnReceiver: receiver), session);
                foreach (var pair in Sources.Types.GetTypeParameters(function).Zip(selected.TypeArguments))
                { worker._bodySubstitution[pair.First] = pair.Second; }
                var flow = state.Clone();
                flow.Effects.Clear(); flow.ExceptionSink = [];
                flow.CleanupPaths.Clear(); flow.CleanupFactsInitialized = false;
                flow.CleanupPaths.Add(new([], flow.Assigned.ToImmutableHashSet(), flow.Narrowed.ToImmutableDictionary(), flow.Fields.ToImmutableHashSet())
                { Construction = flow.Construction });
                foreach (var local in worker.OwnLocals(function))
                {
                    flow.Assigned.Remove(local); flow.Narrowed.Remove(local);
                    flow.Construction = flow.Construction with
                    { Aliases = flow.Construction.Aliases.Remove(local), Callbacks = flow.Construction.Callbacks.Remove(local) };
                }
                worker.SeedConstructionParameters(function, flow);
                foreach (var pair in callbacks ?? ImmutableDictionary<int, ConstructionCallback>.Empty)
                {
                    var parameter = function.Parameters[pair.Key];
                    foreach (var local in worker.OwnLocals(function).Where(local =>
                        ReferenceEquals(local.Origin.Element, parameter.NameOrigin.Element)))
                    { flow.Construction = flow.Construction with { Callbacks = flow.Construction.Callbacks.SetItem(local, pair.Value) }; }
                }
                foreach (int index in aliases)
                {
                    var parameter = function.Parameters[index];
                    var local = worker.OwnLocals(function).FirstOrDefault(local => ReferenceEquals(local.Origin.Element, parameter.NameOrigin.Element));
                    if (local is not null) { flow.Construction = flow.Construction with { Aliases = flow.Construction.Aliases.Add(local) }; }
                }
                var returns = new List<State>();
                var result = function.ReturnType is { } writtenReturn ? worker.Written(writtenReturn)
                    : function is AstAccessor accessor ? accessor.Name == "get"
                        ? worker.ParentProperty(accessor)?.Type is { } propertyType ? worker.Written(propertyType)
                            : worker.Ancestors(accessor).OfType<AstIndexer>().FirstOrDefault()?.ValueType is { } indexType ? worker.Written(indexType) : null
                        : Empty
                    : ConstructionHelperBody(function).Kind == AstBodyKind.Block ? Empty : selected.Result;
                var context = new Context(function, result, Exits: returns);
                if (function.Descendants().Take(65537).Count() > 65536)
                { worker.Defer(function); worker.MarkConstructionUnknown(flow); }
                else if (ConstructionHelperBody(function).Node is AstBlock block) { worker.Statements(block.Statements, flow, context, false); }
                else if (ConstructionHelperBody(function).Node is AstExpression expression)
                {
                    var value = worker.Expression(expression, flow, context, result);
                    if (result is not null) { worker.Require(value, result, expression, flow); }
                    worker._constructionReturnsReceiver |= worker.ConstructionAlias(expression, flow);
                }
                if (flow.Reachable) { returns.Add(flow.Clone()); }
                foreach (var exit in returns.Where(exit => exit.Reachable)) { worker.CheckOut(exit, context, function); }
                var budget = function.ThrownTypes.Select(worker.Written).ToArray();
                foreach (var effect in flow.Effects.Where(worker.IsChecked))
                {
                    if (!budget.Any(type => worker.Converts(effect, type)))
                    { worker.Report("SOL2250", "Helper effect is not declared: " + effect.Name, function); }
                }
                var normal = returns.FirstOrDefault()?.Clone();
                if (normal is not null)
                {
                    foreach (var exit in returns.Skip(1)) { var left = normal.Clone(); normal.Join(left, exit); }
                }
                var next = new HelperApproximation(normal?.Construction ?? flow.Construction, normal is not null,
                    worker._errorGeneration != 0 ? BodyBindingStatus.Invalid : worker._deferred.Count != 0 ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound,
                    (flow.ExceptionSink ?? []).ToImmutableArray(), flow.Effects.Union(returns.SelectMany(exit => exit.Effects)).ToImmutableArray(),
                    worker._constructionReturnsReceiver, normal?.Assigned.ToImmutableHashSet());
                helpers[key] = next;
                lastWorker = worker;
                bool stable = previous is not null && previous.Normal == next.Normal && previous.Status == next.Status
                    && previous.ReturnsReceiver == next.ReturnsReceiver
                    && previous.Facts.Slots.Count == next.Facts.Slots.Count
                    && previous.Facts.Aliases.SetEquals(next.Facts.Aliases)
                    && (previous.AssignedLocals ?? []).SetEquals(next.AssignedLocals ?? [])
                    && previous.Facts.Slots.All(pair => next.Facts.Slots.GetValueOrDefault(pair.Key) == pair.Value)
                    && previous.Exceptions.Length == next.Exceptions.Length
                    && previous.Exceptions.Zip(next.Exceptions).All(pair => pair.First.Type == pair.Second.Type
                        && pair.First.Origin == pair.Second.Origin
                        && pair.First.State.Construction.Slots.Count == pair.Second.State.Construction.Slots.Count
                        && pair.First.State.Construction.Slots.All(slot =>
                            pair.Second.State.Construction.Slots.GetValueOrDefault(slot.Key) == slot.Value))
                    && previous.Effects.ToHashSet().SetEquals(next.Effects);
                if (!session.RecursiveHelpers || stable) { break; }
                previous = next;
                if (iteration == 127) { worker.Defer(site); helpers[key] = next with { Status = BodyBindingStatus.Deferred }; }
            }
            if (lastWorker is not null) { ReplayConstruction(lastWorker, site); }
            var proof = helpers[key];
            session.Origins.Add(new(function, "receiver helper", proof.Effects));
            ApplyHelperApproximation(proof, state, site);
            if (proof.ReturnsReceiver && site is AstExpression expressionSite) { _constructionReceiverValues.Add(expressionSite); }
            if (proof.Status == BodyBindingStatus.Invalid)
            { Report("SOL2277", "Construction depends on an invalid receiver helper.", site); }
            if (proof.Status != BodyBindingStatus.Bound) { Defer(site); }
        }
        finally { active.Remove(key); }
    }

    private void ApplyHelperApproximation(HelperApproximation proof, State state, AstNode site)
    {
        if (proof.Exceptions.Length > State.MaxCleanupExceptionalAlternatives
            || (state.ExceptionSink?.Count ?? 0) + proof.Exceptions.Length > State.MaxCleanupExceptionalAlternatives)
        {
            Defer(site); MarkConstructionUnknown(state);
            state.Effects.UnionWith(proof.Effects); state.Reachable &= proof.Normal;
            return;
        }
        foreach (var edge in proof.Exceptions)
        {
            var snapshot = state.Clone(); snapshot.Construction = edge.State.Construction;
            state.RecordExceptionalEdge(edge.Type, snapshot, edge.Origin);
        }
        state.Construction = proof.Facts;
        if (proof.AssignedLocals is { } assigned)
        { state.Assigned.UnionWith(assigned.Where(local => _localTypes.ContainsKey(local))); }
        state.Fields.UnionWith(proof.Facts.Slots.Where(pair => pair.Value == ConstructionAssignment.Assigned)
            .Select(pair => pair.Key.Declaration).OfType<AstProperty>());
        state.Effects.UnionWith(proof.Effects);
        state.Reachable &= proof.Normal;
    }

    private static AstBody ConstructionHelperBody(AstCallable callable) => callable switch
    { AstFunction function => function.Body, AstAccessor accessor => accessor.Body, _ => new(AstBodyKind.Missing, null) };

    private void ApplyConstructionAccessor(AstAccessor accessor, ResolvedType owner, AstNode site, State state)
    {
        if (_constructionFrame is null || ConstructionHelperBody(accessor).Kind == AstBodyKind.Missing) { return; }
        var declaration = (AstDeclaration?)ParentProperty(accessor) ?? Ancestors(accessor).OfType<AstIndexer>().FirstOrDefault();
        if (owner.Declaration?.Parts[0].SyntaxKind == OpenDot.Sol.Cst.NodeKind.InterfaceDeclaration
            || declaration is not null && !declaration.Modifiers.Contains("private")
                && !declaration.Modifiers.Contains("final")
                && (declaration.Modifiers.Contains("open") || declaration.Modifiers.Contains("override"))
                && (_constructionFrame.Owner != _constructionFrame.Receiver
                    || owner.Declaration?.Parts[0].Modifiers.Contains("open") == true))
        { Report("SOL2272", "Construction cannot dynamically dispatch an accessor on its unpublished receiver.", site); return; }
        var target = new Applicable(new(accessor, null, owner), new([], [], [], false, null), [], [], null,
            accessor.ThrownTypes.Select(Written).ToImmutableArray(), [], false);
        ApplyConstructionHelper(accessor, owner, target, [], site, state, true);
    }

    private void ApplyConstructionProtocol(ProtocolSelection protocol, AstNode site, State state)
    {
        if (_constructionFrame is null) { return; }
        var function = protocol.Function;
        if (function.Body.Kind == AstBodyKind.Missing)
        { Defer(site); MarkConstructionUnknown(state); return; }
        if ((function.Modifiers.Contains("open") || function.Modifiers.Contains("override"))
            && !function.Modifiers.Contains("final") && protocol.Owner.Declaration?.Parts[0].Modifiers.Contains("open") == true)
        { Defer(site); MarkConstructionUnknown(state); return; }
        var parameters = function.Parameters.Select(parameter => InterfaceWritten(parameter.Type!, protocol.Owner)).ToImmutableArray();
        var target = new Applicable(new(function, null, protocol.Owner), new([], [], [], false, null), [], parameters,
            protocol.Type, protocol.Effects, [], false);
        ApplyConstructionHelper(function, protocol.Owner, target, ImmutableHashSet.Create(0), site, state, false);
    }
}
