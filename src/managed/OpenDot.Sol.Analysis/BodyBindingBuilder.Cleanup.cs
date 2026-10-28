// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private sealed record CleanupFacts(ImmutableHashSet<LocalSymbol> Assigned,
        ImmutableDictionary<LocalSymbol, ResolvedType> Narrowed, ImmutableHashSet<AstProperty> Fields);

    private Value Statements(IEnumerable<AstNode> statements, State state, Context context, bool resultContext)
    {
        var scope = new object();
        var outerSink = state.ExceptionSink;
        var exceptionalEdges = new List<State.ExceptionEdge>();
        var suppressedOutcomes = new List<State>();
        var effectsAtEntry = state.Effects.ToHashSet();
        int exceptionalBudget = State.MaxCleanupExceptionalAlternatives;
        int exitStart = context.Exits?.Count ?? 0;
        int breakStart = context.Breaks?.Count ?? 0;
        int continueStart = context.Continues?.Count ?? 0;
        state.InitializeCleanupFacts();
        state.ExceptionSink = exceptionalEdges;

        var result = StatementsCore(statements, state, context with
        { CleanupScope = scope, ActiveExceptionUnwind = false, CleanupBoundary = false }, resultContext);

        if (state.Reachable)
        {
            CopyState(state, ApplyCleanupScope(state, scope, context, outerSink,
                context.ActiveExceptionUnwind || context.SuppressCleanupFailures, ref exceptionalBudget));
        }
        ApplyExitCleanups(context.Exits, exitStart, scope, context, outerSink, ref exceptionalBudget);
        ApplyExitCleanups(context.Breaks, breakStart, scope, context, outerSink, ref exceptionalBudget);
        ApplyExitCleanups(context.Continues, continueStart, scope, context, outerSink, ref exceptionalBudget);

        foreach (var edge in exceptionalEdges)
        {
            if (context.CleanupBoundary && edge.FromCleanup && edge.Origin is { } cleanupThrow)
            { Report("SOL2254", "Cleanup cannot transfer control outwards.", cleanupThrow); }
            var exceptional = edge.State.Clone();
            exceptional.ExceptionSink = outerSink;
            exceptional.Reachable = true;
            exceptional = ApplyCleanupScope(exceptional, scope, context, outerSink, true, ref exceptionalBudget);
            if (context.ActiveExceptionUnwind)
            {
                exceptional.Effects.Clear(); exceptional.Effects.UnionWith(effectsAtEntry);
                if (!effectsAtEntry.Contains(edge.Type)) { exceptional.Effects.Remove(edge.Type); }
                exceptional.SuppressedEffects.Add(edge.Type);
                exceptional.Reachable = true;
                exceptional.ExceptionSink = outerSink;
                suppressedOutcomes.Add(exceptional);
            }
            else
            {
                state.Effects.UnionWith(exceptional.Effects);
                if (outerSink is not null)
                {
                    exceptional.Reachable = true;
                    exceptional.ExceptionSink = null;
                    outerSink.Add(new(edge.Type, exceptional, edge.Origin, edge.FromCleanup));
                }
            }
        }

        if (context.ActiveExceptionUnwind && suppressedOutcomes.Count != 0)
        {
            state.Effects.Clear(); state.Effects.UnionWith(effectsAtEntry);
            var paths = new List<State>();
            if (state.Reachable) { paths.Add(state.Clone()); }
            paths.AddRange(suppressedOutcomes);
            var joined = JoinCleanupOutcomes(paths, outerSink);
            CopyState(state, joined);
            state.Reachable = true;
        }

        state.ExceptionSink = outerSink;
        DeferOverflowed(state);
        return result;
    }

    private void RegisterDefer(AstNode node, AstBlock body, State state, Context context)
    {
        if (!state.Reachable) { return; }
        if (context.CleanupScope is not { } scope)
        {
            Defer(node);
            return;
        }
        if (state.CleanupTrackingDeferred)
        {
            state.OverflowedCleanups.Add(node);
            Defer(node);
            return;
        }

        if (state.CleanupPaths.Any(path => path.Registrations.Count(registration => ReferenceEquals(registration.Scope, scope))
            >= State.MaxCleanupRegistrationsPerScope))
        {
            foreach (var path in state.CleanupPaths)
            { foreach (var active in path.Registrations) { state.OverflowedCleanups.Add(active.Node); } }
            state.OverflowedCleanups.Add(node);
            state.CleanupPaths.Clear();
            state.CleanupTrackingDeferred = true;
            DeferOverflowed(state);
            return;
        }

        var registration = new State.CleanupRegistration(scope, node, body);
        for (int index = 0; index < state.CleanupPaths.Count; index++)
        {
            var path = state.CleanupPaths[index];
            state.CleanupPaths[index] = path with { Registrations = path.Registrations.Add(registration) };
        }
    }

    private static CleanupFacts CaptureCleanupFacts(State state) => new(
        state.Assigned.ToImmutableHashSet(), state.Narrowed.ToImmutableDictionary(), state.Fields.ToImmutableHashSet());

    private static void UpdateCleanupFacts(State state, CleanupFacts before, CleanupFacts after)
    {
        if (!state.CleanupFactsInitialized || state.CleanupTrackingDeferred) { return; }
        var removedAssigned = before.Assigned.Except(after.Assigned).ToImmutableHashSet();
        var addedAssigned = after.Assigned.Except(before.Assigned).ToImmutableHashSet();
        var removedFields = before.Fields.Except(after.Fields).ToImmutableHashSet();
        var addedFields = after.Fields.Except(before.Fields).ToImmutableHashSet();
        var changedNarrowing = before.Narrowed.Keys.Union(after.Narrowed.Keys)
            .Where(local => before.Narrowed.GetValueOrDefault(local) != after.Narrowed.GetValueOrDefault(local))
            .ToArray();

        for (int index = 0; index < state.CleanupPaths.Count; index++)
        {
            var path = state.CleanupPaths[index];
            var assigned = path.Assigned.Except(removedAssigned).Union(addedAssigned);
            var fields = path.Fields.Except(removedFields).Union(addedFields);
            var narrowed = path.Narrowed.ToBuilder();
            foreach (var local in changedNarrowing)
            {
                if (after.Narrowed.TryGetValue(local, out var type)) { narrowed[local] = type; }
                else { narrowed.Remove(local); }
            }
            state.CleanupPaths[index] = path with
            {
                Assigned = assigned.ToImmutableHashSet(),
                Narrowed = narrowed.ToImmutable(),
                Fields = fields.ToImmutableHashSet(),
            };
        }
    }

    private static void InvalidateMutableCleanupNarrowings(State state)
    {
        if (state.CleanupTrackingDeferred) { return; }
        for (int index = 0; index < state.CleanupPaths.Count; index++)
        {
            var path = state.CleanupPaths[index];
            state.CleanupPaths[index] = path with
            { Narrowed = path.Narrowed.RemoveRange(path.Narrowed.Keys.Where(local => local.IsMutable)) };
        }
    }

    private bool ContainsCall(AstNode node)
    {
        if (node is AstCallable or AstAnonymousFunction or AstLambda) { return false; }
        var pending = new Stack<AstNode>();
        var visited = new HashSet<AstNode>(ReferenceEqualityComparer.Instance);
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current)) { continue; }
            if (visited.Count > 65536) { return true; }
            // A nested callable's body is checked separately and is not run when it is declared.
            if (!ReferenceEquals(current, node) && current is AstCallable or AstAnonymousFunction or AstLambda)
            { continue; }
            if (current is AstCall) { return true; }
            foreach (var child in current.Children) { pending.Push(child); }
        }
        return false;
    }

    private void ApplyExitCleanups(List<State>? exits, int start, object scope, Context context,
        List<State.ExceptionEdge>? outerSink, ref int exceptionalBudget)
    {
        if (exits is null) { return; }
        for (int index = start; index < exits.Count; index++)
        {
            if (!exits[index].Reachable) { continue; }
            var exit = ApplyCleanupScope(exits[index], scope, context, outerSink, false, ref exceptionalBudget);
            DeferOverflowed(exit);
            exits[index] = exit;
        }
    }

    private void ApplyFinallyTransfers(List<State>? transfers, int start, AstBlock cleanup,
        Context cleanupContext, List<State.ExceptionEdge>? outerSink)
    {
        if (transfers is null) { return; }
        for (int index = start; index < transfers.Count; index++)
        {
            var transfer = transfers[index];
            if (!transfer.Reachable) { continue; }
            transfer.ExceptionSink = outerSink;
            transfer.Reachable = true;
            Statements(cleanup.Statements, transfer, cleanupContext, false);
        }
    }

    private State ApplyCleanupScope(State input, object scope, Context context,
        List<State.ExceptionEdge>? outerSink, bool activeException, ref int exceptionalBudget)
    {
        if (input.CleanupTrackingDeferred)
        {
            foreach (var node in input.OverflowedCleanups) { Defer(node); }
            return input.Clone();
        }

        var outcomes = new List<State>();
        foreach (var path in input.CleanupPaths)
        {
            var run = input.Clone();
            run.CleanupPaths.Clear();
            run.CleanupPaths.Add(path);
            run.Assigned.Clear(); run.Assigned.UnionWith(path.Assigned);
            run.Narrowed.Clear(); foreach (var pair in path.Narrowed) { run.Narrowed.Add(pair.Key, pair.Value); }
            run.Fields.Clear(); run.Fields.UnionWith(path.Fields);
            var registrations = path.Registrations
                .Where(registration => ReferenceEquals(registration.Scope, scope)).Reverse().ToArray();
            run = RunCleanupPath(run, registrations, 0, context, outerSink, activeException,
                ref exceptionalBudget);
            RemoveScopeRegistrations(run, scope);
            DeferOverflowed(run);
            outcomes.Add(run);
        }

        if (outcomes.Count == 0)
        {
            var empty = input.Clone();
            empty.CleanupPaths.Clear();
            empty.CleanupPaths.Add(new(ImmutableArray<State.CleanupRegistration>.Empty,
                empty.Assigned.ToImmutableHashSet(), empty.Narrowed.ToImmutableDictionary(), empty.Fields.ToImmutableHashSet()));
            return empty;
        }
        var joined = JoinCleanupOutcomes(outcomes, outerSink);
        DeferOverflowed(joined);
        return joined;
    }

    private static State JoinCleanupOutcomes(IReadOnlyList<State> outcomes, List<State.ExceptionEdge>? outerSink)
    {
        var joined = outcomes[0].Clone();
        for (int index = 1; index < outcomes.Count; index++)
        {
            var previous = joined.Clone();
            joined.Join(previous, outcomes[index]);
        }
        joined.ExceptionSink = outerSink;
        return joined;
    }

    private State RunCleanupPath(State run, State.CleanupRegistration[] registrations, int start,
        Context context, List<State.ExceptionEdge>? outerSink, bool activeException,
        ref int exceptionalBudget)
    {
        bool continues = run.Reachable;
        var suppressedOutcomes = new List<State>();
        for (int index = start; index < registrations.Length; index++)
        {
            var registration = registrations[index];
            if (run.CleanupTrackingDeferred)
            {
                foreach (var remaining in registrations.Skip(index)) { Defer(remaining.Node); }
                break;
            }
            if (exceptionalBudget <= 0)
            {
                DeferRemainingCleanups(run, registrations, index);
                break;
            }

            var actionEdges = new List<State.ExceptionEdge>();
            var actionEffects = run.Effects.ToHashSet();
            run.Reachable = true;
            run.ExceptionSink = actionEdges;
            Statements(registration.Body.Statements, run,
                context with
                {
                    Cleanup = true,
                    CleanupLoopDepth = context.LoopDepth,
                    SuppressCleanupFailures = activeException,
                    ActiveExceptionUnwind = false,
                    CleanupBoundary = true,
                    Exits = null,
                    Breaks = null,
                    Continues = null,
                }, false);
            if (activeException)
            {
                run.Effects.Clear(); run.Effects.UnionWith(actionEffects);
            }
            continues &= run.Reachable;

            foreach (var edge in actionEdges)
            {
                var exceptional = edge.State.Clone();
                exceptional.Reachable = true;
                exceptional.ExceptionSink = outerSink;
                if (exceptionalBudget == 0)
                {
                    DeferRemainingCleanups(run, registrations, index + 1);
                    DeferRemainingCleanups(exceptional, registrations, index + 1);
                }
                else
                {
                    exceptionalBudget--;
                    exceptional = RunCleanupPath(exceptional, registrations, index + 1,
                        context, outerSink, true, ref exceptionalBudget);
                }
                if (activeException)
                {
                    exceptional.Effects.Clear(); exceptional.Effects.UnionWith(actionEffects);
                    exceptional.SuppressedEffects.Add(edge.Type);
                    suppressedOutcomes.Add(exceptional);
                    continue;
                }
                run.Effects.UnionWith(exceptional.Effects);
                DeferOverflowed(exceptional);
                if (outerSink is not null)
                {
                    exceptional.Reachable = true;
                    exceptional.ExceptionSink = null;
                    outerSink.Add(new(edge.Type, exceptional, edge.Origin, edge.FromCleanup));
                }
            }
            if (!continues) { break; }
        }
        run.Reachable = continues;
        run.ExceptionSink = outerSink;
        if (activeException && suppressedOutcomes.Count != 0)
        {
            var paths = new List<State>();
            if (run.Reachable) { paths.Add(run.Clone()); }
            paths.AddRange(suppressedOutcomes);
            run = JoinCleanupOutcomes(paths, outerSink);
            run.Reachable = true;
        }
        return run;
    }

    private void DeferRemainingCleanups(State state, State.CleanupRegistration[] registrations, int start)
    {
        state.CleanupTrackingDeferred = true;
        foreach (var registration in registrations.Skip(start))
        {
            state.OverflowedCleanups.Add(registration.Node);
            Defer(registration.Node);
        }
        state.CleanupPaths.Clear();
    }

    private static void RemoveScopeRegistrations(State state, object scope)
    {
        if (state.CleanupTrackingDeferred) { return; }
        for (int index = 0; index < state.CleanupPaths.Count; index++)
        {
            var path = state.CleanupPaths[index];
            state.CleanupPaths[index] = path with
            { Registrations = path.Registrations.Where(registration => !ReferenceEquals(registration.Scope, scope)).ToImmutableArray() };
        }
    }

    private void DeferOverflowed(State state)
    {
        foreach (var node in state.OverflowedCleanups) { Defer(node); }
    }

    private static void CopyState(State target, State source)
    {
        target.Assigned.Clear(); target.Assigned.UnionWith(source.Assigned);
        target.Narrowed.Clear(); foreach (var pair in source.Narrowed) { target.Narrowed.Add(pair.Key, pair.Value); }
        target.Effects.Clear(); target.Effects.UnionWith(source.Effects);
        target.SuppressedEffects.Clear(); target.SuppressedEffects.UnionWith(source.SuppressedEffects);
        target.Fields.Clear(); target.Fields.UnionWith(source.Fields);
        target.CleanupPaths.Clear(); target.CleanupPaths.AddRange(source.CleanupPaths);
        target.OverflowedCleanups.Clear(); target.OverflowedCleanups.UnionWith(source.OverflowedCleanups);
        target.CleanupTrackingDeferred = source.CleanupTrackingDeferred;
        target.CleanupFactsInitialized = source.CleanupFactsInitialized;
        target.Reachable = source.Reachable;
        target.ExceptionSink = source.ExceptionSink;
    }
}
