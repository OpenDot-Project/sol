// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaGenericTargetVerifierTests
{
    [Fact]
    public void VerifierRejectsLambdaCallWithTypeArgumentsChangedAfterBinding()
    {
        var program = Compile("func <T> identity(value: T): T = value\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (int) -> int = { value -> identity(value) }\n"
            + "val text: (String) -> String = identity\n} }");
        var lambda = Assert.Single(program.Functions, function => function.LambdaSource is not null);
        var intTarget = Assert.Single(program.Functions, function => function.Name == "identity"
            && function.SpecializationArguments.SingleOrDefault()?.Name == "int");
        var returned = Assert.IsType<FirReturn>(Assert.Single(lambda.Body!.Statements));
        var call = Assert.IsType<FirCall>(returned.Value);
        Assert.Equal(intTarget.Id, call.Target);

        var stringTarget = Assert.Single(program.Functions, function => function.Name == "identity"
            && function.SpecializationArguments.SingleOrDefault()?.Name == "std.core.String");
        var forgedCall = call with { TypeArguments = stringTarget.SpecializationArguments };
        var forgedLambda = ReplaceReturnValue(lambda, call, forgedCall);
        var result = FirVerifier.Verify(Replace(program, forgedLambda), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("specialization arguments differ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsLambdaFunctionReferenceRetargetedToAnotherClosedInstance()
    {
        var program = Compile("func <T> identity(value: T): T = value\n"
            + "object Main { func main(args: []String): () {\n"
            + "val provider: () -> ((int) -> int) = { -> identity }\n"
            + "val text: (String) -> String = identity\n} }");
        var lambda = Assert.Single(program.Functions, function => function.LambdaSource is not null);
        var intTarget = Assert.Single(program.Functions, function => function.Name == "identity"
            && function.SpecializationArguments.SingleOrDefault()?.Name == "int");
        var stringTarget = Assert.Single(program.Functions, function => function.Name == "identity"
            && function.SpecializationArguments.SingleOrDefault()?.Name == "std.core.String");
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var stringReference = main.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirFunctionReference>()
            .Single(reference => reference.Target == stringTarget.Id);
        var returned = Assert.IsType<FirReturn>(Assert.Single(lambda.Body!.Statements));
        var reference = Assert.IsType<FirFunctionReference>(returned.Value);
        Assert.Equal(intTarget.Id, reference.Target);

        var forgedReference = reference with
        {
            Target = stringTarget.Id,
            Type = stringReference.Type,
        };
        var forgedLambda = ReplaceReturnValue(lambda, reference, forgedReference);
        var result = FirVerifier.Verify(Replace(program, forgedLambda), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("type arguments differ from source generic inference",
                StringComparison.OrdinalIgnoreCase));
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

    private static FirFunction ReplaceReturnValue(FirFunction function, FirExpression before, FirExpression after)
    {
        var statements = function.Body!.Statements.ToBuilder();
        int index = -1;
        for (int candidate = 0; candidate < statements.Count; candidate++)
        {
            if (statements[candidate] is FirReturn { Value: { } value }
                && ReferenceEquals(value, before))
            { index = candidate; break; }
        }
        Assert.True(index >= 0);
        var returned = (FirReturn)statements[index];
        statements[index] = returned with { Value = after };
        return function with { Body = function.Body with { Statements = statements.ToImmutable() } };
    }
}
