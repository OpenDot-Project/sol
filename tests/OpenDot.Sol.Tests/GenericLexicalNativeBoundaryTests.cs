// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalNativeBoundaryTests
{
    [Fact]
    public void BoundLocalGenericSourceDoesNotInventANativeClosureAbi()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "func <T> local(value: T): T = value\nval result = local<int>(42)\n} }");
        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Fact]
    public void CapturedGenericLocalSourceStillRequiresIndependentFirAdmission()
    {
        var source = AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val seed = 42\nfunc <T> local(value: T): int = seed\n"
            + "val result = local<int>(1)\n} }");
        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void GenericOwnerWithBoundLocalProofRemainsOutsideNativeOwnerLayouts()
    {
        var source = AstTestSources.Parse("class Root<T> { func run(value: T): T {\n"
            + "func <U> local(item: U): T = value\nlocal<int>(1)\n} }\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)\n"
            + "object Main { func main(args: []String) {} }");
        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    [Fact]
    public void NativeRejectionDoesNotRetargetTheSelectedLocalSourceFunction()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func local(value: int): int = 99\n"
            + "class Root<T> { func run(value: T): T {\n"
            + "func <U> local(item: U): T = value\nlocal<int>(1)\n} }\n"
            + "func use(owner: Root<int>, value: int): int = owner.run(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.NotNull(local.Binding.LexicalContext);
        Assert.Single(local.Binding.TypeArguments);
        Assert.Same(local.Binding.Target, local.SpecializedBody!.Declaration);
        Assert.NotEqual(analysis.Sources.Declarations.Modules["app"].Declarations
            .Single(symbol => symbol.Name == "local").Parts[0], local.Binding.Target);
    }
}
