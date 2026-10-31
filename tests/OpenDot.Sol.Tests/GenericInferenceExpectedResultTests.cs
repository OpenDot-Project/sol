// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text;
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
    public void ExpectedResultDoesNotChooseBetweenDifferentBaseProjectionEvidence()
    {
        var analysis = Analyze("interface Parent<T> {}\n"
            + "class Projected<T>: Parent<T>, Parent<String> {}\n"
            + "func <T> make(value: T): Projected<T> = throw RuntimeException()\n"
            + "func use(value: int): Parent<String> = make(value)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void ExpectedResultAloneDoesNotInferAcrossDifferentBaseProjectionEvidence()
    {
        var analysis = Analyze("interface Parent<T> {}\n"
            + "class Projected<T>: Parent<T>, Parent<String> {}\n"
            + "func <T> make(): Projected<T> = throw RuntimeException()\n"
            + "func use(): Parent<String> = make()");

        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }

    [Fact]
    public void ExpectedResultCanInferAnArrayTypeArgumentFromNestedEmptyResultShape()
    {
        var analysis = Analyze("func <T> make(): []T = throw RuntimeException()\n"
            + "func use(): [][]String = make()");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var argument = Assert.Single(Assert.Single(analysis.Calls, binding => binding.Target.Name == "make").TypeArguments);
        Assert.Equal(ResolvedTypeKind.Array, argument.Kind);
        Assert.Equal("std.core.String", Assert.Single(argument.Arguments).Name);
    }

    [Fact]
    public void ExpectedResultInfersFromAnUnannotatedGenericExpressionBody()
    {
        var analysis = Analyze("func <T> identityOrNull(value: T?) = value\n"
            + "func use(): String? = identityOrNull(null)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete,
            string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "identityOrNull");
        var inferred = Assert.Single(call.TypeArguments);
        Assert.Equal(ResolvedTypeKind.Nominal, inferred.Kind);
        Assert.Equal("std.core.String", inferred.Name);
        Assert.Equal(ResolvedTypeKind.Nullable, call.ReturnType!.Kind);
        Assert.Equal("std.core.String", Assert.Single(call.ReturnType.Arguments).Name);
    }

    [Fact]
    public void ExpectedResultClosesAnInferredReturnWhenTheOnlyArgumentIsDefaulted()
    {
        var analysis = Analyze("func <T> maybe(value: T? = null) = value\n"
            + "func use(): String? = maybe()");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete,
            string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "maybe");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(ResolvedTypeKind.Nullable, call.ReturnType!.Kind);
        Assert.Equal("std.core.String", Assert.Single(call.ReturnType.Arguments).Name);
        Assert.Equal(new[] { 0 }, call.DefaultParameters);
    }

    [Fact]
    public void ExpectedResultConflictsWithTheTypeInferredByAnInputArgument()
    {
        var analysis = Analyze("func <T> inferredIdentity(value: T) = value\n"
            + "func use(value: int): String = inferredIdentity(value)");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "inferredIdentity");
    }

    [Fact]
    public void ExpectedResultCannotChangeAConcretePartOfAnInvariantGenericResult()
    {
        var analysis = Analyze("class Pair<A, B> {}\n"
            + "func <T> make(): Pair<T, int> = throw RuntimeException()\n"
            + "func use(): Pair<String, short> = make()");

        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
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

    [Fact]
    public void ProjectionDepthBudgetReturnsAnInferenceFailureInsteadOfThrowing()
    {
        var source = new StringBuilder("interface Root<T> {}\nopen class Layer0<T>: Root<T> {}\n");
        for (int index = 1; index <= 130; index++)
        {
            source.Append("open class Layer").Append(index).Append("<T>: Layer").Append(index - 1)
                .Append("<T> {}\n");
        }
        source.Append("func <T> make(): Layer130<T> = throw RuntimeException()\n")
            .Append("func use(): Root<String> = make()");

        var analysis = Analyze(source.ToString());

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2290");
    }
}
