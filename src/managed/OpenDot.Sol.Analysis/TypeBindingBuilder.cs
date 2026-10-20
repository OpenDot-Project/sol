// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

internal sealed partial class TypeBindingBuilder
{
    private static readonly HashSet<string> PrimitiveNames = new(StringComparer.Ordinal)
    {
        "sbyte", "short", "int", "long", "byte", "uint", "ulong", "half", "single", "double", "quad", "bool",
    };

    private readonly CancellationToken _cancellation;
    private readonly Dictionary<string, ResolvedType> _interned = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeShape, ResolvedType> _resolvedShapes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AstNode, AstSnapshot> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AstNode, int> _ownerIds = new(ReferenceEqualityComparer.Instance);
    private readonly ResolvedType _error;

    internal TypeBindingBuilder(AnalysisSession session, CancellationToken cancellation)
    {
        Session = session;
        _cancellation = cancellation;
        _error = Intern(ResolvedTypeKind.Error, "<error>", "error", null, [], [], []);
        foreach (var file in session.Files)
        {
            foreach (var node in file.Nodes)
            {
                _owners.Add(node, file);
                _ownerIds.Add(node, _ownerIds.Count);
            }
        }
    }

    internal AnalysisSession Session { get; }
    internal Dictionary<AstType, ResolvedType> References { get; } = new(ReferenceEqualityComparer.Instance);
    internal List<ResolvedType> Types { get; } = [];
    internal List<Diagnostic> Diagnostics { get; } = [];
    internal Dictionary<AstDeclaration, ImmutableArray<ResolvedType>> Parameters { get; } = new(ReferenceEqualityComparer.Instance);

    internal void Build()
    {
        foreach (var owner in Session.Files.SelectMany(f => f.Nodes).OfType<AstDeclaration>())
        {
            var parameters = owner switch
            {
                AstTypeDeclaration type => type.TypeParameters,
                AstCallable callable => callable.TypeParameters,
                _ => [],
            };
            var symbol = Session.GetDeclaredSymbol(owner);
            var canonical = symbol?.Parts[0] ?? owner;
            string identity = symbol is null ? "node:" + _ownerIds[owner] : "symbol:" + symbol.Id;
            Parameters.Add(owner, parameters.Select((p, index) => Intern(ResolvedTypeKind.Parameter,
                p.Name ?? "", "parameter:" + identity + ":" + index, null, [], [], [],
                new(canonical, index))).ToImmutableArray());
        }
        foreach (var file in Session.Files)
        {
            foreach (var type in file.Nodes.OfType<AstType>().Reverse())
            {
                _cancellation.ThrowIfCancellationRequested();
                References.Add(type, Resolve(type.Shape, type, 0));
            }
            foreach (var declaration in file.Nodes.OfType<AstDeclaration>())
            {
                if (declaration.Name is not null && PrimitiveNames.Contains(declaration.Name))
                {
                    Report("SOL2116", "A primitive type name cannot be shadowed: " + declaration.Name, declaration);
                }
                if (declaration is AstCallable callable)
                {
                    foreach (var parameter in callable.Parameters.Where(p => PrimitiveNames.Contains(p.Name)))
                    {
                        Report("SOL2116", "A primitive type name cannot be shadowed: " + parameter.Name, callable);
                    }
                }
            }
            foreach (var parameter in file.Nodes.OfType<AstTypeParameter>().Where(p => p.Name is not null && PrimitiveNames.Contains(p.Name)))
            {
                Report("SOL2116", "A primitive type name cannot be shadowed: " + parameter.Name, parameter);
            }
        }
        ValidateBases();
        ValidateResolvedSignatures();
    }

