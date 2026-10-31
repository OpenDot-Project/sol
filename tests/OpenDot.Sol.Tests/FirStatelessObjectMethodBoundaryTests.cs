// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirStatelessObjectMethodBoundaryTests
{
    [Fact]
    public void StatelessSingletonMethodCanBeCalledDirectly()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("object Utility { func value(): int = 42 }\n"
            + "object Main { func main(args: []String): () { val result = Utility.value() } }")],
            TestContext.Current.CancellationToken);

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        Assert.Contains(compilation.Program.Program.Functions,
            function => function.Name == "value" && function.Body is not null);
    }

    [Fact]
    public void StatefulSingletonMethodCannotEnterReceiverFreeFir()
    {
        AssertRejected("object Utility { val seed = 41; func value(): int = 42 }\n"
            + "object Main { func main(args: []String): () { val result = Utility.value() } }");
    }

    [Fact]
    public void VirtualSingletonMethodCannotEnterReceiverFreeFir()
    {
        AssertRejected("object Utility { open func value(): int = 42 }\n"
            + "object Main { func main(args: []String): () { val result = Utility.value() } }");
    }

    [Fact]
    public void StateAddedInAnotherPartialObjectPartClosesTheReceiverFreeBoundary()
    {
        var declarations = new[]
        {
            AstTestSources.Parse("partial object Utility { func value(): int = 42 }"),
            AstTestSources.Parse("partial object Utility { val seed = 41 }"),
            AstTestSources.Parse("object Main { func main(args: []String): () { val result = Utility.value() } }"),
        };
        var compilation = FirCompilation.Create(declarations, TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    private static void AssertRejected(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }
}
