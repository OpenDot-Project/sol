// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class InterfaceImplementationScopeTests
{
    [Fact]
    public void ConstructorUseChecksTheClosedDelegateInitializationEffects()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class A: Exception {}\nclass B: Exception {}\ninterface I { func read(): int }\n"
            + "func make(value: Object, target: I): I throws A = target\nfunc make(value: String, target: I): I throws B = target\n"
            + "class C<T: Object>(value: T, target: I) throws A: I by make(value, target) {}\n"
            + "func use(value: String, target: I): C<String> throws A = C<String>(value, target)");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");
        Assert.Equal(BodyBindingStatus.Invalid, analysis.Bodies.Single(body => body.Declaration.Name == "use").Status);
    }
    [Fact]
    public void AnInferredOverrideRecomputesItsReturnForTheConstructedOwner()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Widget: Object {}\ninterface I { func resolve(): Object }\n"
            + "class C<T: Object>(val stored: T): I { func choose(value: Object): int = 1; "
            + "func choose(value: Widget): String = \"closed\"; override func resolve() = choose(this.stored) }\n"
            + "func use(value: C<Widget>): Object = value.resolve()");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = analysis.Calls.Single(call => call.Target.Name == "resolve");
        var specialized = analysis.GetGenericMemberBody((AstFunction)call.Target, call.Receiver!, [], TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, specialized.Summary.Status);
        Assert.Equal("std.core.String", specialized.Summary.ReturnType!.Name);
    }
    [Fact]
    public void OutConstructorParametersAreNotInitializedDelegateValues()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I { func read(): int }\n"
            + "class C(out delegate: I): I by delegate {}");
        Assert.True(analysis.HasErrors);
        var selection = analysis.InterfaceImplementations.Values.SelectMany(records => records)
            .Single(record => record.Receiver.Declaration!.Name == "C");
        Assert.NotEqual(BodyBindingStatus.Bound, selection.Status);
    }
    [Theory]
    [InlineData(": U")]
    [InlineData("")]
    public void ClosedMethodArgumentsDoNotRewriteTheGenericImplementationTemplate(string annotation)
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I<T> { func <U> map(value: U): U; func <U> identity(value: U): U }\n"
            + "class C<T>: I<T> { override func <U> map(value: U)" + annotation + " = this.identity<U>(value); "
            + "override func <U> identity(value: U): U = value }\nfunc use(c: C<int>): String = c.map<String>(\"ok\")");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = analysis.Calls.Single(call => call.Target.Name == "map");
        var specialized = analysis.GetGenericMemberBody((AstFunction)call.Target, call.Receiver!, call.TypeArguments,
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, specialized.Summary.Status);
        Assert.Equal("std.core.String", specialized.Summary.ReturnType!.Name);
    }

    [Fact]
    public void AnotherInstanceOfTheCurrentOwnerKeepsItsOwnDelegateTarget()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I<T> { func read(value: T): T }\n"
            + "class Wrapper<T>(delegate: I<T>): I<T> by delegate { func other(value: Wrapper<String>): String = value.read(\"ok\") }\n"
            + "func use(value: Wrapper<int>, other: Wrapper<String>): String = value.other(other)");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = analysis.Calls.Single(call => call.Target.Name == "other");
        var specialized = analysis.GetGenericMemberBody((AstFunction)call.Target, call.Receiver!, [], TestContext.Current.CancellationToken);
        Assert.Empty(specialized.Diagnostics);
        var read = Assert.Single(specialized.Calls);
        Assert.Equal("std.core.String", Assert.Single(read.ParameterTypes).Name);
        Assert.Equal("std.core.String", Assert.Single(read.InterfaceImplementation!.Implementation!.DelegateTargetType!.Arguments).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PropertyProtocolEffectsParticipateInInterfaceValidationAndInvocation(bool allowed)
    {
        var session = AstTestSources.Session("module app\nimport std.reflect\nclass Failure: Exception {}\n"
            + "class Provider() { operator func getValue(owner: Object?, property: SolProperty<int>): int throws Failure = 1 }\n"
            + "interface I { val item: int { get" + (allowed ? " throws Failure" : "") + " } }\n"
            + "class C: I { override val item: int by Provider() }\nfunc read(value: C): int throws Failure = value.item",
            "module std.reflect\nclass SolProperty<T> {}", GenericMemberProofTestSource.Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        if (!allowed) { Assert.Contains(analysis.Diagnostics, d => d.Code == "SOL2235"); return; }
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Contains(analysis.Bodies.Single(body => body.Declaration.Name == "read").EscapingEffects,
            type => type.Name == "app.Failure");
    }

    [Fact]
    public void AProtocolOnAnotherInstanceDoesNotUseThePropertyOwnersTypeArguments()
    {
        var session = AstTestSources.Session("module app\nimport std.reflect\ninterface I { val item: int }\n"
            + "class C<T>(val stored: T): I { override val item: int by C<String>(\"text\"); "
            + "operator func getValue(owner: Object?, property: SolProperty<T>): T = this.stored }",
            "module std.reflect\nclass SolProperty<T> {}", GenericMemberProofTestSource.Core);
        var analysis = BodyAnalysis.Create(SourceAnalysis.Create(session, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Contains(analysis.Diagnostics, d => d.Code == "SOL2280");
        Assert.False(analysis.IsComplete);
    }
}
