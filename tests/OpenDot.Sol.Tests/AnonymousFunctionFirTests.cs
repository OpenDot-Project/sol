// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionFirTests
{
    private static FirCompilation Compile(string body)
    {
        var source = AstTestSources.Parse("import std.stdio\nobject Main { func main(args: []String) throws IOException { "
            + body + " } }");
        return FirCompilation.Create([source], TestContext.Current.CancellationToken);
    }

    [Fact]
    public void NoncapturingAnonymousFunctionLowersToAVerifiedFunctionPointer()
    {
        var compilation = Compile("val callback = func(value: int): int { value + 1 }; println(callback(41))");
        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program!;
        var function = Assert.Single(program.Program.Functions.Where(candidate =>
            candidate.AnonymousFunctionSource is not null));

        Assert.Equal("<anonymous-function>", function.Name);
        Assert.Equal("value", Assert.Single(function.Parameters).Name);
        Assert.Equal("int", function.ReturnType.Name);
        Assert.NotNull(function.Body);
        Assert.NotEmpty(LoweringTrace.Inspect(program, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CapturingAnonymousFunctionIsRejectedAtItsSourceExpression()
    {
        const string sourceText = "object Main { func main(args: []String) throws IOException { val seed = 42; "
            + "val callback = func(): int { seed }; println(callback()) } }";
        var source = AstTestSources.Parse("import std.stdio\n" + sourceText);
        var function = Assert.Single(source.Nodes.OfType<OpenDot.Sol.Ast.AstAnonymousFunction>());

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == function.Origin.Span);
    }

    [Fact]
    public void ContextualFunctionTypeSuppliesAnInferredReturnTypeBeforeLowering()
    {
        var compilation = Compile("val callback: (int) -> int = func(value: int) { value + 1 }; "
            + "println(callback(41))");

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var function = Assert.Single(compilation.Program!.Program.Functions.Where(candidate =>
            candidate.AnonymousFunctionSource is not null));
        Assert.Equal("int", function.ReturnType.Name);
    }

    [Fact]
    public void ExpressionBodyAnonymousFunctionPreservesItsSourceReturn()
    {
        var compilation = Compile("val callback = func(value: int): int = value + 1; println(callback(41))");

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var function = Assert.Single(compilation.Program!.Program.Functions.Where(candidate =>
            candidate.AnonymousFunctionSource is not null));
        Assert.Single(function.Body!.Statements.OfType<OpenDot.Sol.Fir.FirReturn>());
    }

    [Fact]
    public void OutParameterWritesRemainVerifiableInTheLiftedFunction()
    {
        var compilation = Compile("var answer: int; val assign = func(out value: int): () { value = 42 }; "
            + "assign(out answer); println(answer)");

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var function = Assert.Single(compilation.Program!.Program.Functions.Where(candidate =>
            candidate.AnonymousFunctionSource is not null));
        Assert.Equal(AstPassingMode.Out, Assert.Single(function.Parameters).Mode);
        Assert.Empty(function.ReturnType.Arguments);
    }
}
