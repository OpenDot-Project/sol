// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionFirBoundaryTests
{
    [Theory]
    [InlineData("local capture", 0, "object Main { func main(args: []String) { val seed = 41; "
        + "val callback: () -> int = func(): int { seed }; val result = callback() } }")]
    [InlineData("nested capturing closure", 1, "object Main { func main(args: []String) { "
        + "val outer: (int) -> (() -> int) = func(seed: int): () -> int { "
        + "val inner = func(): int { seed }; inner }; val callback = outer(41); val result = callback() } }")]
    [InlineData("generic enclosing function", 0, "func <T> make(ignored: T): () -> int { "
        + "val callback = func(): int { 1 }; callback } "
        + "object Main { func main(args: []String) { val callback = make<int>(0); val value = callback() } }")]
    [InlineData("generic enclosing type", 0, "class Holder<T> { func make(): () -> int { "
        + "val callback = func(): int { 1 }; callback } } "
        + "object Main { func main(args: []String) {} }")]
    [InlineData("explicit bound receiver", 0, "class Counter(val count: int) { func make(): () -> int { "
        + "val callback = func(): int { this.count }; callback } } "
        + "object Main { func main(args: []String) {} }")]
    [InlineData("implicit receiver member", 0, "class Counter(val count: int) { func make(): () -> int { "
        + "val callback = func(): int { count }; callback } } "
        + "object Main { func main(args: []String) {} }")]
    public void UnsupportedAnonymousFunctionFirBoundariesAreReportedAtTheirSource(
        string boundary, int expectedFunctionIndex, string text)
    {
        var source = AstTestSources.Parse(text);
        var functions = source.Root.Descendants().OfType<AstAnonymousFunction>()
            .OrderBy(function => function.Origin.Span.Start).ToArray();
        Assert.NotEmpty(functions);
        var target = functions[expectedFunctionIndex];

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is null, $"The {boundary} boundary unexpectedly entered FIR.");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == target.Origin.Span);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void VarargAnonymousFunctionUsesOneArrayValuedFirParameter()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) { "
            + "val callback: ([]int) -> int = func(vararg values: int): int { values[0] }; "
            + "val result = callback([42]) } }");

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.NotNull(compilation.Program);
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var function = Assert.Single(program.Functions
            .Where(candidate => candidate.AnonymousFunctionSource is not null));
        var parameter = Assert.Single(function.Parameters);
        Assert.Equal(AstPassingMode.Value, parameter.Mode);
        Assert.Equal(ResolvedTypeKind.Array, parameter.Type.Kind);
        Assert.Equal("int", parameter.Type.Arguments[0].Name);
        var main = program.Functions.Single(candidate => candidate.Name == "main");
        var call = Assert.IsType<FirIndirectCall>(Assert.Single(main.Body!.Statements.OfType<FirDeclare>(),
            declaration => declaration.Initializer is FirIndirectCall).Initializer);
        Assert.Equal(parameter.Type, call.Function.Type.Arguments[0]);
    }

    [Fact]
    public void NonescapingRefLikeCaptureIsRejectedByTheCurrentFirBoundary()
    {
        var source = AstTestSources.Parse("ref struct View {}\n"
            + "func use(value: View) { val callback = func(): View { value }; val result = callback() }\n"
            + "object Main { func main(args: []String) {} }");
        var function = Assert.Single(source.Root.Descendants().OfType<AstAnonymousFunction>());

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == function.Origin.Span);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }
}
