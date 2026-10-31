// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public sealed partial class BodyAnalysis
{
    private readonly record struct LexicalBodyKey(AstFunction Function, string MethodIds, string Context);
    private readonly Dictionary<LexicalBodyKey, SpecializedBodyAnalysis> _lexicalBodies = [];
    private readonly HashSet<LexicalBodyKey> _activeLexicalReturns = [];

    private ResolvedType? ResolveClosedLexicalReturn(AstFunction function,
        ImmutableArray<ResolvedType> arguments, LexicalBodyContext context, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var key = new LexicalBodyKey(function, TypeIds(arguments), context.Identity);
        while (!Monitor.TryEnter(_genericBodyGate, 25)) { cancellation.ThrowIfCancellationRequested(); }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_lexicalBodies.TryGetValue(key, out var cached)) { return cached.Summary.ReturnType; }
            if (_memberReturnInferenceSteps >= 1024 || _activeLexicalReturns.Count + _activeMemberReturns.Count >= 128
                || !_activeLexicalReturns.Add(key)) { return null; }
            _memberReturnInferenceSteps++;
            try
            {
                var body = GetLexicalBody(function, arguments, context, cancellation);
                return body.Summary.Status == BodyBindingStatus.Bound && body.Diagnostics.IsEmpty
                    && body.UnverifiedNodes.IsEmpty ? body.Summary.ReturnType : null;
            }
            catch (TypeSpecializationException) { return null; }
            finally { _activeLexicalReturns.Remove(key); }
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }

    internal SpecializedBodyAnalysis GetLexicalBody(AstFunction function,
        ImmutableArray<ResolvedType> arguments, LexicalBodyContext context, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(context);
        cancellation.ThrowIfCancellationRequested();
        if (!_nodes.Contains(function) || !ReferenceEquals(context.Sources, Sources)
            || !ReferenceEquals(context.Function, function))
        { throw new ArgumentException("A lexical body context must belong to its source function.", nameof(context)); }
        if (arguments.IsDefault || Sources.Types.GetTypeParameters(function).Length != arguments.Length)
        { throw new ArgumentException("A lexical body requires every ordered local method argument.", nameof(arguments)); }
        ValidateLexicalContext(function, context, cancellation);
        foreach (var argument in arguments) { ValidateClosedBodyArgument(argument, nameof(arguments)); }
        foreach (var pair in context.EnclosingArguments)
        {
            Types.ValidateOwnership(pair.Parameter);
            ValidateClosedBodyArgument(pair.Argument, nameof(context));
        }
        foreach (var local in context.Locals)
        {
            if (!_localSymbols.Contains(local.Local))
            { throw new ArgumentException("A lexical context contains a foreign local.", nameof(context)); }
            if (local.Type is not null) { ValidateClosedBodyArgument(local.Type, nameof(context)); }
            if (local.Narrowed is not null) { ValidateClosedBodyArgument(local.Narrowed, nameof(context)); }
        }
        var key = new LexicalBodyKey(function, TypeIds(arguments), context.Identity);
        while (!Monitor.TryEnter(_genericBodyGate, millisecondsTimeout: 25))
        { cancellation.ThrowIfCancellationRequested(); }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (_lexicalBodies.TryGetValue(key, out var cached)) { return cached; }
            var builder = new BodyBindingBuilder(Sources, Types, cancellation)
            { ClosedMemberReturn = ResolveClosedMemberReturn, ClosedOwnerReturnTemplate = ResolveClosedOwnerReturnTemplate,
                ClosedLexicalReturn = ResolveClosedLexicalReturn, InterfaceSourceBody = GetBody,
                ConstructionSourceStatus = GetConstructionSourceStatus };
            var result = builder.BuildLexicalBody(function, arguments, context);
            cancellation.ThrowIfCancellationRequested();
            if (result.Summary.Status == BodyBindingStatus.Bound
                && result.Diagnostics.IsEmpty && result.UnverifiedNodes.IsEmpty)
            { _lexicalBodies.Add(key, result); }
            return result;
        }
        finally { Monitor.Exit(_genericBodyGate); }
    }
}
