// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public enum SpecializationFailure { ResourceLimit, InvalidType }

/// <summary>A bounded specialization failure; callers translating source must issue a diagnostic.</summary>
public sealed class TypeSpecializationException : Exception
{
    internal TypeSpecializationException(SpecializationFailure failure, string message) : base(message)
    {
        Failure = failure;
    }
    public SpecializationFailure Failure { get; }
}

/// <summary>
/// An owned, synchronized type interner extending a written-type analysis without mutating it.
/// Identities are local to this workspace; new types must not be passed to its input analysis.
/// </summary>
public sealed partial class TypeSpecialization
{
    private readonly object _gate = new();
    private readonly List<ResolvedType> _types;
    private readonly Dictionary<string, ResolvedType> _interned = new(StringComparer.Ordinal);
    private readonly Dictionary<AstDeclaration, int> _ownerIds = new(ReferenceEqualityComparer.Instance);
    private readonly int _instanceLimit;
    private readonly int _depthLimit;

    private readonly struct GateLease(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    public TypeSpecialization(TypeAnalysis analysis, int instanceLimit = 100000, int depthLimit = 128)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (instanceLimit < 1 || instanceLimit > 100000) { throw new ArgumentOutOfRangeException(nameof(instanceLimit)); }
        if (depthLimit < 1 || depthLimit > 128) { throw new ArgumentOutOfRangeException(nameof(depthLimit)); }
        Analysis = analysis;
        _instanceLimit = instanceLimit;
        _depthLimit = depthLimit;
        _types = [.. analysis.Types];
        foreach (var parameter in _types.Select(t => t.Parameter).OfType<GenericParameterIdentity>())
        {
            _ownerIds.TryAdd(parameter.Owner, _ownerIds.Count);
        }
        foreach (var type in _types) { _interned.Add(Key(type.Kind, type.Name, type.Declaration, type.Parameter,
            type.Arguments, type.ParameterModes, type.Effects), type); }
    }

    public TypeAnalysis Analysis { get; }
    public ImmutableArray<ResolvedType> Types { get { lock (_gate) { return _types.ToImmutableArray(); } } }

    public void ValidateOwnership(ResolvedType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (_gate)
        {
            if (type.Id < 0 || type.Id >= _types.Count || !ReferenceEquals(_types[type.Id], type))
            {
                throw new ArgumentException("The type belongs to another specialization workspace.", nameof(type));
            }
        }
    }

    public ResolvedType Substitute(ResolvedType type, IReadOnlyDictionary<ResolvedType, ResolvedType> substitutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(substitutions);
        using var gate = EnterGate(cancellationToken);
        ValidateOwnership(type);
        foreach (var pair in substitutions)
        {
            ValidateOwnership(pair.Key);
            ValidateOwnership(pair.Value);
            if (pair.Key.Kind != ResolvedTypeKind.Parameter)
            {
                throw new ArgumentException("Only generic parameters can be substituted.", nameof(substitutions));
            }
        }
        return Rewrite(type, substitutions, new(), 0, cancellationToken);
    }

