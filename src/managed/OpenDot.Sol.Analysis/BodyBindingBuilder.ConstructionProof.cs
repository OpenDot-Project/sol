// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private sealed record ConstructionFrame(ResolvedType Owner, AstConstructor? Constructor, ResolvedType Receiver,
        bool Helper = false, bool OwnReceiver = true);
    private sealed class ConstructionSession
    {
        internal HashSet<(int Owner, AstConstructor? Constructor)> Chain { get; } = [];
        internal HashSet<(int Owner, AstConstructor? Constructor)> Allocations { get; } = [];
        internal HashSet<(int Owner, AstCallable Constructor)> ActiveDefaults { get; } = [];
        internal List<ConstructionOrigin> Origins { get; } = [];
        internal int Instances;
        internal int Edges;
        internal HashSet<(int Owner, AstConstructor? Constructor)> ConstructorInstances { get; } = [];
        internal HashSet<(AstNode Site, int Owner, AstCallable? Target)> Dependencies { get; } = [];
        internal Dictionary<HelperInstance, HelperApproximation> Helpers { get; } = [];
        internal HashSet<HelperInstance> ActiveHelpers { get; } = [];
        internal bool RecursiveHelpers;
        internal HashSet<AstExpression> ActiveCallbacks { get; } = [];
        internal HashSet<(AstExpression Expression, int Owner, int Signature)> CallbackInstances { get; } = [];
        internal Dictionary<HelperDeclaration, HelperDependencies> HelperDependencies { get; } = [];
        internal Dictionary<HelperDeclaration, ImmutableArray<ResolvedType>> HelperTypeArguments { get; } = [];
        internal Dictionary<HelperDeclaration, ImmutableArray<HelperDeclaration>> RecursiveGroups { get; } = [];
    }
    private ConstructionFrame? _constructionFrame;
    private ConstructionSession? _constructionSession;
    internal Func<ResolvedType, AstConstructor?, BodyBindingStatus?>? ConstructionSourceStatus { get; set; }
    internal readonly Dictionary<(AstNode Site, int Owner, AstConstructor? Constructor), ConstructionProof> ConstructionProofs = [];

    private BodyBindingBuilder ConstructionWorker(ResolvedType owner, ConstructionFrame? frame, ConstructionSession session)
    {
        Types.ValidateOwnership(owner);
        _cancellation.ThrowIfCancellationRequested();
        var worker = new BodyBindingBuilder(Sources, Types, _cancellation)
        {
            ClosedMemberReturn = ClosedMemberReturn, ClosedOwnerReturnTemplate = ClosedOwnerReturnTemplate,
            ClosedLexicalReturn = ClosedLexicalReturn, InterfaceSourceBody = InterfaceSourceBody,
            ConstructionSourceStatus = ConstructionSourceStatus,
            _constructionFrame = frame, _constructionSession = session,
            _discoveringConstructionDependencies = _discoveringConstructionDependencies,
            _interfaceInitializationActive = _interfaceInitializationActive,
            _bodySubstitution = new(_bodySubstitution),
        };
        foreach (var pair in Substitution(owner)) { worker._bodySubstitution[pair.Key] = pair.Value; }
        foreach (var pair in _localTypes) { worker._localTypes[pair.Key] = pair.Value; }
        worker._interfaceResolutionActive.UnionWith(_interfaceResolutionActive);
        worker._interfaceResolutionActive.Add(owner.Id);
        return worker;
    }

    private void ReplayConstruction(BodyBindingBuilder worker, AstNode? dependencySite = null)
    {
        foreach (var (node, code) in worker._reported)
        {
            var diagnostic = worker.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Code == code
                && diagnostic.Span == node.Origin.Span && ReferenceEquals(diagnostic.Source, node.Origin.Source));
            if (diagnostic is not null)
            {
                bool prior = _replayingConstruction;
                _replayingConstruction = true;
                try { Report(code, diagnostic.Message, node); }
                finally { _replayingConstruction = prior; }
            }
        }
        foreach (var gap in worker._deferred) { Defer(dependencySite ?? gap); }
        foreach (var pair in worker.ConstructionProofs) { ConstructionProofs[pair.Key] = pair.Value; }
    }

    private static IEnumerable<AstConstructor> Constructors(ResolvedType owner) =>
        owner.Declaration?.Parts.OfType<AstTypeDeclaration>().Select(part => part.PrimaryConstructor).OfType<AstConstructor>()
            .Concat(owner.Declaration.Members.SelectMany(member => member.Parts).OfType<AstConstructor>()) ?? [];

    private static bool RuntimeConstructionType(ResolvedType owner) =>
        owner.Declaration is { ModuleName: "std.core", Name: "String", Arity: 0 };

    private void ValidateConstructionDeclarations()
    {
        foreach (var declaration in Files.Keys.OfType<AstTypeDeclaration>().Where(type => type.SyntaxKind
            is NodeKind.ClassDeclaration or NodeKind.StructDeclaration or NodeKind.RefStructDeclaration))
        {
            _cancellation.ThrowIfCancellationRequested();
            var owner = ContainingDeclaredType(declaration);
            if (owner is null || owner.Declaration!.Parts[0] != declaration) { continue; }
            // String storage and its length projection belong to the runtime, not a source field.
            if (RuntimeConstructionType(owner)) { continue; }
            foreach (var constructor in Constructors(owner).Cast<AstConstructor?>().DefaultIfEmpty())
            {
                if (constructor is not null) { ValidateEscapingParameterAnnotations(constructor, constructor.Parameters); }
                var proof = ProveConstruction(owner, constructor, constructor ?? (AstNode)declaration, [], [], new State());
                if (constructor is not null)
                { Bodies[constructor] = new(constructor, Empty, proof.CanCompleteNormally, proof.Status, proof.Effects); }
            }
        }
    }

    private ConstructionProof ProveConstruction(ResolvedType owner, AstConstructor? constructor, AstNode site,
        ImmutableArray<CallArgumentBinding> arguments, ImmutableArray<int> defaults, State caller, int? originStart = null)
    {
        _cancellation.ThrowIfCancellationRequested();
        Types.ValidateOwnership(owner);
        if (!Files.ContainsKey(site)) { throw new ArgumentException("Construction site belongs to another workspace.", nameof(site)); }
        var session = _constructionSession ?? new ConstructionSession();
        originStart ??= session.Origins.Count;
        var selection = new ConstructionSelection(site, owner, constructor, arguments, defaults, false);
        if (RuntimeConstructionType(owner))
        {
            Defer(site);
            return new(selection, BodyBindingStatus.Deferred, true, ImmutableDictionary<ConstructionSlot, ConstructionAssignment>.Empty,
                [], [], "Runtime-owned storage has no ordinary source constructor proof.");
        }
        if (session.Allocations.Count + session.Chain.Count + session.ActiveHelpers.Count >= 128
            || !session.Allocations.Add((owner.Id, constructor)))
        {
            Defer(site);
            return new(selection, BodyBindingStatus.Deferred, true, ImmutableDictionary<ConstructionSlot, ConstructionAssignment>.Empty,
                [], [], "Recursive allocation has no finite construction proof.");
        }
        try
        {
            var frame = new ConstructionFrame(owner, constructor, owner);
            var worker = ConstructionWorker(owner, frame, session);
            var state = new State { ExceptionSink = [] };
            worker.ExecuteConstructor(owner, constructor, site, state, 0);
            var status = worker._errorGeneration != 0 ? BodyBindingStatus.Invalid
                : worker._deferred.Count != 0 || Sources.HasErrors ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound;
            if (ConstructionSourceStatus?.Invoke(owner, constructor) == BodyBindingStatus.Invalid)
            { status = BodyBindingStatus.Invalid; }
            var proof = new ConstructionProof(selection, status, state.Reachable, state.Construction.Slots,
                session.Origins.Skip(originStart.Value).ToImmutableArray(), state.Effects.ToImmutableArray(), status == BodyBindingStatus.Bound ? null : "Construction proof is incomplete or invalid.");
            if (site == constructor || site is AstTypeDeclaration)
            {
                foreach (var pair in worker.Expressions) { Expressions[pair.Key] = pair.Value; }
                foreach (var pair in worker.Calls) { Calls[pair.Key] = pair.Value; }
                foreach (var pair in worker.LambdaBodies) { LambdaBodies[pair.Key] = pair.Value; }
                foreach (var pair in worker.AnonymousFunctionBodies) { AnonymousFunctionBodies[pair.Key] = pair.Value; }
            }
            ReplayConstruction(worker);
            ConstructionProofs[(site, owner.Id, constructor)] = proof;
            if (status == BodyBindingStatus.Invalid && site is AstCall)
            { Report("SOL2277", "The selected constructor has an invalid construction dependency.", site); }
            if (status != BodyBindingStatus.Bound) { Defer(site); }
            // The callee's locals and field identities cannot be copied into the caller's state.
            foreach (var edge in state.ExceptionSink ?? [])
            { caller.RecordExceptionalEdge(edge.Type, caller, edge.Origin); }
            caller.Effects.UnionWith(state.Effects);
            return proof;
        }
        finally { session.Allocations.Remove((owner.Id, constructor)); }
    }

    private void ExecuteConstructor(ResolvedType owner, AstConstructor? constructor, AstNode site, State state, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        var session = _constructionSession!;
        var key = (owner.Id, constructor);
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        { Defer(site); MarkConstructionUnknown(state); return; }
        session.ConstructorInstances.Add(key);
        session.Dependencies.Add((site, owner.Id, constructor));
        session.Instances = session.ConstructorInstances.Count + session.Helpers.Count + session.CallbackInstances.Count;
        session.Edges = session.Dependencies.Count;
        if (depth >= 128 || session.Instances > 1024 || session.Edges > 8192)
        { Defer(site); MarkConstructionUnknown(state); return; }
        if (!session.Chain.Add(key))
        { Report("SOL2275", "Constructor delegation contains a cycle.", site); return; }
        try
        {
            var previous = _constructionFrame;
            _constructionFrame = new(owner, constructor, previous?.Receiver ?? owner);
            try { ExecuteConstructorCore(owner, constructor, site, state, depth); }
            finally { _constructionFrame = previous; }
        }
        finally { session.Chain.Remove(key); }
    }

    private void ExecuteConstructorCore(ResolvedType owner, AstConstructor? constructor, AstNode site, State state, int depth)
    {
        var parts = owner.Declaration!.Parts.OfType<AstTypeDeclaration>().ToArray();
        var initializedParts = parts.Where(part => part.HasInitialization).ToArray();
        if (initializedParts.Length > 1)
        { Report("SOL2276", "Only one partial part may own construction and initialization.", initializedParts[1]); return; }
        var nodes = (constructor?.Descendants() ?? []).Concat(InitializationMembers(owner)
            .SelectMany(member => member.Descendants())).Take(65537).Count();
        if (nodes > 65536) { Defer(site); MarkConstructionUnknown(state); return; }
        SeedConstructionParameters(constructor, state);
        var exits = new List<State>();
        var context = new Context(constructor, Empty, Exits: exits);
        var primary = parts.Select(part => part.PrimaryConstructor).OfType<AstConstructor>().FirstOrDefault();
        bool delegatesThis = constructor?.Delegation?.Children.OfType<AstAtom>().FirstOrDefault()?.Spelling == "this";
        if (constructor is not null && constructor != primary && primary is not null && !delegatesThis)
        { Report("SOL2274", "A secondary constructor must delegate to the primary initialization chain.", constructor); return; }
        if (delegatesThis)
        {
            var delegation = constructor!.Delegation!;
            ExecuteConstructionEdge(owner, delegation, ConstructionArguments(delegation), state, context, depth, true);
        }
        else
        {
            var concreteBase = Types.GetDirectBases(owner, _cancellation).FirstOrDefault(type =>
                type.Declaration?.Parts[0].SyntaxKind == NodeKind.ClassDeclaration);
            var writtenBase = parts.SelectMany(part => part.Bases).FirstOrDefault(entry =>
                entry.Children.OfType<AstType>().FirstOrDefault() is { } type && ReferenceEquals(Written(type), concreteBase));
            var delegation = constructor?.Delegation ?? writtenBase;
            if (concreteBase is not null)
            {
                bool explicitBase = constructor?.Delegation is not null
                    || writtenBase?.Children.Any(child => child.SyntaxKind == NodeKind.ArgumentList) == true;
                ExecuteConstructionEdge(concreteBase, delegation ?? site,
                    explicitBase && delegation is not null ? ConstructionArguments(delegation) : [], state, context, depth, false, !explicitBase);
            }
            else if (constructor?.Delegation is not null)
            { Report("SOL2274", "There is no concrete base constructor to delegate to.", constructor.Delegation); }
            if (state.Reachable)
            {
                foreach (var slot in ConstructionSlots(owner).Where(slot => slot.Kind == ConstructionSlotKind.PrimaryProperty))
                { AssignConstructionSlot(slot, site, state, initialization: true); }
                InitializeConstructionDelegates(owner, state, context);
                foreach (var member in InitializationMembers(owner))
                {
                    if (!state.Reachable) { break; }
                    if (member is AstProperty property && property.Initializer is { } initializer)
                    {
                        ConstructionPhase(initializer, "field initializer", state, () =>
                        {
                            var expected = property.Type is null ? null : Written(property.Type);
                            Require(Expression(initializer, state, context, expected), expected, initializer, state);
                        });
                        if (state.Reachable) { AssignConstructionSlot(PropertySlot(property, owner), property, state, true); }
                    }
                    else if (member is AstProperty { Delegate: not null } delegated)
                    {
                        ConstructionPhase(delegated, "property delegate", state, () =>
                        {
                            var contract = Delegate(delegated, state, context);
                            if (contract is null) { Defer(delegated); }
                            else
                            {
                                state.Effects.UnionWith(contract.InitEffects);
                                if (state.Reachable) { AssignConstructionSlot(PropertySlot(delegated, owner), delegated, state, true); }
                            }
                        });
                    }
                    else if (member is AstInitializer { Body: { } block })
                    { ConstructionPhase(member, "init block", state, () => Statements(block.Statements, state, context, false)); }
                }
            }
        }
        if (state.Reachable && constructor?.Body is { } body)
        { ConstructionPhase(constructor, "constructor body", state, () => Statements(body.Statements, state, context, false)); }
        var normal = exits.Where(exit => exit.Reachable).ToList();
        if (state.Reachable) { normal.Add(state.Clone()); }
        foreach (var exit in normal)
        {
            foreach (var slot in ConstructionSlots(owner))
            {
                if (exit.Construction.Slots.GetValueOrDefault(slot) != ConstructionAssignment.Assigned)
                {
                    if (exit.Construction.Slots.GetValueOrDefault(slot) == ConstructionAssignment.Unknown && _deferred.Count != 0)
                    { Defer(site); }
                    else { Report("SOL2270", "Construction leaves required storage unassigned: " + slot.Name, site); }
                }
            }
            if (constructor is not null) { CheckOut(exit, context, constructor); }
        }
        if (normal.Count != 0)
        {
            var joined = normal[0].Clone();
            foreach (var exit in normal.Skip(1)) { var left = joined.Clone(); joined.Join(left, exit); }
            joined.Effects.UnionWith(state.Effects);
            CopyState(state, joined);
        }
        else { state.Reachable = false; }
        if (constructor is not null)
        {
            var budget = constructor.ThrownTypes.Select(Written).ToArray();
            foreach (var effect in budget) { CheckThrowable(effect, constructor); }
            foreach (var effect in state.Effects.Where(IsChecked))
            {
                if (!budget.Any(type => Converts(effect, type)))
                { Report("SOL2250", "Constructor initialization effect is not declared: " + effect.Name, constructor); }
            }
        }
    }

    private void SeedConstructionParameters(AstCallable? callable, State state)
    {
        if (callable is null) { return; }
        var before = CaptureCleanupFacts(state);
        if (callable is AstAccessor accessor && Ancestors(accessor).OfType<AstIndexer>().FirstOrDefault() is { } indexer)
        {
            SeedConstructionParameters(indexer, state);
            if (indexer.ValueType is { } valueType)
            {
                foreach (var local in OwnLocals(accessor).Where(local => local.Kind == LocalSymbolKind.AccessorValue))
                { _localTypes[local] = Written(valueType); }
            }
        }
        foreach (var local in Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols).Where(local =>
            local.Kind is LocalSymbolKind.Parameter or LocalSymbolKind.ConstructorProperty
            && callable.Parameters.Any(parameter => ReferenceEquals(parameter.NameOrigin.Element, local.Origin.Element)))
            .Concat(OwnLocals(callable).Where(local => local.Kind == LocalSymbolKind.AccessorValue)))
        {
            if (local.WrittenType is { } type)
            { _localTypes[local] = local.PassingMode == AstPassingMode.Vararg ? Types.GetArray(Written(type)) : Written(type); }
            if (local.PassingMode != AstPassingMode.Out) { state.Assigned.Add(local); }
        }
        UpdateCleanupFacts(state, before, CaptureCleanupFacts(state));
    }

    private void ConstructionOriginAt(AstNode site, string phase, State state) =>
        _constructionSession!.Origins.Add(new(site, phase, state.Effects.ToImmutableArray()));

    private void ConstructionPhase(AstNode site, string phase, State state, Action action)
    {
        var before = CaptureCleanupFacts(state);
        var prior = state.Effects.ToArray();
        state.Effects.Clear();
        try { action(); UpdateCleanupFacts(state, before, CaptureCleanupFacts(state)); ConstructionOriginAt(site, phase, state); }
        finally { state.Effects.UnionWith(prior); }
    }

    private void InitializeConstructionDelegates(ResolvedType owner, State state, Context context)
    {
        foreach (var entry in owner.Declaration!.Parts.OfType<AstTypeDeclaration>().SelectMany(part => part.Bases))
        {
            if (!state.Reachable) { break; }
            var expression = entry.Children.FirstOrDefault(child => child.SyntaxKind == NodeKind.Delegation)?
                .Children.OfType<AstExpression>().FirstOrDefault();
            if (expression is null) { continue; }
            var type = entry.Children.OfType<AstType>().FirstOrDefault();
            var expected = type is null ? null : Written(type);
            ConstructionPhase(entry, "interface delegate", state, () =>
                Require(Expression(expression, state, context, expected), expected, expression, state));
            if (ConstructionAlias(expression, state))
            { Defer(expression); }
            if (state.Reachable)
            { AssignConstructionSlot(new(entry, owner, entry.Origin.Text, ConstructionSlotKind.InterfaceDelegate, false), entry, state, true); }
        }
    }
}
