// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private FirExpression FunctionReference(AstExpression source, AstFunction target, ResolvedType type)
    {
        var sourceBinding = BoundExpression(source);
        var typeArguments = sourceBinding?.FunctionValueTypeArguments ?? [];
        if (target.TypeParameters.Any()
            && GenericMethodSourcePolicy.DirectOwner(target, _analysis.Sources) is not null
            && (!ReferenceEquals(sourceBinding?.FunctionValueTarget, target)
                || !GenericObjectMethodFunctionValuePolicy.IsSupportedSource(source, target, _analysis.Sources)))
        {
            Unsupported(source, "Generic object method function value receiver is not a direct, verified singleton reference");
            return new FirConstant("()", Empty, source.Origin);
        }
        if (!TryFunctionReferenceTarget(target, type, typeArguments))
        {
            Unsupported(source, "Function value does not have a closed, source-matched native function signature");
            return new FirConstant("()", Empty, source.Origin);
        }
        int function = SpecializationTarget(target, typeArguments, source);
        if (function < 0) { return new FirConstant("()", Empty, source.Origin); }
        return new FirFunctionReference(function, type, source.Origin);
    }

    private bool TryFunctionReferenceTarget(AstFunction target, ResolvedType expected,
        ImmutableArray<ResolvedType> typeArguments)
    {
        if (expected.Kind != ResolvedTypeKind.Function
            || target.Body.Kind == AstBodyKind.Missing || target.ReturnsByReference
            || _catalog.Intrinsic(target) is not null)
        { return false; }
        var owner = GenericMethodSourcePolicy.DirectOwner(target, _analysis.Sources);
        if (owner is not null && !GenericMethodSourcePolicy.IsSupportedStatelessObjectMethod(target, _analysis.Sources))
        { return false; }

        var typeParameters = _analysis.Sources.Types.GetTypeParameters(target);
        if (typeArguments.IsDefault || typeArguments.Length != typeParameters.Length) { return false; }
        var substitution = typeParameters.Zip(typeArguments).ToDictionary(pair => pair.First, pair => pair.Second);

        if (target.Parameters.Any(parameter => parameter.Type is null)
            || target.ReturnType is null && _analysis.GetBody(target)?.ReturnType is null)
        { return false; }

        var parameters = target.Parameters.Select(parameter =>
        {
            var type = _analysis.Sources.Types.GetType(parameter.Type!);
            return parameter.PassingMode == AstPassingMode.Vararg ? _analysis.Types.GetArray(type) : type;
        }).Select(type => _analysis.Types.Substitute(type, substitution, _cancellation)).ToArray();
        var modes = target.Parameters.Select(parameter => parameter.PassingMode == AstPassingMode.Vararg
            ? AstPassingMode.Value : parameter.PassingMode).ToArray();
        var result = target.ReturnType is { } returnType
            ? _analysis.Types.Substitute(_analysis.Sources.Types.GetType(returnType), substitution, _cancellation)
            : _analysis.Types.Substitute(_analysis.GetBody(target)!.ReturnType!, substitution, _cancellation);
        var effects = target.ThrownTypes.Select(effect =>
            _analysis.Types.Substitute(_analysis.Sources.Types.GetType(effect), substitution, _cancellation));
        var signature = _analysis.Types.GetFunction(parameters, result, modes, effects);
        if (signature != expected) { return false; }

        return parameters.All(FirVerifier.Supported) && FirVerifier.Supported(result);
    }
}
