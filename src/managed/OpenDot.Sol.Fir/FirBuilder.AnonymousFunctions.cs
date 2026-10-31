// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private FirExpression AnonymousFunctionReference(AstAnonymousFunction function, ResolvedType type)
    {
        if (FunctionValueNestingDepth(function) >= FirVerifier.MaximumNativeFunctionValueNesting)
        {
            Unsupported(function, "Nested function-value lifting exceeds the 128-level FIR budget");
            return new FirConstant("()", Empty, function.Origin);
        }
        if (!CanLowerAnonymousFunction(function, type))
        {
            Unsupported(function, "Anonymous-function capture, parent context, or function ABI is outside the verified native subset");
            return new FirConstant("()", Empty, function.Origin);
        }

        if (!_anonymousFunctionIds.TryGetValue(function, out int target))
        {
            target = _nextFunctionId++;
            _anonymousFunctionIds.Add(function, target);
            _builtFunctions.Add(target, BuildAnonymousFunction(function, target, type));
        }
        return new FirFunctionReference(target, type, function.Origin);
    }

    private bool CanLowerAnonymousFunction(AstAnonymousFunction function, ResolvedType type)
    {
        if (type.Kind != ResolvedTypeKind.Function || type.Arguments.IsEmpty
            || !FirVerifier.Supported(type) || function.Body.Kind == AstBodyKind.Missing
            || function.Body.Node is null || function.Parameters.Length != type.ParameterModes.Length
            || function.Parameters.Any(parameter => parameter.DefaultValue is not null)
            || AnonymousFunctionOwnedNodes(function).OfType<AstReturn>()
                .Any(returned => returned.Label is not null || returned.IsByReference))
        { return false; }

        var sourceBinding = BoundExpression(function);
        if (sourceBinding?.Status != BodyBindingStatus.Bound || sourceBinding.Type != type) { return false; }

        if (_current is not AstFunction currentFunction || currentFunction.TypeParameters.Any()
            || Ancestors(currentFunction).OfType<AstFunction>().Any())
        { return false; }
        var owner = GenericMethodSourcePolicy.DirectOwner(currentFunction, _analysis.Sources);
        if (owner is not null && (owner.TypeParameters.Any()
            || !GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(currentFunction, _analysis.Sources)))
        { return false; }

        var scope = _analysis.Sources.Lexical.Scopes
            .SingleOrDefault(candidate => ReferenceEquals(candidate.Owner, function));
        if (scope is null || !_analysis.Sources.Lexical.GetAnonymousFunctionCaptures(function).IsEmpty
            || !_analysis.Sources.Lexical.GetAnonymousFunctionReceiverCaptures(function).IsEmpty)
        { return false; }

        foreach (var node in AnonymousFunctionOwnedNodes(function))
        {
            if (node is AstFunction)
            { return false; }
            if (node is AstAnonymousFunction nested && !ReferenceEquals(nested, function)) { continue; }
            if (node is not AstAtom atom) { continue; }
            if (atom.Spelling is "this" or "super") { return false; }
            if (atom.SyntaxKind != NodeKind.NameExpression || atom.Spelling == "default") { continue; }

            var binding = _analysis.Sources.Lexical.GetBinding(atom);
            if (binding.Lookup.Locals.Any(local => !IsWithinScope(local.ScopeId, scope.Id))
                || binding.Lookup.Locals.Any(local => local.Kind == LocalSymbolKind.Function)
                || binding.Lookup.Declarations.Scope == LookupScope.Member)
            { return false; }
            if (binding.Lookup.Declarations.Candidates.SelectMany(symbol => symbol.Parts)
                .Any(declaration => _files.TryGetValue(declaration, out var file)
                    && Ancestors(declaration).OfType<AstTypeDeclaration>().Any()))
            { return false; }
        }

        for (int index = 0; index < function.Parameters.Length; index++)
        {
            var parameter = function.Parameters[index];
            var sourceMode = parameter.PassingMode == AstPassingMode.Vararg
                ? AstPassingMode.Value : parameter.PassingMode;
            var sourceType = parameter.PassingMode == AstPassingMode.Vararg
                ? type.Arguments[index].Kind == ResolvedTypeKind.Array
                    ? type.Arguments[index].Arguments[0] : null
                : type.Arguments[index];
            if (sourceMode != type.ParameterModes[index] || sourceType is null) { return false; }
            if (parameter.Type is { } written && Specialize(_analysis.Sources.Types.GetType(written)) != sourceType)
            { return false; }
        }

        foreach (var call in AnonymousFunctionOwnedNodes(function).OfType<AstCall>())
        {
            if (BoundCall(call)?.Target is AstFunction target && target.TypeParameters.Any())
            {
                var binding = BoundCall(call)!;
                if (!HasClosedNativeTypeArguments(target, binding.TypeArguments)) { return false; }
            }
        }
        foreach (var expression in AnonymousFunctionOwnedNodes(function).OfType<AstExpression>())
        {
            if (BoundExpression(expression)?.FunctionValueTarget is { } target && target.TypeParameters.Any())
            {
                var binding = BoundExpression(expression)!;
                if (!HasClosedNativeTypeArguments(target, binding.FunctionValueTypeArguments)) { return false; }
            }
        }
        return true;
    }

    private IEnumerable<AstNode> AnonymousFunctionOwnedNodes(AstAnonymousFunction function)
    {
        foreach (var node in function.Descendants())
        {
            bool nestedCallableBody = false;
            for (AstNode? parent = _files[node].GetParent(node); parent is not null;
                parent = _files[node].GetParent(parent))
            {
                if (ReferenceEquals(parent, function)) { break; }
                if (parent is AstAnonymousFunction or AstLambda or AstFunction)
                { nestedCallableBody = true; break; }
            }
            if (!nestedCallableBody) { yield return node; }
        }
    }

    private FirFunction BuildAnonymousFunction(AstAnonymousFunction function, int id, ResolvedType type)
    {
        var savedSlots = _slots.ToArray();
        var savedLambda = _activeLambda;
        var savedAnonymousFunction = _activeAnonymousFunction;
        var savedParameters = _activeLambdaParameters;
        _slots.Clear();
        _activeLambda = null;
        _activeAnonymousFunction = function;
        try
        {
            var scope = _analysis.Sources.Lexical.Scopes.Single(candidate =>
                ReferenceEquals(candidate.Owner, function));
            var parameters = ImmutableArray.CreateBuilder<FirSlot>(function.Parameters.Length);
            for (int index = 0; index < function.Parameters.Length; index++)
            {
                var parameter = function.Parameters[index];
                var local = scope.Symbols.SingleOrDefault(candidate => candidate.Kind == LocalSymbolKind.Parameter
                    && ReferenceEquals(candidate.Origin.Element, parameter.NameOrigin.Element));
                if (local is null)
                {
                    Unsupported(function, "Anonymous-function parameter has no lexical identity");
                    continue;
                }
                var slot = new FirSlot(index, parameter.Name, type.Arguments[index], local.IsMutable,
                    type.ParameterModes[index], true, parameter.NameOrigin) { IsEscaping = parameter.IsEscaping };
                parameters.Add(slot);
                _slots.Add(local, slot);
            }

            _activeLambdaParameters = parameters.ToImmutable();
            var resultType = type.Arguments[^1];
            var body = AnonymousFunctionBlock(function, resultType);
            return new(id, "<anonymous-function>", _activeLambdaParameters,
                _activeLambdaParameters.Concat(_slots.Values).DistinctBy(slot => slot.Id)
                    .OrderBy(slot => slot.Id).ToImmutableArray(), resultType, body,
                FirRuntimeFunction.None, type.Effects, function.Origin)
            { AnonymousFunctionSource = function };
        }
        finally
        {
            _slots.Clear();
            foreach (var pair in savedSlots) { _slots.Add(pair.Key, pair.Value); }
            _activeLambda = savedLambda;
            _activeAnonymousFunction = savedAnonymousFunction;
            _activeLambdaParameters = savedParameters;
        }
    }

    private FirBlock AnonymousFunctionBlock(AstAnonymousFunction function, ResolvedType resultType)
    {
        if (function.Body.Kind == AstBodyKind.Expression && function.Body.Node is AstExpression expression)
        { return new([new FirReturn(Expression(expression), expression.Origin)], function.Origin); }
        if (function.Body.Node is not AstBlock block)
        { return new([], function.Origin); }

        var statements = ImmutableArray.CreateBuilder<FirStatement>();
        for (int index = 0; index < block.Statements.Length; index++)
        {
            var source = block.Statements[index];
            bool tail = index == block.Statements.Length - 1 && resultType != Empty
                && source is AstExpressionStatement { Expression: not null };
            if (tail && source is AstExpressionStatement { Expression: { } value })
            { statements.Add(new FirReturn(Expression(value), source.Origin)); }
            else if (Statement(source) is { } statement) { statements.Add(statement); }
        }
        if (resultType == Empty && block.Statements.LastOrDefault() is not AstReturn)
        { statements.Add(new FirReturn(null, function.Origin)); }
        return new(statements.ToImmutable(), function.Origin);
    }
}
