// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaFunctionValueBoundaryTests
{
    [Fact]
    public void LocalValueCaptureIsRejectedAtTheLambdaSource()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val seed = 41\n"
            + "val callback: (int) -> int = { value -> value + seed }\n"
            + "val result = callback(1)\n} }");
        var lambda = SingleLambda(source);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambda.Origin.Span);
    }

    [Theory]
    [InlineData("class Counter { val count: int = 1; func callback(): () -> int = { this.count } }")]
    [InlineData("object Counter { val count: int = 1; func callback(): () -> int = { count } }")]
    public void ReceiverAndImplicitMemberCapturesAreRejectedAtTheLambdaSource(string declaration)
    {
        var source = AstTestSources.Parse(declaration);
        var lambda = SingleLambda(source);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken, library: true);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambda.Origin.Span);
    }

    [Theory]
    [InlineData("func capture(ref value: int): () -> int = { value }")]
    [InlineData("ref struct View {}\nfunc capture(value: View) = { value }")]
    public void RefAndRefLikeCapturesRemainRejectedBySourceAnalysis(string declaration)
    {
        var source = AstTestSources.Parse(declaration);
        var lambda = SingleLambda(source);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken, library: true);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2262"
            && diagnostic.Span.Start >= lambda.Origin.Span.Start
            && diagnostic.Span.End <= lambda.Origin.Span.End);
    }

    [Fact]
    public void CapturedLambdaInsideClosedGenericBodyIsNotLiftedAsAnOpenInstance()
    {
        var source = AstTestSources.Parse("func <T> transform(value: T): T {\n"
            + "val callback: (T) -> T = { item -> item }\n"
            + "callback(value)\n}\n"
            + "object Main { func main(args: []String) { val result = transform(1) } }");
        var lambda = SingleLambda(source);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambda.Origin.Span);
    }

    [Fact]
    public void LambdaInsideLocalFunctionIsNotLiftedAsATopLevelFunction()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "func local(): () -> int = { -> 42 }\n"
            + "val callback = local()\n"
            + "val result = callback()\n} }");
        var lambda = Assert.Single(source.Root.Descendants().OfType<AstLambda>());

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambda.Origin.Span);
    }

    [Fact]
    public void FunctionVarianceThatNeedsAnAbiAdapterIsNotEmittedAsABareCodePointer()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val callback: (String) -> Object = { value: Object -> value }\n"
            + "val result = callback(\"sol\")\n} }");
        var lambda = SingleLambda(source);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambda.Origin.Span);
    }

    [Fact]
    public void NullableFunctionValueDoesNotCrossTheCurrentNativePointerBoundary()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val callback: (() -> int)? = { -> 1 }\n} }");

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code is "SOL2220" or "SOL2400");
    }

    [Fact]
    public void VerifierRejectsAFunctionTargetWhoseLambdaSourceMarkerWasRemoved()
    {
        var program = CompileWithUnusedCapturingGeneric(out _).Program!;
        var lambdaFunction = Assert.Single(program.Program.Functions.Where(function => function.LambdaSource is not null));
        var forgedFunction = lambdaFunction with { LambdaSource = null };

        var verification = FirVerifier.Verify(program.Program with
        {
            Functions = program.Program.Functions.SetItem(forgedFunction.Id, forgedFunction),
        }, TestContext.Current.CancellationToken);

        Assert.Null(verification.Program);
        Assert.Contains(verification.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void VerifierRejectsAFunctionTargetRetargetedToACapturingLambda()
    {
        var compilation = CompileWithUnusedCapturingGeneric(out var capturedLambda);
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var lambdaFunction = Assert.Single(program.Functions.Where(function => function.LambdaSource is not null));
        var forgedFunction = lambdaFunction with
        {
            LambdaSource = capturedLambda,
            Origin = capturedLambda.Origin,
        };

        var verification = FirVerifier.Verify(program with
        {
            Functions = program.Functions.SetItem(forgedFunction.Id, forgedFunction),
        }, TestContext.Current.CancellationToken);

        Assert.Null(verification.Program);
        Assert.Contains(verification.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Span == capturedLambda.Origin.Span);
    }

    [Fact]
    public void VerifierRejectsDuplicateSyntheticFunctionsForOneSourceLambda()
    {
        var compilation = CompileWithUnusedCapturingGeneric(out _);
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var lambdaFunction = Assert.Single(program.Functions.Where(function => function.LambdaSource is not null));
        var duplicate = lambdaFunction with { Id = program.Functions.Length };

        var verification = FirVerifier.Verify(program with
        {
            Functions = program.Functions.Add(duplicate),
        }, TestContext.Current.CancellationToken);

        Assert.Null(verification.Program);
        Assert.Contains(verification.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Span == lambdaFunction.LambdaSource!.Origin.Span);
    }

    [Fact]
    public void VerifierRejectsForgedIndirectCheckedEffectInsideLambda()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("import std.stdio\n"
            + "object Main { func main(args: []String) {\n"
            + "val empty: () -> () = { -> () }\n"
            + "val invoke: (() -> ()) -> () = { callback -> callback() }\n"
            + "invoke(empty)\n} }")], TestContext.Current.CancellationToken);
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var invoker = Assert.Single(program.Functions.Where(function => function.LambdaSource is not null
            && function.Parameters.Length == 1));
        var body = invoker.Body!;
        var evaluate = Assert.Single(body.Statements.OfType<FirEvaluate>(), statement =>
            statement.Value is FirIndirectCall);
        var call = Assert.IsType<FirIndirectCall>(evaluate.Value);
        var ioException = Assert.Single(program.Analysis.Types.Types, type =>
            type.Declaration is { ModuleName: "std.stdio", Name: "IOException" });
        var original = call.Function.Type;
        var forgedType = program.Analysis.Types.GetFunction(original.Arguments.Take(original.ParameterModes.Length),
            original.Arguments[^1], original.ParameterModes, [ioException]);
        var forgedCall = call with { Function = call.Function with { Type = forgedType } };
        int statementIndex = body.Statements.IndexOf(evaluate);
        var forgedInvoker = invoker with
        {
            Body = body with
            {
                Statements = body.Statements.SetItem(statementIndex, evaluate with { Value = forgedCall }),
            },
        };

        var verification = FirVerifier.Verify(program with
        {
            Functions = program.Functions.SetItem(forgedInvoker.Id, forgedInvoker),
        }, TestContext.Current.CancellationToken);

        Assert.Null(verification.Program);
        Assert.Contains(verification.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("checked effect budget", System.StringComparison.Ordinal));
    }

    private static FirCompilation CompileWithUnusedCapturingGeneric(out AstLambda capturedLambda)
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val callback: () -> int = { -> 1 }; val result = callback()\n} }\n"
            + "func <T> make(seed: int, ignored: T): () -> int = { -> seed }");
        var lambdas = source.Root.Descendants().OfType<AstLambda>().ToArray();
        Assert.Equal(2, lambdas.Length);
        capturedLambda = Assert.Single(lambdas, lambda => lambda.Descendants().OfType<AstAtom>()
            .Any(atom => atom.Spelling == "seed"));
        return FirCompilation.Create([source], TestContext.Current.CancellationToken);
    }

    private static AstLambda SingleLambda(AstSnapshot source) =>
        Assert.Single(source.Root.Descendants().OfType<AstLambda>());
}
