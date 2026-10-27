// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirIndirectCallInvariantTests
{
    [Fact]
    public void GenericCallbackInstanceCarriesClosedSignatureEffectsAndIndirectFlowEdge()
    {
        var program = Compile("""
            import std.stdio
            func identify(value: int): int throws IOException = value
            func <T> invoke(callback: (int) -> T throws IOException, value: int): T throws IOException = callback(value)
            object Main { func main(args: []String): () throws IOException {
                val result = invoke(identify, 41)
            } }
            """);
        var instance = Assert.Single(program.Functions,
            function => function.Name == "invoke" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal("int", Assert.Single(instance.SpecializationArguments).Name);

        var indirect = Assert.IsType<FirIndirectCall>(Assert.IsType<FirReturn>(
            Assert.Single(instance.Body!.Statements)).Value);
        Assert.Equal("int", indirect.Type.Name);
        Assert.Equal("int", indirect.Function.Type.Arguments[0].Name);
        Assert.Equal("int", indirect.Function.Type.Arguments[^1].Name);
        Assert.Equal("std.stdio.IOException", Assert.Single(indirect.Function.Type.Effects).Name);

        var callSite = Assert.Single(FirVerifier.Verify(program, TestContext.Current.CancellationToken)
            .Program!.FlowGraphs.Single(graph => graph.FunctionId == instance.Id).Blocks.SelectMany(block => block.Calls));
        Assert.Null(callSite.Target);
        Assert.Equal("std.stdio.IOException", Assert.Single(callSite.Effects).Name);

        using var json = JsonDocument.Parse(FirJson.Serialize(
            FirVerifier.Verify(program, TestContext.Current.CancellationToken).Program!,
            TestContext.Current.CancellationToken));
        Assert.Equal(6, json.RootElement.GetProperty("schema").GetInt32());
        var serializedInstance = Assert.Single(json.RootElement.GetProperty("functions").EnumerateArray(),
            function => function.GetProperty("id").GetInt32() == instance.Id);
        Assert.Equal([Assert.Single(instance.SpecializationArguments).Id], serializedInstance
            .GetProperty("specializationArguments").EnumerateArray().Select(argument => argument.GetInt32()));
        Assert.Contains("\"kind\": \"FirIndirectCall\"", json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void VerifierRejectsFunctionReferenceRetargetedToAnotherClosedInstance()
    {
        var program = Compile("""
            func <T> identity(value: T): T = value
            object Main { func main(args: []String): () {
                val number: (int) -> int = identity
                val text: (String) -> String = identity
            } }
            """);
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var declarations = main.Body!.Statements.OfType<FirDeclare>()
            .Where(declaration => declaration.Initializer is FirFunctionReference).ToArray();
        var number = Assert.IsType<FirFunctionReference>(declarations[0].Initializer);
        var text = Assert.IsType<FirFunctionReference>(declarations[1].Initializer);
        Assert.NotEqual(number.Target, text.Target);

        var forgedReference = number with { Target = text.Target, Type = text.Type };
        var forgedMain = ReplaceInitializer(main, number, forgedReference);
        var result = FirVerifier.Verify(Replace(program, forgedMain), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("type arguments differ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsIndirectArgumentModeChangedAfterGenericBinding()
    {
        var program = Compile("""
            func <T> invoke(value: T, callback: (T) -> T): T = callback(value)
            func identity(value: int): int = value
            object Main { func main(args: []String): () {
                val answer = invoke(41, identity)
            } }
            """);
        var instance = Assert.Single(program.Functions,
            function => function.Name == "invoke" && !function.SpecializationArguments.IsEmpty);
        var body = instance.Body!;
        var returned = Assert.IsType<FirReturn>(Assert.Single(body.Statements));
        var indirect = Assert.IsType<FirIndirectCall>(returned.Value);
        var argument = Assert.Single(indirect.Arguments);
        var forgedCall = indirect with
        {
            Arguments = indirect.Arguments.SetItem(0, argument with { Mode = OpenDot.Sol.Ast.AstPassingMode.Ref }),
        };
        var forgedInstance = instance with
        {
            Body = body with { Statements = body.Statements.SetItem(0, returned with { Value = forgedCall }) },
        };

        var result = FirVerifier.Verify(Replace(program, forgedInstance), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("passing mode", StringComparison.OrdinalIgnoreCase));
    }

    private static FirProgram Compile(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program.Program;
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };

    private static FirFunction ReplaceInitializer(FirFunction function, FirFunctionReference before,
        FirFunctionReference after)
    {
        var statements = function.Body!.Statements.ToBuilder();
        int index = -1;
        for (int candidate = 0; candidate < statements.Count; candidate++)
        {
            if (statements[candidate] is FirDeclare { Initializer: { } initializer }
                && ReferenceEquals(initializer, before)) { index = candidate; break; }
        }
        Assert.True(index >= 0);
        var declaration = (FirDeclare)statements[index];
        statements[index] = declaration with { Initializer = after };
        return function with { Body = function.Body with { Statements = statements.ToImmutable() } };
    }
}
