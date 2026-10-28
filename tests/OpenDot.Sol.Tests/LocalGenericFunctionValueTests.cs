// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LocalGenericFunctionValueTests
{
    private static (SourceAnalysis Sources, BodyAnalysis Body) Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return (sources, BodyAnalysis.Create(sources, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExpectedFunctionTypeClosesLocalGenericFunctionParametersAndResult()
    {
        var (sources, body) = Analyze("func use(): int { func <T> identity(value: T): T = value; "
            + "val callback: (int) -> int = identity; callback(42) }");
        var use = Assert.Single(sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>(), function => function.Name == "use");
        var summary = body.GetBody(use)!;
        var value = Assert.Single(body.Expressions, expression => expression.Expression is AstAtom
            { Spelling: "identity" });

        Assert.False(body.HasErrors, string.Join('\n', body.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal("int", summary.ReturnType!.Name);
        Assert.Equal(ResolvedTypeKind.Function, value.Type!.Kind);
        Assert.Equal("int", value.Type.Arguments[0].Name);
        Assert.Equal("int", value.Type.Arguments[^1].Name);
        Assert.Equal(BodyBindingStatus.Bound, value.Status);
    }

    [Fact]
    public void FunctionValueKeepsFixedArityInsteadOfLocalDeclarationDefaults()
    {
        var (_, body) = Analyze("func use(): int { func <T> choose(value: T, fallback: T = value): T = value; "
            + "val callback: (int, int) -> int = choose; callback(20, 22) }");
        var functionValue = Assert.Single(body.Expressions, expression => expression.Expression is AstAtom
            { Spelling: "choose" });

        Assert.False(body.HasErrors, string.Join('\n', body.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal(ResolvedTypeKind.Function, functionValue.Type!.Kind);
        Assert.Equal(3, functionValue.Type.Arguments.Length);
        Assert.All(functionValue.Type.Arguments, argument => Assert.Equal("int", argument.Name));
    }

    [Fact]
    public void LocalGenericFunctionValueChecksItsDeclaredConstraint()
    {
        var (_, body) = Analyze("interface Marker {}\nclass Marked: Marker {}\n"
            + "func use(): Marked { func <T: Marker> identity(value: T): T = value; "
            + "val callback: (Marked) -> Marked = identity; callback(Marked()) }");

        Assert.False(body.HasErrors, string.Join('\n', body.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
    }

    [Fact]
    public void IncompatibleExpectedFunctionTypeReportsATypeError()
    {
        var (_, body) = Analyze("class Text {}\nfunc use() { func <T> identity(value: T): T = value; "
            + "val callback: (int) -> Text = identity }");

        Assert.Contains(body.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    [Fact]
    public void GenericLocalFunctionValueRequiresAnExpectedFunctionType()
    {
        var (_, body) = Analyze("func use() { func <T> identity(value: T): T = value; "
            + "val callback = identity }");

        Assert.False(body.Diagnostics.Any(diagnostic => diagnostic.Code == "SOL2206"));
        Assert.Contains(body.Expressions, expression => expression.Expression is AstAtom
            { Spelling: "identity" } && expression.Status == BodyBindingStatus.Deferred);
    }
}
