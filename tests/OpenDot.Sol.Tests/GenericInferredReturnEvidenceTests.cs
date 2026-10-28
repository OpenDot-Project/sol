// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInferredReturnEvidenceTests
{
    private const string Core = "module std.core\n"
        + "open class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string source)
    {
        var sources = SourceAnalysis.Create(AstTestSources.Session(["module app\n" + source, Core]),
            TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Diagnostics(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(
        diagnostic => diagnostic.Code + " " + diagnostic.Message));

    private static BodyCallBinding SingleCall(BodyAnalysis analysis, string name) =>
        Assert.Single(analysis.Calls, binding => binding.Target.Name == name);

    [Fact]
    public void ExpectedFunctionResultClosesGenericParameterInsideInferredFunctionBody()
    {
        var analysis = Analyze("func <T> provide() = { value: T -> value }\n"
            + "func use(): (String) -> String = provide()");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.True(analysis.IsComplete, string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = SingleCall(analysis, "provide");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(ResolvedTypeKind.Function, call.ReturnType!.Kind);
        Assert.Equal("std.core.String", call.ReturnType.Arguments[0].Name);
        Assert.Equal("std.core.String", call.ReturnType.Arguments[1].Name);
    }

    [Fact]
    public void ExpectedNullableFunctionTypeClosesGenericParameterInsideInferredBody()
    {
        var source = "func <T> provide() = { value: T -> value }\n"
            + "func use(): ((String) -> String)? = provide()";
        var analysis = Analyze(source);

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "provide");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(ResolvedTypeKind.Function, call.ReturnType!.Kind);
    }

    [Fact]
    public void ExpectedTypeClosesGenericParameterNestedInInferredNominalResult()
    {
        var analysis = Analyze("class Box<T>(val value: T) {}\n"
            + "func <T> create(value: T? = null) = Box(value)\n"
            + "func use(): Box<String?> = create()");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "create");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("app.Box", call.ReturnType!.Name);
        Assert.Equal("std.core.String", Assert.Single(call.ReturnType.Arguments).Arguments.Single().Name);
    }

    [Fact]
    public void ExpectedTypeClosesGenericParameterInsideInferredArrayResult()
    {
        var analysis = Analyze("func <T> values(value: T? = null) = [value]\n"
            + "func use(): []String? = values()");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "values");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(ResolvedTypeKind.Array, call.ReturnType!.Kind);
        Assert.Equal(ResolvedTypeKind.Nullable, Assert.Single(call.ReturnType.Arguments).Kind);
        Assert.Equal("std.core.String", Assert.Single(call.ReturnType.Arguments).Arguments.Single().Name);
    }

    [Fact]
    public void InferredReturnExpectedEvidenceDoesNotReplaceArgumentEvidence()
    {
        var analysis = Analyze("func <T> identity(value: T) = value\n"
            + "func use(value: int): int = identity(value)\n"
            + "func invalid(value: int): String = identity(value)");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "identity");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void ExplicitTypeArgumentsRemainFixedWhenExpectedResultDiffers()
    {
        var analysis = Analyze("func <T> maybe(value: T? = null) = value\n"
            + "func use(): Object? = maybe<String>()");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        Assert.Equal("std.core.String", Assert.Single(SingleCall(analysis, "maybe").TypeArguments).Name);
        Assert.Equal(ResolvedTypeKind.Nullable, SingleCall(analysis, "maybe").ReturnType!.Kind);
    }

    [Fact]
    public void ExplicitTypeArgumentThatCannotConvertToExpectedResultIsRejected()
    {
        var analysis = Analyze("func <T> maybe(value: T? = null) = value\n"
            + "func use(): String = maybe<Object>()");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.Equal("std.core.Object", Assert.Single(SingleCall(analysis, "maybe").TypeArguments).Name);
    }

    [Fact]
    public void GenericConstraintIsCheckedAfterExpectedResultClosesInferredBody()
    {
        var analysis = Analyze("interface Marker {}\nclass Mark: Marker {}\n"
            + "func <T: Marker> create(value: T? = null) = value\n"
            + "func valid(): Mark? = create()\n"
            + "func invalid(): String? = create()");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        var valid = Assert.Single(analysis.Calls, binding => binding.Target.Name == "create");
        Assert.Equal("app.Mark", Assert.Single(valid.TypeArguments).Name);
    }

    [Fact]
    public void RecursiveInferredReturnDoesNotSupplySpeculativeGenericEvidence()
    {
        var analysis = Analyze("func <T> cycle(value: T) = cycle(value)\n"
            + "func use(): String = cycle(\"seed\")");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2200");
        Assert.NotEmpty(analysis.Calls.Where(binding => binding.Target.Name == "cycle"));
        Assert.All(analysis.Calls.Where(binding => binding.Target.Name == "cycle"), binding =>
            Assert.Null(binding.ReturnType));
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void BottomInferredReturnDoesNotInventUnusedGenericArgument()
    {
        var analysis = Analyze("func <T> fail() = throw RuntimeException()\n"
            + "func use(): String = fail()");

        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "fail");
    }

    [Fact]
    public void ExpectedResultDoesNotCreateEvidenceForUnannotatedBlockReturn()
    {
        var analysis = Analyze("func <T> discard(value: T) { value }\n"
            + "func use(): () { val result: String = discard(\"seed\") }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        var call = SingleCall(analysis, "discard");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(ResolvedTypeKind.Tuple, call.ReturnType!.Kind);
        Assert.Empty(call.ReturnType.Arguments);
    }

    [Fact]
    public void SelectedGenericExpressionBodyRetainsItsClosedResultInCallBinding()
    {
        var analysis = Analyze("func <T> choose(value: T) = value\n"
            + "func use(): String = choose(\"result\")");

        Assert.False(analysis.HasErrors, Diagnostics(analysis));
        var call = SingleCall(analysis, "choose");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("std.core.String", call.ReturnType!.Name);
    }
}