    private ResolvedType Resolve(TypeShape shape, AstType origin, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_resolvedShapes.TryGetValue(shape, out var resolved)) { return resolved; }
        var result = ResolveCore(shape, origin, depth);
        _resolvedShapes.Add(shape, result);
        return result;
    }

    private ResolvedType ResolveCore(TypeShape shape, AstType origin, int depth)
    {
        if (depth > 256)
        {
            Report("SOL2110", "The type resolution nesting budget was exceeded.", origin);
            return _error;
        }
        var arguments = shape.Arguments.Select(argument => Resolve(argument, origin, depth + 1)).ToImmutableArray();
        var effects = shape.Effects.Select(effect => Resolve(effect, origin, depth + 1))
            .Distinct().OrderBy(t => t.Id).ToImmutableArray();
        if (arguments.Any(t => t.IsError) || effects.Any(t => t.IsError)) { return _error; }
        if (shape.Kind == AstTypeKind.Named)
        {
            return ResolveName(shape.Name, arguments, origin);
        }
        if (shape.Kind == AstTypeKind.Error) { return _error; }
        if (shape.Kind == AstTypeKind.Array && arguments.Any(t => t.IsRefLike))
        {
            Report("SOL2115", "A managed array cannot contain ref-like elements.", origin);
            return _error;
        }
        if (shape.Kind == AstTypeKind.Nullable && arguments.Length == 1 && arguments[0].IsNullable)
        {
            return arguments[0];
        }
        string name = shape.Name;
        if (shape.Kind == AstTypeKind.FixedArray)
        {
            if (!TryArrayLength(name, out var length) || length < 0 || length > int.MaxValue)
            {
                Report("SOL2111", "A fixed array length must fit a nonnegative int.", origin);
                return _error;
            }
            name = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var kind = shape.Kind switch
        {
            AstTypeKind.Array => ResolvedTypeKind.Array,
            AstTypeKind.FixedArray => ResolvedTypeKind.FixedArray,
            AstTypeKind.Nullable => ResolvedTypeKind.Nullable,
            AstTypeKind.Tuple => ResolvedTypeKind.Tuple,
            AstTypeKind.Function => ResolvedTypeKind.Function,
            AstTypeKind.Bottom => ResolvedTypeKind.Bottom,
            _ => ResolvedTypeKind.Error,
        };
        return Intern(kind, name, kind + ":" + name, null, arguments, shape.ParameterModes, effects);
    }

    private ResolvedType ResolveName(string name, ImmutableArray<ResolvedType> arguments, AstType origin)
    {
        if (name.Split('.').Any(string.IsNullOrWhiteSpace))
        {
            Report("SOL2112", "The type name contains a missing component.", origin);
            return _error;
        }
        if (PrimitiveNames.Contains(name))
        {
            if (!arguments.IsEmpty)
            {
                Report("SOL2112", "A primitive type cannot have generic arguments.", origin);
                return _error;
            }
            return Intern(ResolvedTypeKind.Primitive, name, "primitive:" + name, null, [], [], []);
        }
        var parameter = FindParameter(name, origin);
        if (parameter is not null)
        {
            if (!arguments.IsEmpty)
            {
                Report("SOL2112", "A type parameter cannot have generic arguments.", origin);
                return _error;
            }
            return parameter;
        }
        var candidates = FindTypes(name, origin).Distinct().Where(symbol => symbol.Arity == arguments.Length).ToArray();
        if (candidates.Length != 1)
        {
            Report(candidates.Length > 1 ? "SOL2002" : "SOL2112",
                candidates.Length > 1 ? "Ambiguous type reference: " + name
                    : "Unknown type or generic arity mismatch: " + name, origin);
            return _error;
        }
        var declaration = candidates[0];
        var definition = (AstTypeDeclaration)declaration.Parts[0];
        var parameters = definition.TypeParameters.ToArray();
        for (int index = 0; index < arguments.Length; index++)
        {
            string? parameterName = parameters[index].Name;
            bool allowed = definition.Children.Where(n => n.SyntaxKind == NodeKind.WhereClause).SelectMany(n => n.Children)
                .Any(n => n.Children.Any(child => child.SyntaxKind == NodeKind.Name && child.Origin.Text == parameterName)
                    && n.Children.Any(child => child.SyntaxKind == NodeKind.AntiConstraint));
            if (arguments[index].IsRefLike && !allowed)
            {
                Report("SOL2115", "A ref-like type argument requires an explicit allows ref struct constraint.", origin);
                return _error;
            }
        }
        return Intern(ResolvedTypeKind.Nominal, declaration.QualifiedName, "nominal:" + declaration.Id,
            declaration, arguments, [], []);
    }

    private ResolvedType Intern(ResolvedTypeKind kind, string name, string key, DeclarationSymbol? symbol,
        ImmutableArray<ResolvedType> arguments, ImmutableArray<AstPassingMode> modes, ImmutableArray<ResolvedType> effects,
        GenericParameterIdentity? parameter = null)
    {
        key += "<" + string.Join(',', arguments.Select(t => t.Id)) + ">(" + string.Join(',', modes.Select(m => (int)m))
            + ")!" + string.Join(',', effects.Select(t => t.Id));
        if (_interned.TryGetValue(key, out var type)) { return type; }
        type = new(Types.Count, kind, name, symbol, arguments, modes, effects, parameter);
        Types.Add(type);
        _interned.Add(key, type);
        return type;
    }

    private void Report(string code, string message, AstNode origin)
    {
        if (Diagnostics.Count < Session.Options.DiagnosticLimit)
        {
            Diagnostics.Add(new(code, message, origin.Origin.Source, origin.Origin.Span));
        }
    }

    private static bool TryArrayLength(string spelling, out BigInteger length)
    {
        int radix = spelling.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16
            : spelling.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2
            : spelling.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : 10;
        length = 0;
        var digits = radix == 10 ? spelling.AsSpan() : spelling.AsSpan(2);
        if (digits.IsEmpty) { return false; }
        foreach (char value in digits)
        {
            int digit = value is >= '0' and <= '9' ? value - '0'
                : value is >= 'a' and <= 'f' ? value - 'a' + 10
                : value is >= 'A' and <= 'F' ? value - 'A' + 10 : -1;
            if (digit < 0 || digit >= radix) { return false; }
            length = length * radix + digit;
            if (length > int.MaxValue) { return false; }
        }
        return true;
    }
}
