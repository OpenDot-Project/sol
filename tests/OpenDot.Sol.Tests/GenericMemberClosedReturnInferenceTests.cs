// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberClosedReturnInferenceTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Theory]
    [InlineData("Root<Widget>")]
    [InlineData("Leaf<Widget>")]
    public void ClosedReceiverRecomputesInferredResultBeforeCheckingCaller(string receiver)
    {
        var analysis = Analyze("class Widget: Object {}\n"
            + "open class Root<T: Object> {\n"
            + " func choose(value: Object): int = 1\n"
            + " func choose(value: Widget): String = \"closed\"\n"
            + " func resolve(value: T) = choose(value)\n}\n"
            + "class Leaf<T: Object>: Root<T> {}\n"
            + "func use(owner: " + receiver + ", value: Widget): String = owner.resolve(value)");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "resolve");
        Assert.Equal("std.core.String", call.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void ClosedResultParticipatesInAnEnclosingOverloadSelection()
    {
        var analysis = Analyze("class Widget: Object {}\n"
            + "class Root<T: Object> {\n"
            + " func choose(value: Object): int = 1\n"
            + " func choose(value: Widget): String = \"closed\"\n"
            + " func resolve(value: T) = choose(value)\n}\n"
            + "func consume(value: int): int = value\n"
            + "func consume(value: String): String = value\n"
            + "func use(owner: Root<Widget>, value: Widget): String = consume(owner.resolve(value))");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "consume");
        Assert.Equal("std.core.String", call.ReturnType!.Name);
        Assert.Equal("std.core.String", Assert.Single(call.ParameterTypes).Name);
    }

    [Fact]
    public void NestedInferredMembersUseTheirOwnClosedBodies()
    {
        var analysis = Analyze("class Widget: Object {}\n"
            + "class Root<T: Object> {\n"
            + " func choose(value: Object): int = 1\n"
            + " func choose(value: Widget): String = \"closed\"\n"
            + " func inner(value: T) = choose(value)\n"
            + " func outer(value: T) = inner(value)\n}\n"
            + "func use(owner: Root<Widget>, value: Widget): String = owner.outer(value)");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "outer");
        Assert.Equal("std.core.String", call.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void MethodInferenceUsesTheClosedOwnerReturnTemplate()
    {
        var analysis = Analyze("class Widget: Object {}\n"
            + "class Root<T: Object> {\n"
            + " func choose(value: Object): int = 1\n"
            + " func choose(value: Widget): String = \"closed\"\n"
            + " func <U> resolve(value: T, tag: U) = choose(value)\n}\n"
            + "func use(owner: Root<Widget>, value: Widget): String = owner.resolve(value, 7)");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "resolve");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("std.core.String", call.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void ExpectedResultCanStillInferAnOpenMethodParameter()
    {
        var analysis = Analyze("class Root<T> { func <U> echo(value: U?) = value }\n"
            + "func use(owner: Root<int>): String? = owner.echo(null)");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "echo");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.True(call.ReturnType!.IsNullable);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void ReturnTemplateRetainsDeclaredMethodBoundsUntilArgumentsAreSolved()
    {
        var analysis = Analyze("class Widget: Object {}\n"
            + "class Root<T: Object> { func <U> echo(value: U?) where U: T = value }\n"
            + "func use(owner: Root<Object>, value: Widget): Widget? = owner.echo(value)");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "echo");
        Assert.Equal("app.Widget", Assert.Single(call.TypeArguments).Name);
        Assert.True(call.ReturnType!.IsNullable);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    private static BodyAnalysis Analyze(string declarations)
    {
        var session = AstTestSources.Session("module app\n" + declarations, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }
}
