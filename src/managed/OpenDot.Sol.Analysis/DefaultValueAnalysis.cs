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

public sealed record ParameterDefaultSet(ImmutableArray<CallDefaultArgument?> Parameters, bool IsComplete);

/// <summary>Declaration-owned default expressions, including inherited override contracts.</summary>
public sealed class DefaultValueAnalysis
{
    private readonly Dictionary<AstCallable, ParameterDefaultSet> _defaults = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AstNode, AstSnapshot> _files = new(ReferenceEqualityComparer.Instance);
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly HashSet<AstCallable> _active = new(ReferenceEqualityComparer.Instance);
    private readonly TypeSpecialization _types;
    private readonly LexicalAnalysis _lexical;
    private readonly CancellationToken _cancellation;

    private DefaultValueAnalysis(TypeAnalysis types, LexicalAnalysis lexical, CancellationToken cancellation)
    {
        Types = types;
        _types = new(types);
        _lexical = lexical;
        _cancellation = cancellation;
        foreach (var file in types.Declarations.Files)
        {
            foreach (var node in file.Nodes) { _files.Add(node, file); }
        }
        foreach (var callable in _files.Keys.OfType<AstCallable>())
        {
            Resolve(callable, 0);
            ValidateReferences(callable);
        }
        Diagnostics = _diagnostics.OrderBy(d => d.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Span.Start).ToImmutableArray();
    }

    public TypeAnalysis Types { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool HasErrors => Types.HasErrors || _lexical.HasErrors || !Diagnostics.IsEmpty;

    public static DefaultValueAnalysis Create(TypeAnalysis types, LexicalAnalysis lexical, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(lexical);
        if (!ReferenceEquals(types.Declarations, lexical.Declarations)) { throw new ArgumentException("Analyses must share a declaration session.", nameof(lexical)); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(types, lexical, cancellationToken);
    }

    public ParameterDefaultSet GetDefaults(AstCallable callable)
    {
        ArgumentNullException.ThrowIfNull(callable);
        return _defaults.TryGetValue(callable, out var result) ? result
            : throw new ArgumentException("The callable is not owned by this analysis.", nameof(callable));
    }

    private ParameterDefaultSet Resolve(AstCallable callable, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_defaults.TryGetValue(callable, out var previous)) { return previous; }
        var own = callable.Parameters.Select((p, index) => p.DefaultValue is null ? null : new CallDefaultArgument(index, callable, p.DefaultValue)).ToArray();
        if (depth > 128)
        {
            Report("SOL2138", "Inherited default resolution exceeded its nesting budget.", callable);
            return new(own.ToImmutableArray(), false);
        }
        if (!_active.Add(callable)) { return new(own.ToImmutableArray(), false); }
        bool complete = true;
        try
        {
            if (callable is AstFunction function && function.Modifiers.Contains("override"))
            {
                var inherited = FindOverridden(function).Select(baseFunction => Resolve(baseFunction, depth + 1)).ToArray();
                complete = inherited.Length != 0 && inherited.All(d => d.IsComplete);
                for (int index = 0; index < own.Length; index++)
                {
                    var origins = inherited.Select(d => d.Parameters[index]).OfType<CallDefaultArgument>()
                        .DistinctBy(d => (d.DeclaredBy, d.ParameterIndex)).ToArray();
                    if (origins.Length > 1)
                    {
                        Report("SOL2137", "Multiple inherited default expressions for parameter: " + function.Parameters[index].Name, function);
                        complete = false;
                    }
                    else if (own[index] is null && origins.Length == 1) { own[index] = origins[0] with { ParameterIndex = index }; }
                }
            }
        }
        catch (TypeSpecializationException error)
        {
            Report("SOL2138", error.Message, callable);
            complete = false;
        }
        finally { _active.Remove(callable); }
        var result = new ParameterDefaultSet(own.ToImmutableArray(), complete);
        _defaults.Add(callable, result);
        return result;
    }

    private IEnumerable<AstFunction> FindOverridden(AstFunction function)
    {
        if (string.IsNullOrWhiteSpace(function.Name)) { yield break; }
        var file = _files[function];
        AstNode? owner = file.GetParent(function);
        while (owner is not null && owner is not AstTypeDeclaration) { owner = file.GetParent(owner); }
        if (owner is not AstTypeDeclaration type || Types.Declarations.GetDeclaredSymbol(type) is not { } symbol) { yield break; }
        var self = _types.Nominal(symbol, Types.GetTypeParameters(type));
        var pending = new Stack<(ResolvedType Type, int Depth)>(_types.GetDirectBases(self, _cancellation).Select(t => (t, 0)));
        var visited = new HashSet<int>();
        while (pending.TryPop(out var frame))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (frame.Depth > 128) { throw new TypeSpecializationException(SpecializationFailure.ResourceLimit, "Inherited default lookup exceeded its nesting budget."); }
            var current = frame.Type;
            if (!visited.Add(current.Id) || current.Declaration is not { } definition) { continue; }
            bool matched = false;
            foreach (var candidate in definition.FindMembers(function.Name ?? "").SelectMany(s => s.Parts).OfType<AstFunction>())
            {
                if (!candidate.Modifiers.Contains("private") && Matches(function, candidate, current))
                {
                    matched = true;
                    yield return candidate;
                }
            }
            if (matched) { continue; }
            foreach (var parent in _types.GetDirectBases(current, _cancellation)) { pending.Push((parent, frame.Depth + 1)); }
        }
    }