    private GateLease EnterGate(CancellationToken cancellationToken)
    {
        while (!Monitor.TryEnter(_gate, millisecondsTimeout: 25))
        { cancellationToken.ThrowIfCancellationRequested(); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(_gate);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    private ResolvedType Rewrite(ResolvedType type, IReadOnlyDictionary<ResolvedType, ResolvedType> substitutions,
        Dictionary<ResolvedType, ResolvedType> memo, int depth, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > _depthLimit) { throw Limit(); }
        // Substitution is simultaneous: a replacement containing another key is not rewritten again.
        if (substitutions.TryGetValue(type, out var replacement)) { return replacement; }
        if (memo.TryGetValue(type, out var cached)) { return cached; }
        var arguments = type.Arguments.Select(t => Rewrite(t, substitutions, memo, depth + 1, cancellation)).ToImmutableArray();
        var effects = type.Effects.Select(t => Rewrite(t, substitutions, memo, depth + 1, cancellation))
            .Distinct().OrderBy(t => t.Id).ToImmutableArray();
        var result = Construct(type.Kind, type.Name, type.Declaration, type.Parameter, arguments, type.ParameterModes, effects);
        memo.Add(type, result);
        return result;
    }

    public ResolvedType GetPrimitive(string name)
    {
        if (name is not ("sbyte" or "short" or "int" or "long" or "byte" or "uint" or "ulong"
            or "half" or "single" or "double" or "quad" or "bool"))
        {
            throw new ArgumentException("Unknown primitive type.", nameof(name));
        }
        lock (_gate) { return Construct(ResolvedTypeKind.Primitive, name, null, null, [], [], []); }
    }

    public ResolvedType GetBottom()
    {
        lock (_gate) { return Construct(ResolvedTypeKind.Bottom, "", null, null, [], [], []); }
    }

    public ResolvedType GetArray(ResolvedType element)
    {
        lock (_gate)
        {
            ValidateOwnership(element);
            return Construct(ResolvedTypeKind.Array, "", null, null, [element], [], []);
        }
    }

    public ResolvedType GetNullable(ResolvedType element)
    {
        lock (_gate)
        {
            ValidateOwnership(element);
            return Construct(ResolvedTypeKind.Nullable, "", null, null, [element], [], []);
        }
    }

    public ResolvedType GetTuple(IEnumerable<ResolvedType> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        lock (_gate)
        {
            var arguments = elements.ToImmutableArray();
            foreach (var type in arguments) { ValidateOwnership(type); }
            return Construct(ResolvedTypeKind.Tuple, "", null, null, arguments, [], []);
        }
    }

    internal ResolvedType Nominal(DeclarationSymbol symbol, ImmutableArray<ResolvedType> arguments)
    {
        lock (_gate)
        {
            foreach (var type in arguments) { ValidateOwnership(type); }
            if (symbol.Kind != SymbolKind.Type || arguments.Length != symbol.Arity
                || !ReferenceEquals(Analysis.Declarations.GetDeclaredSymbol(symbol.Parts[0]), symbol))
            {
                throw new ArgumentException("Invalid nominal definition or generic arity.", nameof(symbol));
            }
            return Construct(ResolvedTypeKind.Nominal, symbol.QualifiedName, symbol, null, arguments, [], []);
        }
    }

    public ResolvedType GetFunction(IEnumerable<ResolvedType> parameters, ResolvedType result,
        IEnumerable<AstPassingMode> modes, IEnumerable<ResolvedType>? effects = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(modes);
        lock (_gate)
        {
            var arguments = parameters.Append(result).ToImmutableArray();
            var passing = modes.ToImmutableArray();
            var thrown = (effects ?? []).Distinct().OrderBy(t => t.Id).ToImmutableArray();
            if (passing.Length != arguments.Length - 1 || passing.Any(m => m == AstPassingMode.Vararg))
            { throw new ArgumentException("Function values require one non-vararg passing mode per parameter.", nameof(modes)); }
            foreach (var type in arguments.Concat(thrown)) { ValidateOwnership(type); }
            return Construct(ResolvedTypeKind.Function, "", null, null, arguments, passing, thrown);
        }
    }

    private ResolvedType Construct(ResolvedTypeKind kind, string name, DeclarationSymbol? declaration,
        GenericParameterIdentity? parameter, ImmutableArray<ResolvedType> arguments,
        ImmutableArray<AstPassingMode> modes, ImmutableArray<ResolvedType> effects)
    {
        if (kind == ResolvedTypeKind.Nullable && arguments.Length == 1 && arguments[0].IsNullable) { return arguments[0]; }
        if (arguments.Any(t => t.IsError) || effects.Any(t => t.IsError)) { return _types[0]; }
        if (kind == ResolvedTypeKind.Array && arguments.Any(t => t.IsRefLike))
        {
            throw new TypeSpecializationException(SpecializationFailure.InvalidType, "A managed array cannot contain ref-like elements.");
        }
        if (kind == ResolvedTypeKind.Nominal && declaration is not null)
        {
            var definition = declaration.Parts[0];
            var parameters = Analysis.GetTypeParameters(definition);
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index].IsRefLike && !AllowsRefLike(definition, parameters[index].Name))
                {
                    throw new TypeSpecializationException(SpecializationFailure.InvalidType,
                        "A ref-like type argument requires allows ref struct: " + parameters[index].Name);
                }
            }
        }
        string key = Key(kind, name, declaration, parameter, arguments, modes, effects);
        if (_interned.TryGetValue(key, out var existing)) { return existing; }
        if (_types.Count - Analysis.Types.Length >= _instanceLimit) { throw Limit(); }
        var result = new ResolvedType(_types.Count, kind, name, declaration, arguments, modes, effects, parameter);
        _types.Add(result);
        _interned.Add(key, result);
        return result;
    }

    private string Key(ResolvedTypeKind kind, string name, DeclarationSymbol? declaration,
        GenericParameterIdentity? parameter, ImmutableArray<ResolvedType> arguments,
        ImmutableArray<AstPassingMode> modes, ImmutableArray<ResolvedType> effects)
    {
        string owner = parameter is null ? "" : _ownerIds[parameter.Owner] + ":" + parameter.Ordinal;
        return (int)kind + ":" + name + ":" + declaration?.Id + ":" + owner
            + "<" + string.Join(',', arguments.Select(t => t.Id)) + ">(" + string.Join(',', modes.Select(m => (int)m))
            + ")!" + string.Join(',', effects.Select(t => t.Id));
    }

    private static TypeSpecializationException Limit() => new(SpecializationFailure.ResourceLimit,
        "The generic specialization depth or instance budget was exceeded.");
}
