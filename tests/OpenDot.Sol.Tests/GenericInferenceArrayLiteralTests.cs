// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInferenceArrayLiteralTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\nopen class RuntimeException: Exception {}";

    private static BodyAnalysis Analyze(string source)
    {
        var declarations = AstTestSources.Session(["module app\n" + source, Core]);
        var sources = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ArrayNumericLiteralCanUseAnElementTypeFixedByAnotherArgument()
    {
        var analysis = Analyze("func <T> choose(values: []T, seed: T): T = seed\n"
            + "func use(seed: byte): byte = choose([1], seed)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "choose([1], seed)");
        Assert.Equal("byte", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NestedArrayNumericLiteralUsesTheClosedLeafElementType()
    {
        var analysis = Analyze("func <T> choose(values: [][]T, seed: T): T = seed\n"
            + "func use(seed: byte): byte = choose([[1], []], seed)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "choose([[1], []], seed)");
        Assert.Equal("byte", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NegativeNumericLiteralUsesTheClosedSignedElementType()
    {
        var analysis = Analyze("func <T> choose(values: []T, seed: T): T = seed\n"
            + "func use(seed: sbyte): sbyte = choose([-1], seed)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "choose([-1], seed)");
        Assert.Equal("sbyte", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NumericLiteralOnlyArrayStillUsesItsDefaultElementType()
    {
        var analysis = Analyze("func <T> first(values: []T): T = values[0]\n"
            + "func use(): int = first([1])");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "first([1])");
        Assert.Equal("int", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void EmptyArrayCanBeContextuallyCheckedAfterAnotherArgumentClosesItsElementType()
    {
        var analysis = Analyze("func <T> choose(values: []T, seed: T): T = seed\n"
            + "func use(seed: byte): byte = choose([], seed)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "choose([], seed)");
        Assert.Equal("byte", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NullElementIsCheckedAgainstTheNullableShapeAfterNonNullEvidenceClosesTheType()
    {
        var analysis = Analyze("func <T> choose(values: []T?, seed: T): T = seed\n"
            + "func use(seed: String): String = choose([seed, null], seed)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "choose([seed, null], seed)");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void OutOfRangeLiteralIsRejectedAfterTheElementTypeCloses()
    {
        var analysis = Analyze("func <T> choose(values: []T, seed: T): T = seed\n"
            + "func use(seed: byte): byte = choose([256], seed)");

        Assert.True(analysis.HasErrors);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Call.Origin.Text == "choose([256], seed)");
    }

    [Fact]
    public void RejectedArrayCandidateDoesNotPolluteTheSelectedCandidateEvidence()
    {
        var analysis = Analyze("func <T> choose(values: []T, seed: T): T = seed\n"
            + "func choose(values: []String, seed: String): String = seed\n"
            + "func use(seed: byte): byte = choose([1], seed)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "choose([1], seed)");
        Assert.Equal("byte", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("byte", call.ReturnType!.Name);
    }

    [Fact]
    public void TypedNumericArrayElementDoesNotAcquireLiteralNarrowing()
    {
        var analysis = Analyze("func <T> choose(values: []T, seed: T): T = seed\n"
            + "func use(value: int, seed: byte): () { choose([value], seed) }");

        if (!analysis.HasErrors)
        {
            var call = Assert.Single(analysis.Calls, binding => binding.Call.Origin.Text == "choose([value], seed)");
            Assert.NotEqual("byte", Assert.Single(call.TypeArguments).Name);
        }
        else
        {
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code is "SOL2221" or "SOL2206");
        }
    }
}
