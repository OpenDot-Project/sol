// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ClosedClassMemberBodyTests
{
    [Fact]
    public void StatefulObjectMemberReturnInferenceUsesTheClosedReceiverBody()
    {
        var analysis = GenericMemberProofTestSource.Analyze("object Counter { var count: int = 1;\n"
            + "func <T> identity(value: T) = value; func <T> relay(value: T) = this.identity(value);\n"
            + "func <T> read(value: T): int = this.count }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var owner = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(), []);
        var boolean = analysis.Types.GetPrimitive("bool");
        var identity = analysis.GetGenericMemberBody(GenericMemberProofTestSource.Function(analysis, "identity"),
            owner, [boolean], TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, identity.Summary.Status);
        var relay = analysis.GetGenericMemberBody(GenericMemberProofTestSource.Function(analysis, "relay"),
            owner, [boolean], TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, relay.Summary.Status);
        Assert.Empty(relay.Diagnostics); Assert.Empty(relay.UnverifiedNodes);
        Assert.Same(boolean, relay.Summary.ReturnType);
        var read = analysis.GetGenericMemberBody(GenericMemberProofTestSource.Function(analysis, "read"),
            owner, [boolean], TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, read.Summary.Status);
        Assert.Same(owner, Assert.Single(read.Expressions, binding => binding.Expression is AstMember).ValueOwner);
    }

    [Fact]
    public void NongenericClassOwnerAndGenericMethodUseASeparateClosedMemberCache()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C(seed: int) { val stored: int = seed;\n"
            + "func <T> identity(value: T): T = value; func <T> read(value: T): int = this.stored }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var definition = analysis.Sources.Declarations.Modules["app"].Declarations.Single();
        var owner = analysis.Types.Nominal(definition, []);
        var identity = GenericMemberProofTestSource.Function(analysis, "identity");
        var read = GenericMemberProofTestSource.Function(analysis, "read");
        foreach (string name in new[] { "int", "bool" })
        {
            var type = analysis.Types.GetPrimitive(name);
            var body = analysis.GetGenericMemberBody(identity, owner, [type], TestContext.Current.CancellationToken);
            Assert.Equal(BodyBindingStatus.Bound, body.Summary.Status);
            Assert.Empty(body.Diagnostics); Assert.Empty(body.UnverifiedNodes);
            Assert.Same(type, body.Summary.ReturnType); Assert.Same(owner, body.ConstructedOwner);
            Assert.Empty(body.OwnerTypeArguments);
            Assert.Same(body, analysis.GetGenericMemberBody(identity, owner, [type], TestContext.Current.CancellationToken));
            // The receiver-free API must not accidentally reuse a cached instance with a receiver.
            Assert.Throws<ArgumentException>(() => analysis.GetGenericBody(identity, [type], TestContext.Current.CancellationToken));
            var reading = analysis.GetGenericMemberBody(read, owner, [type], TestContext.Current.CancellationToken);
            Assert.Equal(BodyBindingStatus.Bound, reading.Summary.Status);
            Assert.Same(owner, Assert.Single(reading.Expressions, binding => binding.Expression is AstMember).ValueOwner);
        }
    }
}
