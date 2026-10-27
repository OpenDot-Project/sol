// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericCallArgumentMappingTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\nopen class RuntimeException: Exception {}";

    private static SourceAnalysis Sources(string source) => SourceAnalysis.Create(
        AstTestSources.Session(new[] { "module app\n" + source, Core }), TestContext.Current.CancellationToken);

    private static BodyAnalysis Bodies(string source)
    {
        var sources = Sources(source);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static CallAnalysis Calls(string source)
    {
        var sources = Sources(source);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return CallAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void InferenceUsesTheFormalParameterAfterNamedReordering()
    {
        var analysis = Bodies("func <T, U> firstOf(first: T, second: U): T = first\n"
            + "func main(number: int, flag: bool): int { firstOf(second = flag, first = number) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "firstOf(second = flag, first = number)");
        Assert.Equal(new[] { "int", "bool" }, call.TypeArguments.Select(t => t.Name));
        Assert.Equal(new[] { 1, 0 }, call.Arguments.Select(a => a.ParameterIndex));
        Assert.Equal(new[] { "int", "bool" }, call.ParameterTypes.Select(t => t.Name));
    }

    [Fact]
    public void ExplicitGenericOrdinalsSurviveNamedMappingAndOmittedDefaults()
    {
        var analysis = Bodies("class Text {}\nfunc <T, U> select(first: T, second: U, fallback: U = second): U = fallback\n"
            + "func main(number: int, text: Text): int { select<Text, int>(second = number, first = text) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text
            == "select<Text, int>(second = number, first = text)");
        Assert.Equal(new[] { "app.Text", "int" }, call.TypeArguments.Select(t => t.Name));
        Assert.Equal(new[] { 1, 0 }, call.Arguments.Select(a => a.ParameterIndex));
        Assert.Equal(new[] { 2 }, call.DefaultParameters);
        Assert.Equal(new[] { "app.Text", "int", "int" }, call.ParameterTypes.Select(t => t.Name));
    }

    [Fact]
    public void InferredGenericDefaultUsesTheCompletedSubstitutionButAddsNoEvidence()
    {
        var analysis = Bodies("func <T> cascade(first: T, second: T = first, third: T = second): T = third\n"
            + "func mark(value: int): int = value\n"
            + "func main(): int { cascade(third = mark(30), first = mark(10)) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text
            == "cascade(third = mark(30), first = mark(10))");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(new[] { 2, 0 }, call.Arguments.Select(a => a.ParameterIndex));
        Assert.Equal(new[] { 1 }, call.DefaultParameters);
        Assert.Equal(new[] { "int", "int", "int" }, call.ParameterTypes.Select(t => t.Name));
    }

    [Fact]
    public void GenericMiddleVarargMapsEachElementAndKeepsTheNamedTail()
    {
        var analysis = Bodies("func <T> route(head: T, vararg items: T, done: bool, fallback: T = head): int = items.length\n"
            + "func main(): int { route(1, 2, 3, done = true) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "route(1, 2, 3, done = true)");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
        Assert.Equal(new[] { 0, 1, 1, 2 }, call.Arguments.Select(a => a.ParameterIndex));
        Assert.All(call.Arguments.Skip(1).Take(2), argument => Assert.True(argument.IsExpandedVararg));
        Assert.Equal(new[] { 3 }, call.DefaultParameters);
        Assert.Empty(call.EmptyVarargParameters);
    }

    [Fact]
    public void GenericRefAndOutArgumentsUseMappedExactEvidence()
    {
        var analysis = Bodies("func <T> replace(ref value: T, replacement: T): T { value = replacement; value }\n"
            + "func <T> assign(out value: T, source: T) { value = source }\n"
            + "func main(number: int): int { var current = number; replace(replacement = number, value = ref current); "
            + "assign(source = number, value = out var result); result }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var replace = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text
            == "replace(replacement = number, value = ref current)");
        Assert.Equal("int", Assert.Single(replace.TypeArguments).Name);
        Assert.Equal(new[] { 1, 0 }, replace.Arguments.Select(a => a.ParameterIndex));
        var assign = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text
            == "assign(source = number, value = out var result)");
        Assert.Equal("int", Assert.Single(assign.TypeArguments).Name);
        Assert.Equal(new[] { 1, 0 }, assign.Arguments.Select(a => a.ParameterIndex));
    }

    [Fact]
    public void GenericRefEvidenceConflictsWithAnIncompatibleValueArgument()
    {
        var analysis = Bodies("func <T> replace(ref value: T, replacement: T): T { value = replacement; value }\n"
            + "func main(): () { var current = 1; replace(ref current, \"different\"); }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Call.Origin.Text == "replace(ref current, \"different\")");
    }

    [Theory]
    [InlineData("collect(values)", CallBindingStatus.Bound)]
    [InlineData("collect<int>(values)", CallBindingStatus.NoApplicableCandidate)]
    [InlineData("collect<[]int>(values)", CallBindingStatus.Bound)]
    [InlineData("collect(values = values)", CallBindingStatus.Bound)]
    [InlineData("collect(*values)", CallBindingStatus.Bound)]
    [InlineData("collect([1])", CallBindingStatus.Bound)]
    public void GenericVarargArrayFormsStayDistinct(string spelling, CallBindingStatus expected)
    {
        var analysis = Calls("func <T> collect(vararg values: T): []T = values\n"
            + "func main(values: []int): () { " + spelling + " }");
        var binding = Assert.Single(analysis.Bindings);

        Assert.Equal(expected, binding.Status);
        if (expected != CallBindingStatus.Bound) { return; }
        var inferred = Assert.Single(binding.Selected!.TypeArguments);
        if (spelling is "collect(values)" or "collect<[]int>(values)" or "collect([1])")
        {
            Assert.Equal(ResolvedTypeKind.Array, inferred.Kind);
            Assert.Equal("int", Assert.Single(inferred.Arguments).Name);
        }
        else { Assert.Equal("int", inferred.Name); }
        var argument = Assert.Single(binding.Selected.Arguments);
        Assert.Equal(spelling.Contains("values =", System.StringComparison.Ordinal), argument.IsArrayArgument);
        Assert.Equal(spelling.StartsWith("collect(*", System.StringComparison.Ordinal), argument.IsSpread);
    }

    [Fact]
    public void ExplicitAndInferredCallsRejectOutWithoutATypeSource()
    {
        var analysis = Bodies("func <T> create(out value: T) {}\n"
            + "func main(): () { create(out var inferred) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Call.Origin.Text == "create(out var inferred)");
    }

    [Theory]
    [InlineData("Utility.collect(values)", true)]
    [InlineData("Utility.collect<int>(values)", false)]
    [InlineData("Utility.collect<[]int>(values)", true)]
    public void GenericObjectMethodsInferArrayElementsAndRespectExplicitTypeArguments(string spelling, bool expectedBound)
    {
        var analysis = Bodies("object Utility { func <T> collect(vararg values: T): int = values.length }\n"
            + "func main(values: []int): int { " + spelling + " }");

        if (expectedBound)
        {
            Assert.Empty(analysis.Diagnostics);
            var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == spelling);
            var argument = Assert.Single(call.TypeArguments);
            Assert.Equal(ResolvedTypeKind.Array, argument.Kind);
            Assert.Equal("int", Assert.Single(argument.Arguments).Name);
        }
        else
        {
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
            Assert.DoesNotContain(analysis.Calls, binding => binding.Call.Origin.Text == spelling);
        }
    }
}
