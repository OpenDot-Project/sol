// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NestedAnonymousFunctionFirTests
{
    [Fact]
    public void NoncapturingNestedFunctionsAreLiftedAsSeparateVerifiedTargets()
    {
        var source = AstTestSources.Parse("import std.stdio\nobject Main { func main(args: []String) throws IOException { "
            + "val outer = func(value: int): int { val increment = func(input: int): int = input + 1; "
            + "increment(value) }; println(outer(41)) } }");

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var sourceFunctions = source.Nodes.OfType<AstAnonymousFunction>()
            .OrderBy(function => function.Origin.Span.Start).ToArray();
        var liftedFunctions = program.Functions.Where(function => function.AnonymousFunctionSource is not null)
            .OrderBy(function => function.AnonymousFunctionSource!.Origin.Span.Start).ToArray();

        Assert.Equal(2, sourceFunctions.Length);
        Assert.Equal(sourceFunctions, liftedFunctions.Select(function => function.AnonymousFunctionSource));
        Assert.All(liftedFunctions, function =>
        {
            Assert.NotNull(function.Body);
            Assert.Equal("<anonymous-function>", function.Name);
            Assert.Empty(program.Analysis.Sources.Lexical.GetAnonymousFunctionCaptures(function.AnonymousFunctionSource!));
            Assert.Empty(program.Analysis.Sources.Lexical.GetAnonymousFunctionReceiverCaptures(function.AnonymousFunctionSource!));
        });
    }

    [Fact]
    public void NoncapturingAnonymousFunctionInsideLambdaIsLiftedIndependently()
    {
        var source = AstTestSources.Parse("import std.stdio\nobject Main { func main(args: []String) throws IOException { "
            + "val transform: (int) -> int = { value -> val increment = func(input: int): int = input + 1; "
            + "increment(value) }; println(transform(41)) } }");

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        Assert.Single(program.Functions.Where(function => function.LambdaSource is not null));
        Assert.Single(program.Functions.Where(function => function.AnonymousFunctionSource is not null));
    }

    [Fact]
    public void NoncapturingLambdaInsideAnonymousFunctionIsLiftedIndependently()
    {
        var source = AstTestSources.Parse("import std.stdio\nobject Main { func main(args: []String) throws IOException { "
            + "val outer = func(value: int): int { val increment: (int) -> int = { item -> item + 1 }; "
            + "increment(value) }; println(outer(41)) } }");

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        Assert.Single(program.Functions.Where(function => function.LambdaSource is not null));
        Assert.Single(program.Functions.Where(function => function.AnonymousFunctionSource is not null));
    }
}
