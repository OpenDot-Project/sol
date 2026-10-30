// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class InterfaceOperationContractTests
{
    [Theory]
    [InlineData("interface I { func f(): int }\nclass C: I { override func f() = \"bad\" }", "SOL2234")]
    [InlineData("interface I { func f(): int }\nclass C: I { private override func f(): int = 1 }", "SOL2233")]
    [InlineData("class Failure: Exception {}\ninterface I { func f(): int }\nclass C: I { override func f(): int throws Failure = 1 }", "SOL2235")]
    [InlineData("interface I { func f(callback: () -> int) }\nclass C: I { override func f(@escaping callback: () -> int) {} }", "SOL2265")]
    [InlineData("interface I { val item: int { get = 1 } }\nclass C: I { override val item: String { get = \"bad\" } }", "SOL2239")]
    [InlineData("interface I { func f() = \"text\" }\nclass C: I { override func f(): int = 1 }", "SOL2234")]
    public void ImplementationsAreCheckedAgainstTheirInterfaceContract(string source, string code)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Theory]
    [InlineData("interface I<T> { func <U> f(item: U): U = item }\nclass C: I<int> {}\nfunc use(c: C): String = c.f<String>(\"ok\")")]
    [InlineData("interface I { func f(value: int = 1): int = value }\nclass C: I {}\nfunc use(c: C): int = c.f()")]
    [InlineData("interface I<T> { val item: T { get = this.value() }; func value(): T }\nabstract class C: I<int> {}")]
    [InlineData("interface I { this[index: int]: int { get = index } }\nclass C: I { override this[index: int]: int { get = super<I>[index] } }")]
    public void GenericAndAccessorContractsBindInTheirSourceContext(string source)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
    }

    [Fact]
    public void InterfaceBodiesDoNotGrantNativeDispatchPermission()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("interface I { func f(): int = 1 }\n"
            + "class C: I {}\nfunc use(c: C): int = c.f()\nobject Main { func main(args: []String) {} }")],
            TestContext.Current.CancellationToken);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    [Fact]
    public void SetterEffectsDoNotBecomeGetterEffects()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: Exception {}\n"
            + "interface Read { val item: int { get = 1 } }\n"
            + "interface Write { var item: int { set throws Failure {} } }\nclass C: Read, Write {}\n"
            + "func read(c: C): int = c.item\nfunc write(c: C) throws Failure { c.item = 1 }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Empty(analysis.Bodies.Single(body => body.Declaration.Name == "read").EscapingEffects);
        Assert.Contains(analysis.Bodies.Single(body => body.Declaration.Name == "write").EscapingEffects,
            type => type.Name == "app.Failure");
    }
}
