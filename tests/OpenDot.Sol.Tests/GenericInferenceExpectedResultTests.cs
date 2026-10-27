// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInferenceExpectedResultTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\nopen class RuntimeException: Exception {}";

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(["module app\n" + source, Core]);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ExpectedResultProjectsGenericReturnThroughItsDeclaredBase()
    {
        var analysis = Analyze("interface Parent<T> {}\n"
            + "class Projected<T>: Parent<T> {}\n"
            + "func <T> make(): Projected<T> = throw RuntimeException()\n"
            + "func use(): Parent<String> = make()");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void ExpectedResultCanUseMatchingBasePathAfterAnIncompatibleProjection()
    {
        var analysis = Analyze("interface Parent<T> {}\n"
            + "interface FixedParent: Parent<int> {}\n"
            + "class Projected<T>: FixedParent, Parent<T> {}\n"
            + "func <T> make(): Projected<T> = throw RuntimeException()\n"
            + "func use(): Parent<String> = make()");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void ExpectedResultDoesNotNarrowAGenericReturnToADerivedType()
    {
        var analysis = Analyze("interface Parent<T> {}\n"
            + "class Child<T>: Parent<T> {}\n"
            + "func <T> make(): Parent<T> = throw RuntimeException()\n"
            + "func use(): Child<String> = make()");

        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.True(analysis.HasErrors || !analysis.IsComplete);
    }
}
