// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaCaptureFirBoundaryTests
{
    [Fact]
    public void NestedCaptureChainIsRejectedAtTheContainingLambdaOrigin()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val seed = 41\n"
            + "val callback: () -> () -> int = { -> { -> seed } }\n"
            + "val nested = callback()\n"
            + "val result = nested()\n} }");
        var lambdas = source.Root.Descendants().OfType<AstLambda>().OrderBy(lambda => lambda.Origin.Span.Start).ToArray();
        Assert.Equal(2, lambdas.Length);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambdas[0].Origin.Span);
    }

    [Fact]
    public void LocalFunctionCapturedAsAValueIsRejectedAtTheLambdaOrigin()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "func local(): int = 1\n"
            + "val callback: () -> int = { local() }\n"
            + "val result = callback()\n} }");
        var lambda = Assert.Single(source.Root.Descendants().OfType<AstLambda>());

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambda.Origin.Span);
    }

    [Fact]
    public void CapturingOutParameterRemainsRejectedAtItsSourceLocation()
    {
        var source = AstTestSources.Parse("func capture(out value: int): () -> () = { value = 1 }");
        var lambda = Assert.Single(source.Root.Descendants().OfType<AstLambda>());

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken, library: true);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2262"
            && diagnostic.Span.Start >= lambda.Origin.Span.Start
            && diagnostic.Span.End <= lambda.Origin.Span.End);
    }
}
