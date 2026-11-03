// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NativeCallableSymbolsTests
{
    [Fact]
    public void SourceFilePathsAndDeclarationOrderDoNotRenameCallableSymbols()
    {
        var first = AstTestSources.Parse("func run(value: int): int { val f: () -> int = { 3 }; f() }", "example", "first.sol");
        var second = AstTestSources.Parse("func run(value: bool): int { val f: () -> int = { 4 }; f() }", "example", "second.sol");
        var renamed = AstTestSources.Parse("func run(value: int): int { val f: () -> int = { 3 }; f() }", "example", "renamed.sol");
        var cancellation = TestContext.Current.CancellationToken;
        var left = FirCompilation.Create([first, second], cancellation, library: true);
        var right = FirCompilation.Create([second, renamed], cancellation, library: true);
        Assert.NotNull(left.Program);
        Assert.NotNull(right.Program);
        var leftSymbols = NativeCallableSymbols.Build(left.Program.Program, cancellation);
        var rightSymbols = NativeCallableSymbols.Build(right.Program.Program, cancellation);
        var functions = left.Program.Program.Functions.Where(function => function.Name == "run" || function.LambdaSource is not null).ToArray();
        Assert.Equal(4, functions.Length);
        Assert.Contains(functions, function => right.Program.Program.Functions.Single(other => other.Origin.Text == function.Origin.Text).Id != function.Id);
        foreach (var function in functions)
        {
            var matching = Assert.Single(right.Program.Program.Functions, other => other.Origin.Text == function.Origin.Text);
            Assert.Equal(leftSymbols[function.Id], rightSymbols[matching.Id]);
        }
    }
}
