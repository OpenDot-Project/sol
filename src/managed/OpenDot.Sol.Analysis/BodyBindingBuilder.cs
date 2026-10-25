// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    internal sealed class State
    {
        internal sealed record ExceptionEdge(ResolvedType Type, State State);

        internal HashSet<LocalSymbol> Assigned { get; } = [];
        internal Dictionary<LocalSymbol, ResolvedType> Narrowed { get; } = [];
        internal HashSet<ResolvedType> Effects { get; } = [];
        internal HashSet<AstProperty> Fields { get; } = [];
        internal List<ExceptionEdge>? ExceptionSink { get; set; }
        internal bool Reachable { get; set; } = true;
        internal State Clone()
        {
            var copy = new State { Reachable = Reachable, ExceptionSink = ExceptionSink };
            copy.Assigned.UnionWith(Assigned);
            foreach (var pair in Narrowed) { copy.Narrowed.Add(pair.Key, pair.Value); }
            copy.Effects.UnionWith(Effects);
            copy.Fields.UnionWith(Fields);
            return copy;
        }

        internal void RecordExceptionalEdge(ResolvedType type, State? snapshot = null)
        {
            if (ExceptionSink is not { } sink || snapshot is null && !Reachable) { return; }
            var edge = (snapshot ?? this).Clone();
            edge.Reachable = true;
            edge.ExceptionSink = null;
            sink.Add(new(type, edge));
        }

        internal void Join(State left, State right)
        {
            Assigned.Clear(); Narrowed.Clear(); Fields.Clear();
            var live = new[] { left, right }.Where(s => s.Reachable).ToArray();
            Reachable = live.Length != 0;
            if (live.Length != 0)
            {
                Assigned.UnionWith(live[0].Assigned);
                Fields.UnionWith(live[0].Fields);
                foreach (var next in live.Skip(1)) { Assigned.IntersectWith(next.Assigned); }
                foreach (var next in live.Skip(1)) { Fields.IntersectWith(next.Fields); }
                foreach (var pair in live[0].Narrowed)
                {
                    if (live.All(s => s.Narrowed.GetValueOrDefault(pair.Key) == pair.Value)) { Narrowed.Add(pair.Key, pair.Value); }
                }
            }
            Effects.UnionWith(left.Effects); Effects.UnionWith(right.Effects);
        }
    }

    private sealed record Value(ResolvedType? Type, bool Writable = false, bool Storage = false,
        LocalSymbol? Local = null, DeclarationSymbol? Symbol = null, ResolvedType? Receiver = null,
        string? Literal = null, bool IsType = false);
    private sealed record Context(AstCallable? Callable, ResolvedType? Result, AstLambda? Lambda = null,
        ResolvedType? LambdaType = null, int LoopDepth = 0, bool Cleanup = false,
        List<State>? Exits = null, List<State>? Breaks = null);
    private readonly CancellationToken _cancellation;
    private readonly MemberLookupResolver _memberResolver;
    private readonly GenericInferenceSolver _genericInference;
    private readonly Dictionary<LocalSymbol, ResolvedType> _localTypes = [];
    private readonly HashSet<AstCallable> _active = [];
    private readonly HashSet<(AstNode, string)> _reported = [];
    private readonly HashSet<AstNode> _deferred = [];
    private int _depth;
    internal readonly Dictionary<AstNode, AstSnapshot> Files = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<AstExpression, ExpressionBinding> Expressions = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<AstCallable, BodySummary> Bodies = new(ReferenceEqualityComparer.Instance);
    internal readonly List<Diagnostic> Diagnostics = [];
    internal readonly Dictionary<AstCall, BodyCallBinding> Calls = new(ReferenceEqualityComparer.Instance);
    internal SourceAnalysis Sources { get; }
    internal TypeSpecialization Types { get; }
    internal MemberLookupResolver MemberResolver => _memberResolver;
    internal GenericInferenceSolver GenericInference => _genericInference;
    internal bool Deferred => _deferred.Count != 0;
    internal IEnumerable<AstNode> Unverified => _deferred;
    private ResolvedType Empty => Types.GetTuple([]);
    private ResolvedType Bool => Types.GetPrimitive("bool");

    internal BodyBindingBuilder(SourceAnalysis sources, CancellationToken cancellation)
    {
        Sources = sources; Types = new(sources.Types); _cancellation = cancellation;
        _memberResolver = new(sources, Types, cancellation);
        _genericInference = new(Types, cancellation);
        foreach (var file in sources.Declarations.Files)
        {
            foreach (var node in file.Nodes) { Files.Add(node, file); }
        }
    }

    internal void Build()
    {
        foreach (var declaration in Files.Keys.OfType<AstCallable>()) { CheckBody(declaration); }
        foreach (var property in Files.Keys.OfType<AstProperty>().Where(p => !Ancestors(p).Any(n => n is AstCallable or AstLambda)))
        {
            var state = new State();
            var expected = property.Type is null ? null : Written(property.Type);
            if (property.Initializer is { } initializer)
            {
                var value = Expression(initializer, state, new(null, expected), expected);
                Require(value, expected, initializer);
            }
            if (property.Delegate is not null)
            {
                var contract = Delegate(property);
                if (contract is null || !contract.InitEffects.IsEmpty) { Defer(property); }
            }
        }
        ValidateOverrides();
        ValidateOperatorContracts();
        ValidateStorage();
    }

    private BodySummary CheckBody(AstCallable callable)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (Bodies.TryGetValue(callable, out var previous)) { return previous; }
        var body = callable switch
        {
            AstFunction f => f.Body,
            AstAccessor a => a.Body,
            AstConstructor c => new AstBody(c.Body is null ? AstBodyKind.Missing : AstBodyKind.Block, c.Body),
            _ => new AstBody(AstBodyKind.Missing, null),
        };
        ResolvedType? result = callable.ReturnType is not null ? Written(callable.ReturnType)
            : callable is AstAccessor accessor && ParentProperty(accessor) is { } owner
                ? accessor.Name == "get" && owner.Type is not null ? Written(owner.Type) : Empty
                : body.Kind == AstBodyKind.Expression ? null : Empty;
        if (!_active.Add(callable))
        {
            Report("SOL2200", "Recursive return inference requires a return annotation.", callable);
            return new(callable, result, true, BodyBindingStatus.Invalid, []);
        }
        int errors = Diagnostics.Count, gaps = _deferred.Count;
        var state = new State();
        var exits = new List<State>();
        var context = new Context(callable, result, Exits: exits);
        try
        {
            foreach (var local in OwnLocals(callable).Where(l => l.Kind is LocalSymbolKind.Parameter or LocalSymbolKind.AccessorValue))
            {
                var parameter = callable.Parameters.FirstOrDefault(p => ReferenceEquals(p.NameOrigin.Element, local.Origin.Element));
                if (local.WrittenType is not null)
                {
                    var type = Written(local.WrittenType);
                    _localTypes[local] = parameter?.PassingMode == AstPassingMode.Vararg ? Types.GetArray(type) : type;
                }
                if (parameter?.PassingMode != AstPassingMode.Out) { state.Assigned.Add(local); }
            }
            foreach (var parameter in callable.Parameters.Where(p => p.DefaultValue is not null))
            {
                if (parameter.DefaultValue is AstExpression expression)
                {
                    var target = parameter.Type is null ? null : Written(parameter.Type);
                    if (target is not null && parameter.PassingMode == AstPassingMode.Vararg) { target = Types.GetArray(target); }
                    Require(Expression(expression, state, context, target), target, expression);
                }
            }
            if (callable is AstConstructor constructor) { InitializeFields(constructor, state, context); }
            if (body.Kind == AstBodyKind.Expression && body.Node is AstExpression expressionBody)
            {
                var value = Expression(expressionBody, state, context, result);
                result ??= value.Type;
                Require(value, result, expressionBody, state);
                if (value.Type?.IsRefLike == true) { CheckEscape(value, expressionBody, context); }
                CheckOut(state, context, callable);
            }
            else if (body.Node is AstBlock block)
            {
                Statements(block.Statements, state, context, true);
                if (state.Reachable) { CheckOut(state, context, callable); }
            }
            else if (body.Kind != AstBodyKind.Missing) { Defer(callable); }
            foreach (var exit in exits) { CheckOut(exit, context, callable); }
            if (state.Reachable) { CheckInitialized(state, context, callable); }
            foreach (var exit in exits) { CheckInitialized(exit, context, callable); }
            if (callable.ReturnsByReference) { Defer(callable); }
            foreach (var exit in exits) { state.Effects.UnionWith(exit.Effects); }
            var escaping = state.Effects.Where(IsChecked).ToImmutableArray();
            var budget = callable.ThrownTypes.Select(Written).ToArray();
            foreach (var effect in escaping)
            {
                if (!budget.Any(t => Converts(effect, t))) { Report("SOL2250", "Checked exception is not declared: " + effect.Name, callable); }
            }
            foreach (var effect in budget) { CheckThrowable(effect, callable); }
            var status = Diagnostics.Count != errors ? BodyBindingStatus.Invalid
                : _deferred.Count != gaps || result is null ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound;
            var summary = new BodySummary(callable, result, state.Reachable, status, escaping);
            Bodies.Add(callable, summary);
            return summary;
        }
        finally { _active.Remove(callable); }
    }

    private IEnumerable<AstNode> Ancestors(AstNode node)
    {
        for (var parent = Files[node].GetParent(node); parent is not null; parent = Files[node].GetParent(parent)) { yield return parent; }
    }
    private IEnumerable<LocalSymbol> OwnLocals(AstNode node) => Sources.Lexical.Scopes.Where(s => ReferenceEquals(s.Owner, node)).SelectMany(s => s.Symbols);
    private AstProperty? ParentProperty(AstNode node) => Ancestors(node).OfType<AstProperty>().FirstOrDefault();
    private ResolvedType Written(AstType type) => Sources.Types.GetType(type);
    private ResolvedType? Core(string name) => Sources.Declarations.Modules.GetValueOrDefault("std.core")?.Declarations
        .Where(s => s.Kind == SymbolKind.Type && s.Name == name && s.Arity == 0).Select(s => Types.Nominal(s, [])).SingleOrDefault();
    private bool Converts(ResolvedType source, ResolvedType target) => Types.ClassifyConversion(source, target, _cancellation) != IntrinsicConversion.None;
    private void Report(string code, string message, AstNode node)
    {
        if (_reported.Add((node, code)) && Diagnostics.Count < Sources.Declarations.Options.DiagnosticLimit)
        { Diagnostics.Add(new(code, message, node.Origin.Source, node.Origin.Span)); }
    }
    private void Defer(AstNode node) { _deferred.Add(node); }
    private bool IsChecked(ResolvedType type) => Core("RuntimeException") is not { } runtime || !Converts(type, runtime);
    private void CheckThrowable(ResolvedType type, AstNode node)
    {
        if (Core("Throwable") is { } root && !Converts(type, root)) { Report("SOL2251", "An exception type must derive from Throwable.", node); }
        else if (Core("Throwable") is null) { Defer(node); }
    }
}
