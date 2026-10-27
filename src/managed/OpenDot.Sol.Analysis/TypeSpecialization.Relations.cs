// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

public enum IntrinsicConversion { Identity, Reference, Boxing, None }
public enum ConstraintStatus { Satisfied, Unsatisfied, Indeterminate }
public sealed record ConstraintCheck(ConstraintStatus Status, string? ParameterName, string? Reason);

public sealed partial class TypeSpecialization
{
    /// <summary>Substitutes written bases and supplies the implicit std.core.Object base for classes.</summary>
    public ImmutableArray<ResolvedType> GetDirectBases(ResolvedType type, CancellationToken cancellationToken = default)
    {
        using var gate = EnterGate(cancellationToken);
        ValidateOwnership(type);
        if (type.Declaration is not { } declaration || type.Kind != ResolvedTypeKind.Nominal) { return []; }
        var parameters = Analysis.GetTypeParameters(declaration.Parts[0]);
        var substitutions = parameters.Select((p, index) => (p, type.Arguments[index])).ToDictionary(p => p.p, p => p.Item2);
        var bases = declaration.Parts.OfType<AstTypeDeclaration>().SelectMany(p => p.Bases)
            .SelectMany(b => b.Children.OfType<AstType>()).Select(Analysis.GetType)
            .Select(b => Rewrite(b, substitutions, new(), 0, cancellationToken)).Distinct().ToList();
        bool referenceClass = declaration.Parts[0].SyntaxKind is NodeKind.ClassDeclaration or NodeKind.ObjectDeclaration;
        if (referenceClass && !(declaration.ModuleName == "std.core" && declaration.Name == "Object")
            && !bases.Any(b => b.Declaration?.Parts[0].SyntaxKind == NodeKind.ClassDeclaration)
            && Analysis.Declarations.Modules.TryGetValue("std.core", out var core))
        {
            var roots = core.Declarations.Where(s => s.Kind == SymbolKind.Type && s.Name == "Object" && s.Arity == 0).Take(2).ToArray();
            if (roots.Length == 1) { bases.Add(Nominal(roots[0], [])); }
        }
        return bases.ToImmutableArray();
    }

    public IntrinsicConversion ClassifyConversion(ResolvedType source, ResolvedType target,
        CancellationToken cancellationToken = default)
    {
        using var gate = EnterGate(cancellationToken);
        ValidateOwnership(source);
        ValidateOwnership(target);
        return Conversion(source, target, new(), 0, cancellationToken);
    }

