// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirGenericDefaultTests
{
    private static FirCompilation Compile(string body, string declarations = "") => FirCompilation.Create([
        AstTestSources.Parse("import std.stdio\n" + declarations + "\nobject Main { func main(args: []String) throws IOException { " + body + " } }")],
        TestContext.Current.CancellationToken);

    [Fact]
    public void GenericDefaultsReadEarlierCapturedValuesAfterExplicitArguments()
    {
        var compilation = Compile(
            "val reordered = cascade(third = mark(30), first = mark(10)); val chained = cascade(first = mark(1)); println(reordered); println(chained)",
            "func <T> cascade(first: T, second: T = first, third: T = second): T = third\nfunc mark(value: int): int throws IOException { println(value); value }");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var verified = compilation.Program;
        var main = verified.Program.Functions.Single(function => function.Name == "main");
        var calls = main.Body!.Statements.OfType<FirDeclare>().Select(declaration => declaration.Initializer)
            .OfType<FirCall>().ToArray();
        Assert.Equal(2, calls.Length);

        var reordered = calls[0];
        Assert.Equal(new[] { 2, 0, 1 }, reordered.Arguments.Select(argument => argument.Parameter));
        Assert.IsType<FirCall>(reordered.Arguments[0].Value);
        Assert.IsType<FirCall>(reordered.Arguments[1].Value);
        var reorderedDefault = Assert.IsType<FirCallValueReference>(reordered.Arguments[2].Value);
        Assert.Equal(1, reorderedDefault.EvaluationIndex);
        Assert.Equal("int", reorderedDefault.Type.Name);

        var chained = calls[1];
        Assert.Equal(new[] { 0, 1, 2 }, chained.Arguments.Select(argument => argument.Parameter));
        var firstDefault = Assert.IsType<FirCallValueReference>(chained.Arguments[1].Value);
        var secondDefault = Assert.IsType<FirCallValueReference>(chained.Arguments[2].Value);
        Assert.Equal(0, firstDefault.EvaluationIndex);
        Assert.Equal(1, secondDefault.EvaluationIndex);
        Assert.Equal("int", verified.Program.Functions[reordered.Target].SpecializationArguments.Single().Name);

        string json = FirJson.Serialize(verified, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(7, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Contains("\"kind\": \"FirCallValueReference\"", json);
        Assert.Contains("\"evaluationIndex\": 1", json);
        Assert.Equal(json, FirJson.Serialize(verified, TestContext.Current.CancellationToken));
        Assert.NotEmpty(LoweringTrace.Inspect(verified, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ScalarDefaultsLowerOnlyWhenOmitted()
    {
        var compilation = Compile("val omitted = fallback(); val supplied = fallback(9); println(omitted); println(supplied)",
            "func fallback(value: int = -(2 + 3)): int = value");

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var main = compilation.Program.Program.Functions.Single(function => function.Name == "main");
        var calls = main.Body!.Statements.OfType<FirDeclare>().Select(declaration => declaration.Initializer)
            .OfType<FirCall>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.IsType<FirUnary>(Assert.Single(calls[0].Arguments).Value);
        Assert.IsType<FirConstant>(Assert.Single(calls[1].Arguments).Value);
    }

    [Fact]
    public void EffectfulOrNonFreeDefaultsRemainOutsideTheNativeSlice()
    {
        var effectful = Compile("println(effectful())", "func effectful(value: int = mark(1)): int throws IOException = value\nfunc mark(value: int): int throws IOException { println(value); value }");
        Assert.Null(effectful.Program);
        Assert.Contains(effectful.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");

        var member = Compile("println(Box.fallback())", "object Box { func fallback(value: int = 4): int = value }");
        Assert.Null(member.Program);
        Assert.Contains(member.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }
}
