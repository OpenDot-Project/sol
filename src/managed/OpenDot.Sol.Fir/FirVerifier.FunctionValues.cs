// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private void ValidateNestedFunctionEffects(ResolvedType root, AstOrigin origin)
        {
            var pending = new Stack<ResolvedType>();
            var visited = new HashSet<int>();
            pending.Push(root);
            while (pending.TryPop(out var current))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!visited.Add(current.Id)) { continue; }
                if (current.Kind == ResolvedTypeKind.Function)
                {
                    ValidateEffects(current.Effects, origin);
                }
                foreach (var argument in current.Arguments) { pending.Push(argument); }
            }
        }

        private void CheckFunctionReference(FirFunctionReference reference)
        {
            if (reference.Type.Kind != ResolvedTypeKind.Function)
            { Fail("A function reference must have a function type.", reference.Origin); return; }
            if (reference.Target < 0 || reference.Target >= program.Functions.Length)
            { Fail("Function reference target is outside this FIR program.", reference.Origin); return; }

            var target = program.Functions[reference.Target];
            var signature = reference.Type;
            if (target.RuntimeFunction != FirRuntimeFunction.None || target.Body is null
                || target.SpecializationArguments.IsDefault)
            { Fail("Function references require a source body with a closed target.", reference.Origin); return; }
            if (target.Parameters.Length != signature.ParameterModes.Length
                || signature.Arguments.Length != signature.ParameterModes.Length + 1)
            { Fail("Function reference arity differs from its source target.", reference.Origin); return; }
            for (int index = 0; index < target.Parameters.Length; index++)
            {
                if (target.Parameters[index].Type != signature.Arguments[index]
                    || target.Parameters[index].Mode != signature.ParameterModes[index])
                { Fail("Function reference parameter ABI differs from its resolved signature.", reference.Origin); }
            }
            if (target.ReturnType != signature.Arguments[^1])
            { Fail("Function reference result ABI differs from its resolved signature.", reference.Origin); }
            if (!target.Effects.SequenceEqual(signature.Effects))
            { Fail("Function reference checked effects differ from its resolved signature.", reference.Origin); }

            if (!target.SpecializationArguments.IsEmpty)
            {
                if (_function.SpecializationArguments.IsEmpty) { _genericInstanceRoots.Add(target.Id); }
                else
                {
                    if (!_genericInstanceEdges.TryGetValue(_function.Id, out var nested))
                    { nested = []; _genericInstanceEdges.Add(_function.Id, nested); }
                    nested.Add(target.Id);
                }
            }
        }

        private void CheckIndirectCall(FirIndirectCall call)
        {
            Expression(call.Function);
            var signature = call.Function.Type;
            if (signature.Kind != ResolvedTypeKind.Function
                || signature.ParameterModes.Length != call.Arguments.Length
                || signature.Arguments.Length != call.Arguments.Length + 1)
            { Fail("Indirect call arity differs from its function value type.", call.Origin); return; }
            Exact(call.Type, signature.Arguments[^1], call.Origin);
            foreach (var effect in signature.Effects.Where(IsChecked))
            {
                if (!_function.Effects.Any(budget => program.Analysis.Types.ClassifyConversion(
                    effect, budget, cancellation) != IntrinsicConversion.None))
                { Fail("Indirect call exceeds the function's checked effect budget.", call.Origin); }
            }

            for (int index = 0; index < call.Arguments.Length; index++)
            {
                var argument = call.Arguments[index];
                var mode = signature.ParameterModes[index];
                if (argument.Parameter != index || argument.Mode != mode)
                { Fail("Indirect argument position or passing mode differs from its function signature.", call.Origin); }
                Expression(argument.Value);
                Exact(argument.Value.Type, signature.Arguments[index], argument.Value.Origin);
                if (mode is AstPassingMode.Ref or AstPassingMode.Out)
                {
                    if (argument.Value is not FirRead read)
                    { Fail("Indirect ref/out arguments must name a caller storage slot.", argument.Value.Origin); continue; }
                    var slot = Slot(read.Slot, read.Origin);
                    if (slot?.Mutable != true && slot?.Mode is not (AstPassingMode.Ref or AstPassingMode.Out))
                    { Fail("Indirect ref/out argument requires writable caller storage.", argument.Value.Origin); }
                }
            }
        }

        private void MatchFunctionReference(FirFunctionReference actual, AstExpression source)
        {
            if (source is AstLambda lambda)
            {
                if (actual.Target < 0 || actual.Target >= program.Functions.Length
                    || !ReferenceEquals(program.Functions[actual.Target].LambdaSource, lambda))
                { Fail("FIR lambda reference differs from its source lambda identity.", actual.Origin); }
                if (GetSourceExpression(lambda)?.Type is not { Kind: ResolvedTypeKind.Function } type
                    || Substitute(type) != actual.Type)
                { Fail("FIR lambda reference signature differs from source analysis.", actual.Origin); }
                return;
            }

            var binding = GetSourceExpression(source);
            if (binding?.FunctionValueTarget is not { } target)
            { Fail("FIR function reference has no source-selected function declaration.", actual.Origin); return; }
            if (actual.Target < 0 || actual.Target >= program.Functions.Length
                || !ReferenceEquals(program.Functions[actual.Target].Origin.Element, target.Origin.Element))
            {
                Fail("FIR function reference target differs from source name resolution.", actual.Origin);
                return;
            }
            var selectedArguments = binding.FunctionValueTypeArguments.Select(Substitute).ToImmutableArray();
            if (selectedArguments.IsDefault
                || !selectedArguments.SequenceEqual(program.Functions[actual.Target].SpecializationArguments))
            { Fail("FIR function reference type arguments differ from source generic inference.", actual.Origin); }
        }

        private void MatchPositionalRead(FirRead actual, AstAtom source)
        {
            if (_function.LambdaSource is not { Form: AstLambdaForm.Positional } lambda
                || !int.TryParse(source.Spelling.AsSpan(1), out int position)
                || (uint)position >= (uint)_function.Parameters.Length)
            {
                Fail("Positional lambda read has no matching synthetic parameter.", actual.Origin);
                return;
            }
            SameOrigin(actual, source);
            var parameter = _function.Parameters[position];
            if (actual.Slot != parameter.Id || actual.Type != parameter.Type)
            { Fail("Positional lambda read differs from its source parameter index.", actual.Origin); }
        }

        private void MatchIndirectCall(FirIndirectCall actual, AstCall source)
        {
            if (source.Target is not { } target)
            { Fail("Indirect source call has no function-value expression.", source.Origin); return; }
            MatchExpression(actual.Function, target);
            if (actual.Arguments.Length != source.Arguments.Length)
            { Fail("Indirect FIR argument count differs from source syntax.", actual.Origin); }
            for (int index = 0; index < Math.Min(actual.Arguments.Length, source.Arguments.Length); index++)
            {
                var lowered = actual.Arguments[index];
                var argument = source.Arguments[index];
                AstPassingMode sourceMode = argument.Mode switch
                {
                    AstArgumentMode.Ref => AstPassingMode.Ref,
                    AstArgumentMode.Out => AstPassingMode.Out,
                    AstArgumentMode.Value => AstPassingMode.Value,
                    _ => AstPassingMode.Vararg,
                };
                if (lowered.Parameter != index || lowered.Mode != sourceMode || argument.Name is not null || argument.IsTrailingLambda
                    || argument.Value is not AstExpression expression)
                { Fail("Indirect FIR argument mapping differs from source syntax.", actual.Origin); continue; }
                MatchExpression(lowered.Value, expression);
            }
        }
    }
}
