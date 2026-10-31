// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private FirExpression IndirectCall(AstCall call, AstExpression target, ResolvedType signature, ResolvedType result)
    {
        if (signature.Kind != ResolvedTypeKind.Function || signature.ParameterModes.Length != call.Arguments.Length
            || signature.Arguments[^1] != result || call.Target is AstGenericApplication)
        { return Reject(call, "Indirect call does not match its resolved function signature"); }

        var function = Expression(target);
        if (function.Type != signature)
        { return Reject(call, "Indirect call target changed type during FIR lowering"); }

        var arguments = ImmutableArray.CreateBuilder<FirArgument>(call.Arguments.Length);
        for (int index = 0; index < call.Arguments.Length; index++)
        {
            _cancellation.ThrowIfCancellationRequested();
            var source = call.Arguments[index];
            var mode = signature.ParameterModes[index];
            var expectedSourceMode = mode switch
            {
                AstPassingMode.Ref => AstArgumentMode.Ref,
                AstPassingMode.Out => AstArgumentMode.Out,
                _ => AstArgumentMode.Value,
            };
            if (source.Name is not null || source.IsTrailingLambda || source.Mode != expectedSourceMode
                || source.Value is not AstExpression argument || source.Mode == AstArgumentMode.OutVariable)
            { return Reject(call, "Indirect calls require positional arguments with explicit matching passing modes"); }
            var value = Expression(argument);
            if (value.Type != signature.Arguments[index])
            { return Reject(call, "Indirect argument type differs from its function signature"); }
            arguments.Add(new(index, mode, value));
        }

        return new FirIndirectCall(function, arguments.ToImmutable(), result, call.Origin);
    }
}
