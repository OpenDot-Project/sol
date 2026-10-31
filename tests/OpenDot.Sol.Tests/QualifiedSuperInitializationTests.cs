// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class QualifiedSuperInitializationTests
{
    private const string Interface = "interface I { func read(): int = 1; val item: int { get = 1 }; "
        + "this[index: int]: int { get = index }; func self(): I = this }\n";

    [Theory]
    [InlineData("class C: I { constructor() { super<I>.read() } }")]
    [InlineData("class C(): I { val stored: int = super<I>.read() }")]
    [InlineData("class C: I { val stored: int = super<I>.item }")]
    [InlineData("class C(): I { val stored: int = super<I>[0] }")]
    [InlineData("class C(): I { init { super<I>.read() } }")]
    [InlineData("class C: I { init { super<I>.read() } }")]
    [InlineData("class C: I { val stored: int by super<I>.read() }")]
    [InlineData("class C: I by super<I>.self() {}")]
    public void ConstructionCannotUseAQualifiedInterfaceReceiver(string declaration)
    {
        var analysis = GenericMemberProofTestSource.Analyze(Interface + declaration);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2238");
        Assert.False(analysis.IsComplete);
    }

    [Theory]
    [InlineData("class C: I { func run(): int { val local: int = super<I>.read(); local } }")]
    [InlineData("class C: I { val computed: int { get = super<I>.item } }")]
    [InlineData("class C: I { func run(): int = super<I>[0] }")]
    public void RuntimeBodiesRetainQualifiedInterfaceCalls(string declaration)
    {
        var analysis = GenericMemberProofTestSource.Analyze(Interface + declaration);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }
}
