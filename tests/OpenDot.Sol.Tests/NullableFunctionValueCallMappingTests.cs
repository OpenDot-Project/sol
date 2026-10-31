// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NullableFunctionValueCallMappingTests
{
    private sealed record Scenario(AnalysisSession Session, BodyAnalysis Bodies, FirCompilation Fir);

    [Fact]
    public void DeclarationDefaultsKeepKotlinArgumentOrderingWhileNullableCallbackUsesEveryOrderedSlot()
    {
        var scenario = Analyze("func combine(first: int, second: int = first + 10, "
            + "third: int = second + 100): int = first + second + third\n"
            + "func invoke(): int {\n"
            + "val positionalThenNamed = combine(1, third = 3)\n"
            + "val reorderedNamed = combine(third = 3, first = 1)\n"
            + "val trailingDefault = combine(first = 1, second = 2)\n"
            + "val callback: ((int, int, int) -> int)? = combine\n"
            + "if callback == null { return positionalThenNamed + reorderedNamed + trailingDefault }\n"
            + "return positionalThenNamed + reorderedNamed + trailingDefault + callback(1, 2, 3)\n"
            + "}");

        AssertNoSemanticErrors(scenario.Bodies);
        var direct = scenario.Bodies.Calls.Where(binding => binding.Target.Name == "combine")
            .OrderBy(binding => binding.Call.Origin.Span.Start).ToArray();
        Assert.Equal(3, direct.Length);
        Assert.Equal("combine(1, third = 3)", direct[0].Call.Origin.Text);
        Assert.Equal(new[] { 0, 2 }, direct[0].Arguments.Select(argument => argument.ParameterIndex));
        Assert.Equal(new[] { 1 }, direct[0].DefaultParameters);
        Assert.Equal("combine(third = 3, first = 1)", direct[1].Call.Origin.Text);
        Assert.Equal(new[] { 2, 0 }, direct[1].Arguments.Select(argument => argument.ParameterIndex));
        Assert.Equal(new[] { 1 }, direct[1].DefaultParameters);
        Assert.Equal("combine(first = 1, second = 2)", direct[2].Call.Origin.Text);
        Assert.Equal(new[] { 0, 1 }, direct[2].Arguments.Select(argument => argument.ParameterIndex));
        Assert.Equal(new[] { 2 }, direct[2].DefaultParameters);

        var program = Verified(scenario.Fir).Program;
        var invoke = Function(program, "invoke");
        var calls = Expressions(invoke.Body!).OfType<FirCall>()
            .Where(call => program.Functions[call.Target].Name == "combine").ToArray();
        Assert.Equal(new[] { "combine(1, third = 3)", "combine(third = 3, first = 1)",
            "combine(first = 1, second = 2)" }, calls.Select(call => call.Origin.Text));
        Assert.Equal(new[] { 0, 2, 1 }, calls[0].Arguments.Select(argument => argument.Parameter));
        Assert.Equal(new[] { 2, 0, 1 }, calls[1].Arguments.Select(argument => argument.Parameter));
        Assert.Equal(new[] { 0, 1, 2 }, calls[2].Arguments.Select(argument => argument.Parameter));

        var indirect = Assert.Single(Expressions(invoke.Body!).OfType<FirIndirectCall>());
        Assert.Equal(new[] { 0, 1, 2 }, indirect.Arguments.Select(argument => argument.Parameter));
        Assert.All(indirect.Arguments, argument => Assert.Equal(AstPassingMode.Value, argument.Mode));
        var callbackSlot = Assert.Single(invoke.Slots, slot => slot.Name == "callback");
        Assert.True(callbackSlot.Type.IsNullable);
        Assert.Equal(callbackSlot.Type.Arguments[0], indirect.Function.Type);
    }

    [Theory]
    [InlineData("callback(first = 1, second = 2, third = 3)")]
    [InlineData("callback(1)")]
    [InlineData("callback(1, 2, 3, 4)")]
    [InlineData("callback(first = 1, 2, 3)")]
    public void NullableFunctionValueDoesNotRecoverDeclarationNamesOrDefaults(string invocation)
    {
        var scenario = Analyze("func combine(first: int, second: int = 2, third: int = 3): int = first + second + third\n"
            + "func invoke(callback: ((int, int, int) -> int)?): int {\n"
            + "if callback == null { return 0 }\n"
            + invocation + "\n"
            + "return 0\n}");

        Assert.Contains(scenario.Bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.Null(scenario.Fir.Program);
        Assert.Contains(scenario.Fir.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }

    [Fact]
    public void NullableCallbackToVarargDeclarationUsesOneArraySlotAndAcceptsAnEmptyArray()
    {
        var scenario = Analyze("func collect(vararg values: int): int = values.length\n"
            + "func invoke(): int {\n"
            + "val callback: (([]int) -> int)? = collect\n"
            + "if callback == null { return 0 }\n"
            + "callback([])\n}");

        AssertNoSemanticErrors(scenario.Bodies);
        var program = Verified(scenario.Fir).Program;
        var invoke = Function(program, "invoke");
        var call = Assert.Single(Expressions(invoke.Body!).OfType<FirIndirectCall>());
        var argument = Assert.Single(call.Arguments);
        var emptyArray = Assert.IsType<FirArrayLiteral>(argument.Value);

        Assert.Equal(AstPassingMode.Value, argument.Mode);
        Assert.Equal(ResolvedTypeKind.Array, argument.Value.Type.Kind);
        Assert.Equal("int", Assert.Single(argument.Value.Type.Arguments).Name);
        Assert.Empty(emptyArray.Elements);
        Assert.Equal(argument.Value.Type, call.Function.Type.Arguments[0]);
        Assert.True(Assert.Single(invoke.Slots, slot => slot.Name == "callback").Type.IsNullable);
    }

    [Theory]
    [InlineData("callback(1, 2)", "")]
    [InlineData("callback(*items)", ", items: []int")]
    public void NullableVarargFunctionValueRejectsExpansionAndSpreadAdapters(string invocation, string extraParameter)
    {
        var scenario = Analyze("func invoke(callback: (([]int) -> int)?" + extraParameter + "): int {\n"
            + "if callback == null { return 0 }\n"
            + invocation + "\n"
            + "return 0\n}");

        string expectedCode = invocation.Contains('*') ? "SOL2244" : "SOL2221";
        Assert.Contains(scenario.Bodies.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
        Assert.Null(scenario.Fir.Program);
        Assert.Contains(scenario.Fir.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void GenericFunctionReferenceClosesFromNullableContextBeforeIndirectInvocation()
    {
        var scenario = Analyze("func <T> identity(value: T): T = value\n"
            + "func invoke(): int {\n"
            + "val callback: ((int) -> int)? = identity\n"
            + "if callback == null { return 0 }\n"
            + "callback(41)\n}");

        AssertNoSemanticErrors(scenario.Bodies);
        var referenceBinding = Assert.Single(scenario.Bodies.Expressions,
            binding => binding.FunctionValueTarget?.Name == "identity");
        Assert.Equal("int", Assert.Single(referenceBinding.FunctionValueTypeArguments).Name);
        var program = Verified(scenario.Fir).Program;
        var invoke = Function(program, "invoke");
        var reference = Assert.Single(Expressions(invoke.Body!).OfType<FirFunctionReference>());
        var specialized = program.Functions[reference.Target];
        var indirect = Assert.Single(Expressions(invoke.Body!).OfType<FirIndirectCall>());

        Assert.Equal("int", Assert.Single(specialized.SpecializationArguments).Name);
        Assert.Equal("int", Assert.Single(indirect.Arguments).Value.Type.Name);
        Assert.Equal("int", indirect.Type.Name);
        Assert.Equal("int", reference.Type.Arguments[0].Name);
    }

    [Fact]
    public void GenericContextCannotUnifyOneParameterAndResultWithDifferentCallbackTypes()
    {
        var scenario = Analyze("func <T> identity(value: T): T = value\n"
            + "func invoke(): int { val callback: ((int) -> bool)? = identity; 0 }");

        Assert.Contains(scenario.Bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.Null(scenario.Fir.Program);
        Assert.Contains(scenario.Fir.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    [Fact]
    public void NullableCallbackRefAndOutArgumentsRetainTheirExactModeAndStorageSlots()
    {
        var scenario = Analyze("func invoke(callback: ((ref int, out int) -> ())?): int {\n"
            + "var current = 40\n"
            + "var result: int\n"
            + "if callback == null { return 0 }\n"
            + "callback(ref current, out result)\n"
            + "result\n}");

        AssertNoSemanticErrors(scenario.Bodies);
        var program = Verified(scenario.Fir).Program;
        var invoke = Function(program, "invoke");
        var indirect = Assert.Single(Expressions(invoke.Body!).OfType<FirIndirectCall>());

        Assert.Equal(new[] { 0, 1 }, indirect.Arguments.Select(argument => argument.Parameter));
        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Out },
            indirect.Arguments.Select(argument => argument.Mode));
        Assert.All(indirect.Arguments, argument => Assert.IsType<FirRead>(argument.Value));
        Assert.Equal("int", indirect.Arguments[0].Value.Type.Name);
        Assert.Equal("int", indirect.Arguments[1].Value.Type.Name);
    }

    [Theory]
    [InlineData("callback(current)", "var current = 40")]
    [InlineData("callback(out current)", "var current = 40")]
    [InlineData("callback(ref current)", "var current: long = 40")]
    public void NullableRefCallbackRejectsModeOrExactStorageMismatches(string invocation, string local)
    {
        var scenario = Analyze("func invoke(callback: ((ref int) -> ())?): () {\n"
            + local + "\n"
            + "if callback == null { return () }\n"
            + invocation + "\n"
            + "return ()\n}");

        Assert.Contains(scenario.Bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2244");
        Assert.Null(scenario.Fir.Program);
        Assert.Contains(scenario.Fir.Diagnostics, diagnostic => diagnostic.Code == "SOL2244");
    }

    [Fact]
    public void VarianceConversionMayBindInSourceButFIRRejectsItsMissingNativeAdapter()
    {
        var scenario = Analyze("open class Animal {}\nclass Dog: Animal {}\n"
            + "func acceptAnimal(value: Animal): Dog { for {} }\n"
            + "func invoke(dog: Dog): Animal {\n"
            + "val callback: ((Dog) -> Animal)? = acceptAnimal\n"
            + "if callback == null { return dog }\n"
            + "callback(dog)\n}");

        Assert.False(scenario.Bodies.HasErrors, Diagnostics(scenario.Bodies));
        Assert.True(scenario.Bodies.IsComplete, Diagnostics(scenario.Bodies));
        var target = Assert.Single(scenario.Bodies.Expressions,
            binding => binding.FunctionValueTarget?.Name == "acceptAnimal");
        var callbackProperty = scenario.Session.Files.SelectMany(file => file.Nodes)
            .OfType<AstProperty>().Single(property => property.Name == "callback");
        Assert.Equal(IntrinsicConversion.Reference,
            scenario.Bodies.Types.ClassifyConversion(target.Type!,
                scenario.Bodies.Sources.Types.GetType(callbackProperty.Type!), TestContext.Current.CancellationToken));
        Assert.Null(scenario.Fir.Program);
        Assert.Contains(scenario.Fir.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    [Fact]
    public void NullableCallbackArgumentTypeMismatchIsRejectedBeforeFIRAdmission()
    {
        var scenario = Analyze("func invoke(callback: ((int) -> int)?, value: bool): int {\n"
            + "if callback == null { return 0 }\n"
            + "callback(value)\n}");

        Assert.Contains(scenario.Bodies.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.Null(scenario.Fir.Program);
        Assert.Contains(scenario.Fir.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    private static Scenario Analyze(string declarations)
    {
        var session = AstTestSources.Session("module app\n" + declarations);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, Diagnostics(sources));
        var bodies = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var fir = FirCompilation.Create(session.Files, TestContext.Current.CancellationToken, library: true);
        return new(session, bodies, fir);
    }

    private static void AssertNoSemanticErrors(BodyAnalysis bodies)
    {
        Assert.False(bodies.HasErrors, Diagnostics(bodies));
        Assert.True(bodies.IsComplete, Diagnostics(bodies));
    }

    private static VerifiedFirProgram Verified(FirCompilation compilation)
    {
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return Assert.IsType<VerifiedFirProgram>(compilation.Program);
    }

    private static FirFunction Function(FirProgram program, string name) =>
        Assert.Single(program.Functions, function => function.Name == name);

    private static IEnumerable<FirExpression> Expressions(FirBlock block)
    {
        foreach (var statement in block.Statements)
        {
            switch (statement)
            {
                case FirDeclare { Initializer: { } initializer }:
                    foreach (var expression in Expressions(initializer)) { yield return expression; }
                    break;
                case FirStore store:
                    foreach (var expression in Expressions(store.Value)) { yield return expression; }
                    break;
                case FirEvaluate evaluate:
                    foreach (var expression in Expressions(evaluate.Value)) { yield return expression; }
                    break;
                case FirReturn { Value: { } value }:
                    foreach (var expression in Expressions(value)) { yield return expression; }
                    break;
                case FirIf conditional:
                    foreach (var expression in Expressions(conditional.Condition)) { yield return expression; }
                    foreach (var expression in Expressions(conditional.Then)) { yield return expression; }
                    if (conditional.Else is { } alternative)
                    {
                        foreach (var expression in Expressions(alternative)) { yield return expression; }
                    }
                    break;
                case FirLoop loop:
                    if (loop.Condition is { } condition)
                    {
                        foreach (var expression in Expressions(condition)) { yield return expression; }
                    }
                    foreach (var expression in Expressions(loop.Body)) { yield return expression; }
                    break;
            }
        }
    }

    private static IEnumerable<FirExpression> Expressions(FirExpression expression)
    {
        yield return expression;
        IEnumerable<FirExpression> children = expression switch
        {
            FirIndirectCall call => new[] { call.Function }.Concat(call.Arguments.Select(argument => argument.Value)),
            FirCall call => call.Arguments.Select(argument => argument.Value),
            FirBinary binary => [binary.Left, binary.Right],
            FirUnary unary => [unary.Value],
            FirArrayLength length => [length.Array],
            FirArrayIndex index => [index.Array, index.Index],
            FirArrayLiteral array => array.Elements,
            FirIntrinsic intrinsic => intrinsic.Arguments,
            _ => [],
        };
        foreach (var child in children)
        {
            foreach (var descendant in Expressions(child)) { yield return descendant; }
        }
    }

    private static string Diagnostics(SourceAnalysis analysis) => string.Join('\n', analysis.Diagnostics
        .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics
        .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