    private bool Matches(AstFunction target, AstFunction candidate, ResolvedType owner)
    {
        if (target.Parameters.Length != candidate.Parameters.Length) { return false; }
        var methodParameters = Types.GetTypeParameters(candidate);
        var targetParameters = Types.GetTypeParameters(target);
        if (methodParameters.Length != targetParameters.Length) { return false; }
        var replacements = Types.GetTypeParameters(owner.Declaration!.Parts[0])
            .Select((p, index) => (Parameter: p, Value: owner.Arguments[index])).ToDictionary(p => p.Parameter, p => p.Value);
        for (int index = 0; index < methodParameters.Length; index++) { replacements[methodParameters[index]] = targetParameters[index]; }
        for (int index = 0; index < target.Parameters.Length; index++)
        {
            var left = target.Parameters[index];
            var right = candidate.Parameters[index];
            if (left.PassingMode != right.PassingMode || left.Type is null || right.Type is null) { return false; }
            var actual = Types.GetType(left.Type);
            var expected = _types.Substitute(Types.GetType(right.Type), replacements, _cancellation);
            if (actual.IsError || expected.IsError || !ReferenceEquals(actual, expected)) { return false; }
        }
        return true;
    }

    private void ValidateReferences(AstCallable callable)
    {
        for (int index = 0; index < callable.Parameters.Length; index++)
        {
            var parameter = callable.Parameters[index];
            if (parameter.DefaultValue is not { } expression) { continue; }
            var unavailable = callable.Parameters.Skip(index).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in expression.Descendants().Prepend(expression).OfType<AstAtom>()
                .Where(n => n.SyntaxKind == NodeKind.NameExpression && unavailable.Contains(n.Spelling)))
            {
                _cancellation.ThrowIfCancellationRequested();
                var lookup = _lexical.Lookup(name, name.Spelling);
                bool ownProperty = lookup.Locals.Any(l => l.Kind == LocalSymbolKind.ConstructorProperty
                    && callable.Parameters.Any(p => ReferenceEquals(p.NameOrigin.Element, l.Origin.Element)));
                bool qualifier = _files[callable].Root.Imports.Any(i => i.Alias == name.Spelling
                    || i.Alias is null && i.ModuleName.Split('.')[0] == name.Spelling);
                if ((!qualifier && lookup.IsMissing) || ownProperty)
                {
                    Report("SOL2136", "A default expression cannot read its own or a later parameter: " + name.Spelling, name);
                }
            }
        }
    }

    private void Report(string code, string message, AstNode origin)
    {
        if (_diagnostics.Count < Types.Declarations.Options.DiagnosticLimit)
        {
            _diagnostics.Add(new(code, message, origin.Origin.Source, origin.Origin.Span));
        }
    }
}