    private IntrinsicConversion Conversion(ResolvedType source, ResolvedType target,
        HashSet<(int, int)> visited, int depth, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > _depthLimit) { throw Limit(); }
        if (source.IsError || target.IsError) { return IntrinsicConversion.None; }
        if (ReferenceEquals(source, target)) { return IntrinsicConversion.Identity; }
        if (source.Kind == ResolvedTypeKind.Bottom) { return IntrinsicConversion.Reference; }
        if (!visited.Add((source.Id, target.Id))) { return IntrinsicConversion.None; }
        if (source.Kind == ResolvedTypeKind.Function && target.Kind == ResolvedTypeKind.Function
            && source.ParameterModes.SequenceEqual(target.ParameterModes) && source.Arguments.Length == target.Arguments.Length
            && source.Arguments.Take(source.Arguments.Length - 1).SequenceEqual(target.Arguments.Take(target.Arguments.Length - 1))
            && Conversion(source.Arguments[^1], target.Arguments[^1], new(), depth + 1, cancellation) != IntrinsicConversion.None
            && source.Effects.All(effect => target.Effects.Any(budget => Conversion(effect, budget, new(), depth + 1, cancellation) != IntrinsicConversion.None)))
        { return IntrinsicConversion.Reference; }
        if (target.IsNullable)
        {
            var value = source.IsNullable ? source.Arguments[0] : source;
            var conversion = Conversion(value, target.Arguments[0], visited, depth + 1, cancellation);
            return conversion == IntrinsicConversion.Identity ? IntrinsicConversion.Reference : conversion;
        }
        if (source.IsNullable) { return IntrinsicConversion.None; }
        if (source.Kind == ResolvedTypeKind.Array && target.Declaration is { ModuleName: "std.core", Name: "Object", Arity: 0 })
        {
            return IntrinsicConversion.Reference;
        }
        if (source.Kind == ResolvedTypeKind.Primitive && target.Declaration is { ModuleName: "std.core", Name: "Object", Arity: 0 })
        {
            return IntrinsicConversion.Boxing;
        }
        if (source.Kind == ResolvedTypeKind.Parameter && source.Parameter is { } parameter)
        {
            foreach (var bound in Bounds(parameter.Owner, parameter.Ordinal))
            {
                if (Conversion(Analysis.GetType(bound), target, visited, depth + 1, cancellation) != IntrinsicConversion.None)
                {
                    return IntrinsicConversion.Reference;
                }
            }
        }
        if (source.Kind != ResolvedTypeKind.Nominal || target.Kind != ResolvedTypeKind.Nominal) { return IntrinsicConversion.None; }
        bool valueType = source.Declaration?.Parts[0].SyntaxKind is NodeKind.StructDeclaration or NodeKind.RefStructDeclaration
            or NodeKind.EnumDeclaration;
        if (valueType && source.IsRefLike) { return IntrinsicConversion.None; }
        foreach (var parent in GetDirectBases(source, cancellation))
        {
            if (Conversion(parent, target, visited, depth + 1, cancellation) != IntrinsicConversion.None)
            {
                return valueType ? IntrinsicConversion.Boxing : IntrinsicConversion.Reference;
            }
        }
        if (valueType && target.Declaration is { ModuleName: "std.core", Name: "Object", Arity: 0 })
        {
            return IntrinsicConversion.Boxing;
        }
        return IntrinsicConversion.None;
    }

    public ConstraintCheck CheckConstraints(AstDeclaration definition, IReadOnlyList<ResolvedType> arguments,
        CancellationToken cancellationToken = default)
        => CheckConstraintsWithSubstitution(definition, arguments, new Dictionary<ResolvedType, ResolvedType>(), cancellationToken);

    /// <summary>
    /// Checks a declaration's generic bounds after applying substitutions owned by its enclosing
    /// constructed type. The declaration's own parameters are then bound to <paramref name="arguments"/>.
    /// </summary>
    public ConstraintCheck CheckConstraintsWithSubstitution(AstDeclaration definition, IReadOnlyList<ResolvedType> arguments,
        IReadOnlyDictionary<ResolvedType, ResolvedType> enclosingSubstitution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(enclosingSubstitution);
        using var gate = EnterGate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = Analysis.GetTypeParameters(definition);
        if (arguments.Count != parameters.Length) { throw new ArgumentException("Generic arity mismatch.", nameof(arguments)); }
        foreach (var argument in arguments) { ValidateOwnership(argument); }
        var substitutions = new Dictionary<ResolvedType, ResolvedType>();
        foreach (var pair in enclosingSubstitution)
        {
            ValidateOwnership(pair.Key);
            ValidateOwnership(pair.Value);
            if (pair.Key.Kind != ResolvedTypeKind.Parameter)
            {
                throw new ArgumentException("Enclosing substitution keys must be generic parameters.", nameof(enclosingSubstitution));
            }
            substitutions[pair.Key] = pair.Value;
        }
        for (int index = 0; index < parameters.Length; index++)
        { substitutions[parameters[index]] = arguments[index]; }
        bool indeterminate = false;
        for (int index = 0; index < parameters.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var argument = arguments[index];
            string name = parameters[index].Name;
            if (argument.IsError) { indeterminate = true; continue; }
            if (argument.IsRefLike && !AllowsRefLike(definition, name))
            {
                return new(ConstraintStatus.Unsatisfied, name, "The argument requires allows ref struct.");
            }
            foreach (var syntax in Bounds(definition, index))
            {
                var bound = Rewrite(Analysis.GetType(syntax), substitutions, new(), 0, cancellationToken);
                if (bound.IsError || bound.Kind != ResolvedTypeKind.Nominal) { indeterminate = true; continue; }
                bool objectConstraint = bound.Declaration is { ModuleName: "std.core", Name: "Object", Arity: 0 };
                var conversion = Conversion(argument, bound, new(), 0, cancellationToken);
                // A ref-like value can satisfy an implemented interface without boxing into interface storage.
                bool constrainedRef = argument.IsRefLike && !objectConstraint
                    && SatisfiesNominal(argument, bound, new(), 0, cancellationToken);
                if ((!constrainedRef && conversion == IntrinsicConversion.None)
                    || objectConstraint && conversion == IntrinsicConversion.Boxing)
                {
                    return new(ConstraintStatus.Unsatisfied, name, "The argument does not satisfy " + bound.Name + ".");
                }
            }
        }
        return new(indeterminate ? ConstraintStatus.Indeterminate : ConstraintStatus.Satisfied, null, null);
    }

    internal bool HasUnsatisfiedClosedConstraints(AstDeclaration definition,
        IReadOnlyList<ResolvedType> arguments,
        IReadOnlyDictionary<ResolvedType, ResolvedType> enclosingSubstitution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(enclosingSubstitution);
        using var gate = EnterGate(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = Analysis.GetTypeParameters(definition);
        if (arguments.Count != parameters.Length)
        { throw new ArgumentException("Generic arity mismatch.", nameof(arguments)); }
        var substitutions = new Dictionary<ResolvedType, ResolvedType>();
        foreach (var pair in enclosingSubstitution)
        {
            ValidateOwnership(pair.Key);
            ValidateOwnership(pair.Value);
            if (pair.Key.Kind != ResolvedTypeKind.Parameter)
            { throw new ArgumentException("Enclosing substitution keys must be generic parameters.", nameof(enclosingSubstitution)); }
            substitutions[pair.Key] = pair.Value;
        }
        for (int index = 0; index < parameters.Length; index++)
        {
            ValidateOwnership(arguments[index]);
            substitutions[parameters[index]] = arguments[index];
        }

        for (int index = 0; index < parameters.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var argument = arguments[index];
            if (ContainsAnyParameter(argument, cancellationToken)) { continue; }
            string name = parameters[index].Name;
            if (argument.IsRefLike && !AllowsRefLike(definition, name)) { return true; }
            foreach (var syntax in Bounds(definition, index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bound = Rewrite(Analysis.GetType(syntax), substitutions, new(), 0, cancellationToken);
                if (bound.IsError || bound.Kind != ResolvedTypeKind.Nominal
                    || ContainsAnyParameter(bound, cancellationToken)) { continue; }
                bool objectConstraint = bound.Declaration is { ModuleName: "std.core", Name: "Object", Arity: 0 };
                var conversion = Conversion(argument, bound, new(), 0, cancellationToken);
                bool constrainedRef = argument.IsRefLike && !objectConstraint
                    && SatisfiesNominal(argument, bound, new(), 0, cancellationToken);
                if ((!constrainedRef && conversion == IntrinsicConversion.None)
                    || objectConstraint && conversion == IntrinsicConversion.Boxing) { return true; }
            }
        }
        return false;
    }

    private static bool ContainsAnyParameter(ResolvedType root, CancellationToken cancellationToken)
    {
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<int>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current.Id)) { continue; }
            if (current.Kind == ResolvedTypeKind.Parameter) { return true; }
            foreach (var child in current.Arguments.Concat(current.Effects)) { pending.Push(child); }
        }
        return false;
    }

    /// <summary>Checks nominal applications contained in a type; does not instantiate generic bodies.</summary>
    public ConstraintCheck CheckTypeConstraints(ResolvedType type, CancellationToken cancellationToken = default)
    {
        using var gate = EnterGate(cancellationToken);
        ValidateOwnership(type);
        bool indeterminate = false;
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<int>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current.Id)) { continue; }
            if (current.IsError) { indeterminate = true; continue; }
            if (current.Kind == ResolvedTypeKind.Nominal && current.Declaration is { } definition)
            {
                var check = CheckConstraints(definition.Parts[0], current.Arguments, cancellationToken);
                if (check.Status == ConstraintStatus.Unsatisfied) { return check; }
                indeterminate |= check.Status == ConstraintStatus.Indeterminate;
            }
            foreach (var child in current.Arguments.Concat(current.Effects)) { pending.Push(child); }
        }
        return new(indeterminate ? ConstraintStatus.Indeterminate : ConstraintStatus.Satisfied, null, null);
    }

    private bool SatisfiesNominal(ResolvedType source, ResolvedType bound, HashSet<int> visited, int depth, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth > _depthLimit) { throw Limit(); }
        if (ReferenceEquals(source, bound)) { return true; }
        return visited.Add(source.Id) && GetDirectBases(source, cancellation)
            .Any(b => SatisfiesNominal(b, bound, visited, depth + 1, cancellation));
    }

    private static IEnumerable<AstType> Bounds(AstDeclaration definition, int ordinal)
    {
        var parameters = definition switch
        {
            AstTypeDeclaration type => type.TypeParameters,
            AstCallable callable => callable.TypeParameters,
            _ => [],
        };
        var parameter = parameters.ElementAt(ordinal);
        if (parameter.Constraint is not null) { yield return parameter.Constraint; }
        foreach (var entry in definition.Children.Where(n => n.SyntaxKind == NodeKind.WhereClause).SelectMany(n => n.Children)
            .Where(n => n.Children.Any(c => c.SyntaxKind == NodeKind.Name && c.Origin.Text == parameter.Name)))
        {
            foreach (var type in entry.Children.OfType<AstType>()) { yield return type; }
        }
    }

    private static bool AllowsRefLike(AstDeclaration definition, string name) => definition.Children
        .Where(n => n.SyntaxKind == NodeKind.WhereClause).SelectMany(n => n.Children)
        .Any(n => n.Children.Any(c => c.SyntaxKind == NodeKind.Name && c.Origin.Text == name)
            && n.Children.Any(c => c.SyntaxKind == NodeKind.AntiConstraint));
}
