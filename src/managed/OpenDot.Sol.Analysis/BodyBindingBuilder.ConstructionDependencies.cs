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
    private sealed record HelperDeclaration(AstCallable Function, ResolvedType Owner, string Arguments);
    private sealed record HelperDependencies(ImmutableArray<HelperDeclaration> Targets, BodyBindingStatus Status);
    private bool _discoveringConstructionDependencies;

    private BodyBindingStatus DiscoverConstructionDependencies(AstCallable root, ResolvedType owner,
        ImmutableArray<ResolvedType> arguments, AstNode site)
    {
        var session = _constructionSession!;
        _cancellation.ThrowIfCancellationRequested();
        if (!Files.ContainsKey(root) || !Files.ContainsKey(site))
        { throw new ArgumentException("Construction dependency belongs to another source workspace.", nameof(root)); }
        if (_discoveringConstructionDependencies) { return BodyBindingStatus.Bound; }
        var rootKey = new HelperDeclaration(root, owner, string.Join(",", arguments.Select(type => type.Id)));
        var pending = new Queue<(HelperDeclaration Key, ImmutableArray<ResolvedType> Arguments, int Depth)>();
        var visited = new HashSet<HelperDeclaration>();
        pending.Enqueue((rootKey, arguments, 0));
        var status = BodyBindingStatus.Bound;
        int edges = 0;
        while (pending.TryDequeue(out var current))
        {
            _cancellation.ThrowIfCancellationRequested();
            Types.ValidateOwnership(current.Key.Owner);
            foreach (var argument in current.Arguments) { Types.ValidateOwnership(argument); }
            if (!visited.Add(current.Key)) { continue; }
            if (current.Depth >= 128 || visited.Count > 1024 || edges > 8192)
            { Defer(site); return BodyBindingStatus.Deferred; }
            if (!session.HelperDependencies.TryGetValue(current.Key, out var dependency))
            {
                var function = current.Key.Function;
                if (ConstructionHelperBody(function).Kind == AstBodyKind.Missing
                    || function.Descendants().Take(65537).Count() > 65536)
                { Defer(site); return BodyBindingStatus.Deferred; }
                var worker = ConstructionWorker(current.Key.Owner, null, session);
                worker._discoveringConstructionDependencies = true;
                foreach (var pair in Sources.Types.GetTypeParameters(function).Zip(current.Arguments))
                { worker._bodySubstitution[pair.First] = pair.Second; }
                var body = worker.CheckBody(function);
                var targets = worker.Calls.Values.Where(call => call.Target is AstFunction or AstAccessor)
                    .Select(call => new HelperDeclaration(call.Target, call.Receiver ?? current.Key.Owner,
                        string.Join(",", call.TypeArguments.Select(type => type.Id)))).Distinct().ToImmutableArray();
                dependency = new(targets, body.Status);
                session.HelperDependencies[current.Key] = dependency;
                foreach (var target in targets)
                {
                    var call = worker.Calls.Values.First(call => call.Target == target.Function
                        && (call.Receiver ?? current.Key.Owner) == target.Owner
                        && string.Join(",", call.TypeArguments.Select(type => type.Id)) == target.Arguments);
                    session.HelperTypeArguments[target] = call.TypeArguments;
                }
            }
            if (dependency.Status == BodyBindingStatus.Invalid) { status = BodyBindingStatus.Invalid; }
            else if (dependency.Status == BodyBindingStatus.Deferred && status != BodyBindingStatus.Invalid)
            { status = BodyBindingStatus.Deferred; }
            edges += dependency.Targets.Length;
            foreach (var target in dependency.Targets)
            { pending.Enqueue((target, session.HelperTypeArguments.GetValueOrDefault(target, []), current.Depth + 1)); }
        }
        if (edges > 8192) { Defer(site); return BodyBindingStatus.Deferred; }
        MarkConstructionRecursiveGroups(visited);
        return status;
    }

    private void MarkConstructionRecursiveGroups(HashSet<HelperDeclaration> reachable)
    {
        var session = _constructionSession!;
        var order = new List<HelperDeclaration>();
        var seen = new HashSet<HelperDeclaration>();
        var stack = new Stack<(HelperDeclaration Node, bool Finish)>();
        foreach (var root in reachable)
        {
            stack.Push((root, false));
            while (stack.TryPop(out var item))
            {
                _cancellation.ThrowIfCancellationRequested();
                if (item.Finish) { order.Add(item.Node); continue; }
                if (!seen.Add(item.Node)) { continue; }
                stack.Push((item.Node, true));
                foreach (var target in session.HelperDependencies[item.Node].Targets)
                { if (reachable.Contains(target)) { stack.Push((target, false)); } }
            }
        }
        var reverse = reachable.ToDictionary(node => node, _ => new List<HelperDeclaration>());
        foreach (var node in reachable)
        {
            foreach (var target in session.HelperDependencies[node].Targets)
            { if (reverse.TryGetValue(target, out var incoming)) { incoming.Add(node); } }
        }
        seen.Clear();
        foreach (var root in order.AsEnumerable().Reverse())
        {
            if (!seen.Add(root)) { continue; }
            var component = new List<HelperDeclaration>();
            var pending = new Stack<HelperDeclaration>(); pending.Push(root);
            while (pending.TryPop(out var node))
            {
                _cancellation.ThrowIfCancellationRequested();
                component.Add(node);
                foreach (var predecessor in reverse[node])
                { if (seen.Add(predecessor)) { pending.Push(predecessor); } }
            }
            if (component.Count > 1 || session.HelperDependencies[root].Targets.Contains(root))
            {
                var group = component.ToImmutableArray();
                foreach (var node in component) { session.RecursiveGroups[node] = group; }
            }
        }
    }
}
