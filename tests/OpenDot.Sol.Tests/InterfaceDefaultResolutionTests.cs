// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class InterfaceDefaultResolutionTests
{
    [Theory]
    [InlineData("interface A { func read(): int = 1 }\ninterface B: A { override func read(): int = 2 }\nclass C: B {}\nfunc use(value: C): int = value.read()")]
    [InlineData("interface A<T> { func read(value: T): T = value }\nclass C: A<int> {}\nfunc use(value: C): int = value.read(1)")]
    [InlineData("interface A { val item: int { get = 1 } }\ninterface B: A {}\ninterface D: A {}\nclass C: B, D {}\nfunc use(value: C): int = value.item")]
    public void UniqueInheritedBodiesBindWithoutConflict(string source)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
    }

    [Theory]
    [InlineData("interface A { func read(): int = 1 }\ninterface B: A { override func read(): int }\nclass C: B {}", "SOL2237")]
    [InlineData("open class A { open func read(): int = 1 }\ninterface B { func read(): int = 2 }\nclass C: A, B {}", "SOL2236")]
    [InlineData("interface A { val item: int { get = 1 } }\ninterface B { val item: int { get = 2 } }\nclass C: A, B {}", "SOL2236")]
    [InlineData("interface A { val item: int { get = 1 } }\ninterface B { var item: String { set {} } }\nclass C: A, B {}", "SOL2239")]
    [InlineData("interface A { func read(): int }\nclass C: A { override func read(): int = super<A>.read() }", "SOL2238")]
    [InlineData("interface A {}\nclass C { func read(): int = super<A>.read() }", "SOL2238")]
    public void InvalidInheritedContractsRemainDiagnosticsEvenWithoutAUse(string source, string code)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void CompetingDelegatesNeedAnExplicitOverride()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Left { func read(): int }\n"
            + "interface Right { func read(): int }\nclass Both(left: Left, right: Right): Left by left, Right by right {}");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2236");
    }

    [Fact]
    public void ExplicitOverrideCanReplaceCompetingDelegates()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Left { func read(): int }\n"
            + "interface Right { func read(): int }\nclass Both(left: Left, right: Right): Left by left, Right by right { override func read(): int = 3 }");
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2236");
    }
    [Fact]
    public void DirectQualifiedSuperSelectsTheInterfaceBody()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Reader { func read(): int = 1 }\n"
            + "class Both: Reader { override func read(): int = super<Reader>.read() }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal("Reader", Assert.Single(analysis.Calls).Receiver!.Declaration!.Name);
        Assert.True(Assert.Single(analysis.Calls).IsQualifiedInterfaceSuper);
        Assert.Equal("Reader", Assert.Single(analysis.Calls).InterfaceImplementation!.Implementation!.Owner.Declaration!.Name);
    }

    [Fact]
    public void IndirectQualifiedSuperCannotBypassTheDirectInterface()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Root { func read(): int = 1 }\n"
            + "interface Reader: Root {}\nclass Both: Reader { override func read(): int = super<Root>.read() }");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2238");
    }

    [Fact]
    public void GetterAndSetterCanComeFromDifferentInterfaces()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Read { val item: int { get = 1 } }\n"
            + "interface Write { var item: int { set {} } }\nclass Both: Read, Write {}\n"
            + "func use(value: Both): int { value.item = 2; value.item }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
    }

    [Fact]
    public void IndexerDefaultBodyIsSelectedByTheIndexType()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Read { this[index: int]: int { get = index } }\n"
            + "class Both: Read {}\nfunc use(value: Both): int = value[1]");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.DoesNotContain(analysis.UnverifiedNodes, node => node.SyntaxKind == OpenDot.Sol.Cst.NodeKind.IndexExpression);
    }

    [Fact]
    public void InterfaceDelegationSuppliesAnAbstractMethodContract()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Reader { func read(): int }\n"
            + "class Wrapper(delegate: Reader): Reader by delegate {}\nfunc use(value: Wrapper): int = value.read()");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
    }
    [Fact]
    public void IndependentUnusedDefaultsAreADeclarationError()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Left { func read(): int = 1 }\n"
            + "interface Right { func read(): int = 2 }\nclass Both: Left, Right {}");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2236");
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void SharedDiamondDefaultIsOneImplementation()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Root { func read(): int = 1 }\n"
            + "interface Left: Root {}\ninterface Right: Root {}\nclass Both: Left, Right {}\n"
            + "func use(value: Both): int = value.read()");
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2236");
        Assert.Single(analysis.Calls);
    }

    [Fact]
    public void ExplicitOverrideResolvesIndependentDefaults()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Left { func read(): int = 1 }\n"
            + "interface Right { func read(): int = 2 }\n"
            + "class Both: Left, Right { override func read(): int = 3 }\nfunc use(value: Both): int = value.read()");
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2236");
        Assert.Equal("Both", Assert.Single(analysis.Calls).Receiver!.Declaration!.Name);
    }

    [Fact]
    public void ConcreteTypeMustImplementAbstractInterfaceOperation()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Reader { func read(): int }\nclass Missing: Reader {}");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2237");
    }
}
