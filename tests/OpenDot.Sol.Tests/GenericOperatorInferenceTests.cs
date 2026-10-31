// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericOperatorInferenceTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable() {}\n"
        + "open class Exception(): Throwable() {}\nopen class RuntimeException(): Exception() {}\n";

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(["module app\n" + source, Core]);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void OperatorTypeArgumentsAreClosedBeforeRankingAndConstraintRejectionIsCandidateLocal()
    {
        var analysis = Analyze(
            "interface Marker {}\n"
                + "struct Number { companion object {\n"
                + "    operator func <T: Marker> +(left: Number, right: T): Number = left\n"
                + "    operator func +(left: Number, right: Object): String = \"fallback\"\n"
                + "} }\n"
                + "struct Marked: Marker {}\n"
                + "struct Plain {}\n"
                + "func generic(left: Number, marker: Marked): Number = left + marker\n"
                + "func fallback(other: Number, plain: Plain): String = other + plain");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal("app.Number", analysis.Expressions.Single(binding => binding.Expression.Origin.Text == "left + marker").Type!.Name);
        Assert.Equal("std.core.String", analysis.Expressions.Single(binding => binding.Expression.Origin.Text == "other + plain").Type!.Name);
    }

    [Fact]
    public void ExpectedResultCanCloseOperatorTypeArgumentsMissingFromOperands()
    {
        var analysis = Analyze(
            "struct Token { companion object {\n"
                + "    operator func <T> +(left: Token, right: Token): T = throw RuntimeException()\n"
                + "} }\n"
                + "func use(left: Token, right: Token): String = left + right");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var expression = Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "left + right");
        Assert.Equal("std.core.String", expression.Type!.Name);
        Assert.Equal(BodyBindingStatus.Bound, expression.Status);
    }

    [Fact]
    public void UnaryOperatorUsesTheSurroundingExpectedResultForInference()
    {
        var analysis = Analyze(
            "struct Token { companion object {\n"
                + "    operator func <T> +(value: Token): T = throw RuntimeException()\n"
                + "} }\n"
                + "func use(value: Token): String = +value");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var expression = Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "+value");
        Assert.Equal("std.core.String", expression.Type!.Name);
        Assert.Equal(BodyBindingStatus.Bound, expression.Status);
    }

    [Fact]
    public void GenericOperatorExpectedResultUsesConstructedBaseProjection()
    {
        var analysis = Analyze(
            "interface Parent<T> {}\n"
                + "class Projected<T>: Parent<T> {}\n"
                + "struct Token { companion object {\n"
                + "    operator func <T> +(value: Token): Projected<T> = throw RuntimeException()\n"
                + "} }\n"
                + "func use(value: Token): Parent<String> = +value");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var expression = Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "+value");
        Assert.Equal("app.Projected", expression.Type!.Name);
        Assert.Equal(BodyBindingStatus.Bound, expression.Status);
    }

    [Fact]
    public void NongenericOperatorWinsAnEqualOperandConversionVector()
    {
        var analysis = Analyze(
            "struct Token { companion object {\n"
                + "    operator func <T> +(left: T, right: Token): T = left\n"
                + "    operator func +(left: Token, right: Token): String = \"plain\"\n"
                + "} }\n"
                + "func use(left: Token, right: Token): Object = left + right");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var expression = Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "left + right");
        Assert.Equal("std.core.String", expression.Type!.Name);
    }

    [Fact]
    public void NullableOperatorLiftingUsesExpectedPayloadToCloseGenericResult()
    {
        var analysis = Analyze(
            "struct Token { companion object {\n"
                + "    operator func <T> +(left: Token, right: Token): T = throw RuntimeException()\n"
                + "} }\n"
                + "func use(left: Token?, right: Token?): String? = left + right");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var expression = Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "left + right");
        Assert.True(expression.Type!.IsNullable);
        Assert.Equal("std.core.String", expression.Type.Arguments[0].Name);
        Assert.Equal(BodyBindingStatus.Bound, expression.Status);
    }

    [Fact]
    public void GenericOperatorWithoutOperandOrExpectedResultEvidenceRemainsDeferred()
    {
        var analysis = Analyze(
            "struct Token { companion object {\n"
                + "    operator func <T> +(left: Token, right: Token): T = throw RuntimeException()\n"
                + "} }\n"
                + "func use(left: Token, right: Token): () { val result = left + right }");

        Assert.False(analysis.IsComplete);
        var expression = Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "left + right");
        Assert.Equal(BodyBindingStatus.Deferred, expression.Status);
        Assert.Null(expression.Type);
    }

    [Fact]
    public void ExpectedResultTypeMustSatisfyGenericOperatorConstraints()
    {
        var analysis = Analyze(
            "interface Marker {}\n"
                + "struct Token { companion object {\n"
                + "    operator func <T: Marker> +(left: Token, right: Token): T = throw RuntimeException()\n"
                + "} }\n"
                + "class Marked: Marker {}\n"
                + "func use(left: Token, right: Token): Marked = left + right");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var expression = Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "left + right");
        Assert.Equal("app.Marked", expression.Type!.Name);
        Assert.Equal(BodyBindingStatus.Bound, expression.Status);
    }

    [Fact]
    public void UnsatisfiedExpectedResultOperatorConstraintRejectsThatCandidate()
    {
        var analysis = Analyze(
            "interface Marker {}\n"
                + "struct Token { companion object {\n"
                + "    operator func <T: Marker> +(left: Token, right: Token): T = throw RuntimeException()\n"
                + "} }\n"
                + "func use(left: Token, right: Token): String = left + right");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2207");
        Assert.Null(Assert.Single(analysis.Expressions,
            binding => binding.Expression.Origin.Text == "left + right").Type);
    }

    [Fact]
    public void CompoundAssignmentContractSuppliesTheStorageTypeAsInferenceEvidence()
    {
        var analysis = Analyze(
            "struct Token { companion object {\n"
                + "    operator func <T> +(left: Token, right: Token): T = throw RuntimeException()\n"
                + "} }\n"
                + "func use(ref value: Token, right: Token): () { value += right }");

        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code is "SOL2207" or "SOL2222");
        var use = Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use");
        Assert.Equal(BodyBindingStatus.Bound, use.Status);
    }
}
