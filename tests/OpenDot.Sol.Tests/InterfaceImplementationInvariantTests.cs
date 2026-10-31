// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class InterfaceImplementationInvariantTests
{
    [Theory]
    [InlineData("interface I { this[index: int]: int { get = index }; this[index: String]: int { get = 2 } }\nclass C: I {}\nfunc use(c: C): int = c[1]")]
    [InlineData("interface I { this[index: int]: int { get = index }; this[index: long]: int { get = 2 } }\nclass C: I {}\nfunc use(c: C): int = c[1]")]
    [InlineData("interface I { this[index: long]: int { get = 1 } }\nclass C: I {}\nfunc use(c: C): int = c[2147483648]")]
    [InlineData("interface I { this[index: int = 2]: int { get = index } }\nclass C: I {}\nfunc use(c: C): int = c[]")]
    [InlineData("interface I { this[index: int, other: int]: int { get = index + other } }\nclass C: I {}\nfunc use(c: C): int = c[other = 1, index = 2]")]
    [InlineData("interface I { this[out index: int]: int { get { index = 1; 2 } } }\nclass C: I {}\nfunc use(c: C): int { val result = c[out var index]; result + index }")]
    [InlineData("interface I { this[ref index: int]: int { get { index += 1; index } } }\nclass C: I {}\nfunc use(c: C): int { var index = 1; c[ref index] }")]
    [InlineData("interface I { func f(): int }\nopen class Wrapper(delegate: I): I by delegate {}\nclass C(delegate: I): Wrapper(delegate) {}\nfunc use(c: C): int = c.f()")]
    [InlineData("interface I<T> { func f(value: T): T }\nopen class Wrapper<T>(delegate: I<T>): I<T> by delegate {}\nclass C(delegate: I<int>): Wrapper<int>(delegate) {}\nfunc use(c: C): int = c.f(1)")]
    [InlineData("interface I { func f(): int = 1 }\ninterface J: I { override func f(): int = 2 }\nclass C: I, J {}\nfunc use(c: C): int = c.f()")]
    [InlineData("interface I { func f(): int = 1 }\npartial class C: I {}\npartial class C { override func f(): int = 2 }\nfunc use(c: C): int = c.f()")]
    [InlineData("interface I<T> { func f(value: T) = value }\nclass C: I<int> {}\nfunc use(c: C): int = c.f(1)")]
    [InlineData("interface I { func f() = this.g(); func g(): int }\nclass C: I { override func g(): int = 1 }\nfunc use(c: C): int = c.f()")]
    [InlineData("interface I { func f(): int }\nclass C: I { override func f() = helper(); func helper(): int = 1 }\nfunc use(c: C): int = c.f()")]
    [InlineData("interface I<T> { func f(value: T): T = value }\nclass C: I<int> { override func f(value: int): int = super<I<int>>.f(value) }")]
    [InlineData("interface I { val item: int }\nclass C(override val item: int): I {}\nfunc use(c: C): int = c.item")]
    [InlineData("interface I { var item: int { get; set } }\nclass C(override var item: int): I {}\nfunc use(c: C): int { c.item = 2; c.item }")]
    [InlineData("interface I { var item: int { get = 1; private set {} } }\nclass C: I {}\nfunc use(c: C): int = c.item")]
    [InlineData("interface I { val item: int { protected get = 1 } }\nclass C: I { func read(): int = super<I>.item }")]
    [InlineData("interface I { val item: int }\nclass C: I { override val item: int = 1 }\nfunc use(c: C): int = c.item")]
    [InlineData("interface I { this[callback: () -> int]: int { get = callback() } }\nclass C: I {}\nfunc use(c: C): int = c[{ -> 1 }]")]
    [InlineData("interface I { this[vararg indices: int]: int { get = indices.length } }\nclass C: I {}\nfunc use(c: C): int = c[1, 2]")]
    [InlineData("interface I { this[index: int]: int { get } }\nfunc <T: I> use(c: T): int = c[1]")]
    public void ConstructedOperationsKeepTheirSourceSemantics(string source)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
    }

    [Theory]
    [InlineData("interface I { func f(): int }\nclass C: I { func f(): int = 1 }\nfunc use(c: C): int = c.f()", "SOL2231")]
    [InlineData("interface I { this[out index: int]: int { get = 1 } }", "SOL2242")]
    [InlineData("interface I { this[ref index: int]: int { get = 1 } }\nclass C: I {}\nfunc use(c: C): int = c[ref 1]", "SOL2221")]
    [InlineData("interface I { this[ref item: String]: int { get = 1 } }\nclass C: I {}\nfunc use(c: C): int { var item: String? = \"text\"; if item != null { return c[ref item] }; 0 }", "SOL2221")]
    [InlineData("interface I<T> { func f(): int = 1 }\nclass C: I<int> { override func f(): int = super<I<String>>.f() }", "SOL2238")]
    [InlineData("interface I { var item: int { get = 1; set {} } }\nclass C: I { override var item: int { get = super<I>.item; set { super<I>.item = value } } }", null)]
    [InlineData("interface I { this[index: int]: int { get } }\nclass C: I { override this[index: int]: int { get = super<I>[index] } }", "SOL2238")]
    [InlineData("interface I { val item: int }\nclass C(val item: int): I {}", "SOL2231")]
    [InlineData("interface I {}\nclass C: I { func wrong(): I = super<I> }", "SOL2238")]
    [InlineData("interface I { val item: int { private get = 1 } }\nclass C: I { func read(): int = super<I>.item }", "SOL2214")]
    [InlineData("interface I { var item: int { get = 1; private set {} } }\nclass C: I {}\nfunc write(c: C) { c.item = 1 }", "SOL2214")]
    [InlineData("class Failure: Exception {}\ninterface I { func f(): int }\nfunc make(value: I): I throws Failure = value\nclass C(delegate: I): I by make(delegate) { override func f(): int = 1 }", "SOL2250")]
    [InlineData("interface A { func f(): int }\ninterface B { func f(): int = 1 }\nclass C(delegate: A): A by delegate, B {}", "SOL2236")]
    [InlineData("open class Base { func f(): int = 1 }\ninterface I { func f(): int = 2 }\nclass C: Base, I { override func f(): int = 3 }", "SOL2232")]
    public void InvalidOperationsNeverBecomeSuccessfulProofs(string source, string? code)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        if (code is null) { Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message))); return; }
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void SelectionSnapshotRejectsForeignTypes()
    {
        const string source = "interface I { func f(): int = 1 }\nclass C: I {}";
        var first = GenericMemberProofTestSource.Analyze(source);
        var second = GenericMemberProofTestSource.Analyze(source);
        var record = first.InterfaceImplementations.Values.SelectMany(records => records).First(record => record.Receiver.Declaration!.Name == "C");
        Assert.NotNull(first.GetInterfaceImplementations(record.Receiver));
        Assert.Throws<ArgumentException>(() => second.GetInterfaceImplementations(record.Receiver));
        Assert.Single(record.Contracts);
        Assert.Equal("I", record.Implementation!.Member.DeclaringType.Declaration!.Name);
    }

    [Fact]
    public void DelegateTargetIdentityAndInvocationEffectsRemainSeparate()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: Exception {}\ninterface I { func f(): int }\n"
            + "class Target: I { override func f(): int = 1 }\nfunc make(value: Target): Target throws Failure = value\n"
            + "class Wrapper(delegate: Target) throws Failure: I by make(delegate) {}\nfunc use(c: Wrapper): int = c.f()");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var implementation = analysis.InterfaceImplementations.Values.SelectMany(records => records)
            .Single(record => record.Receiver.Declaration!.Name == "Wrapper").Implementation!;
        Assert.Equal("Target", implementation.DelegateTargetType!.Declaration!.Name);
        Assert.Equal("Wrapper", implementation.DelegateOwner!.Declaration!.Name);
        Assert.Contains(implementation.InitializationEffects, type => type.Name == "app.Failure");
        Assert.Empty(implementation.Effects);
        Assert.Empty(analysis.Bodies.Single(body => body.Declaration.Name == "use").EscapingEffects);
    }

    [Fact]
    public void DeclarationOrderDoesNotChooseBetweenIndependentProviders()
    {
        foreach (string bases in new[] { "Left, Right", "Right, Left" })
        {
            var analysis = GenericMemberProofTestSource.Analyze("interface Left { func f(): int = 1 }\n"
                + "interface Right { func f(): int = 2 }\nclass C: " + bases + " {}");
            var record = analysis.InterfaceImplementations.Values.SelectMany(records => records)
                .Single(record => record.Receiver.Declaration!.Name == "C");
            Assert.Equal(BodyBindingStatus.Invalid, record.Status);
            Assert.Null(record.Implementation);
            Assert.Equal(2, record.Contracts.Length);
        }
    }
}
