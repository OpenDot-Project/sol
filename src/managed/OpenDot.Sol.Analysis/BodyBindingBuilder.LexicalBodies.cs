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
    private LexicalBodyContext? _lexicalEntry;
    internal Func<AstFunction, ImmutableArray<ResolvedType>, LexicalBodyContext,
        System.Threading.CancellationToken, ResolvedType?>? ClosedLexicalReturn { get; set; }

    private ResolvedType? InferLexicalReturn(AstFunction function,
        ImmutableArray<ResolvedType> arguments, State state, AstCall call)
    {
        var context = CaptureLexicalContext(function, state);
        if (context is null || ClosedLexicalReturn is null) { return null; }
        if (context.UnavailableReason is not null) { Defer(call); return null; }
        if (arguments.Any(ContainsTypeParameter)
            || context.EnclosingArguments.Any(pair => ContainsTypeParameter(pair.Argument))
            || context.Locals.Any(local => local.Type is { } type && ContainsTypeParameter(type)))
        {
            return CheckBody(function).ReturnType;
        }
        var result = ClosedLexicalReturn(function, arguments, context, _cancellation);
        if (result is null) { Defer(call); }
        return result;
    }

    private LexicalBodyContext? CaptureLexicalContext(AstCallable target, State state)
    {
        if (target is not AstFunction function) { return null; }
        var ancestors = Ancestors(function).ToArray();
        if (!ancestors.Any(node => node is AstCallable or AstLambda)) { return null; }
        var arguments = ImmutableArray.CreateBuilder<LexicalTypeArgument>();
        foreach (var ancestor in ancestors.Reverse().OfType<AstDeclaration>())
        {
            _cancellation.ThrowIfCancellationRequested();
            foreach (var parameter in Sources.Types.GetTypeParameters(ancestor))
            {
                arguments.Add(new(parameter, _bodySubstitution.GetValueOrDefault(parameter, parameter)));
                if (arguments.Count > LexicalBodyContext.MaximumEnclosingArguments)
                {
                    return new(Sources, function, [], [])
                    { UnavailableReason = "Local callable enclosing argument budget exceeded." };
                }
            }
        }
        var owners = ancestors.ToHashSet();
        var locals = ImmutableArray.CreateBuilder<LexicalLocalFact>();
        foreach (var local in Sources.Lexical.Scopes.Where(scope => owners.Contains(scope.Owner))
            .SelectMany(scope => scope.Symbols).Where(local => local.Kind != LocalSymbolKind.Function)
            .OrderBy(local => local.Id))
        {
            _cancellation.ThrowIfCancellationRequested();
            // A mutable capture is a shared cell. A call can invalidate its smart cast even
            // when its declared storage type and definite-assignment fact remain available.
            locals.Add(new(local, LocalType(local), state.Assigned.Contains(local),
                local.IsMutable ? null : state.Narrowed.GetValueOrDefault(local)));
            if (locals.Count > LexicalBodyContext.MaximumLocals)
            {
                return new(Sources, function, [], [])
                { UnavailableReason = "Local callable lexical local budget exceeded." };
            }
        }
        return new(Sources, function, arguments.ToImmutable(), locals.ToImmutable());
    }

    internal SpecializedBodyAnalysis BuildLexicalBody(AstFunction function,
        ImmutableArray<ResolvedType> arguments, LexicalBodyContext context)
    {
        _cancellation.ThrowIfCancellationRequested();
        _lexicalEntry = context;
        _bodySubstitution = context.EnclosingArguments.ToDictionary(pair => pair.Parameter, pair => pair.Argument);
        var parameters = Sources.Types.GetTypeParameters(function);
        for (int index = 0; index < parameters.Length; index++)
        { _bodySubstitution.Add(parameters[index], arguments[index]); }
        foreach (var local in context.Locals)
        { if (local.Type is not null) { _localTypes.Add(local.Local, local.Type); } }
        var enclosing = context.EnclosingArguments.ToDictionary(pair => pair.Parameter, pair => pair.Argument);
        var constraints = Types.CheckConstraintsWithSubstitution(function, arguments, enclosing, _cancellation);
        if (constraints.Status != ConstraintStatus.Satisfied)
        {
            if (constraints.Status == ConstraintStatus.Unsatisfied)
            { Report("SOL2221", "Local generic constraints are not satisfied for the closed lexical instance.", function); }
            else { Defer(function); }
            var rejected = new BodySummary(function, null, true,
                constraints.Status == ConstraintStatus.Unsatisfied ? BodyBindingStatus.Invalid : BodyBindingStatus.Deferred, []);
            return new(this, function, arguments, rejected);
        }
        var summary = CheckBody(function);
        CompleteInterfaceImplementations();
        summary = Bodies.GetValueOrDefault(function, summary);
        ValidateLexicalCaptureLifetime(function);
        if (Diagnostics.Count != 0)
        {
            summary = summary with { Status = BodyBindingStatus.Invalid };
            Bodies[function] = summary;
        }
        _cancellation.ThrowIfCancellationRequested();
        return new(this, function, arguments, summary);
    }

    private void InitializeLexicalEntry(AstCallable callable, State state)
    {
        if (_lexicalEntry is not { } entry || !ReferenceEquals(entry.Function, callable)) { return; }
        foreach (var local in entry.Locals)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (local.Assigned) { state.Assigned.Add(local.Local); }
            else { state.Assigned.Remove(local.Local); }
            if (local.Narrowed is not null) { state.Narrowed.Add(local.Local, local.Narrowed); }
        }
    }
}
