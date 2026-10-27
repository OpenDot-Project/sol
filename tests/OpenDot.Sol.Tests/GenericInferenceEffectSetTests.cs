// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInferenceEffectSetTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\nopen class RuntimeException: Exception {}";

    private const string FailureTypes = "class ReadFailure<T>: Exception {}\n"
        + "class NetworkFailure<T>: Exception {}\n"
        + "func read(): () throws ReadFailure<String> { () }\n"
        + "func network(): () throws NetworkFailure<int> { () }\n";

    private static BodyAnalysis Analyze(string source)
    {
        var declarations = AstTestSources.Session(new[] { "module app\n" + source, Core });
        var sources = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void NestedOpenPatternsMatchByConstructedTypeAndIgnoreEffectOrder()
    {
        var analysis = Analyze(FailureTypes
            + "func <T, U> run(action: () -> () throws ReadFailure<T>, NetworkFailure<U>): () throws ReadFailure<T>, NetworkFailure<U> { action() }\n"
            + "func first(): () throws ReadFailure<String>, NetworkFailure<int> { run({ network(); read() }) }\n"
            + "func second(): () throws ReadFailure<String>, NetworkFailure<int> { run({ read(); network() }) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.True(analysis.IsComplete);
        var calls = analysis.Calls.Where(binding => binding.Target.Name == "run").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, binding => Assert.Equal(new[] { "std.core.String", "int" },
            binding.TypeArguments.Select(type => type.Name)));
    }

    [Fact]
    public void SymmetricNestedPatternsRemainDeferredInsteadOfUsingSourceOrder()
    {
        var analysis = Analyze("class Failure<T>: Exception {}\n"
            + "func left(): () throws Failure<String> { () }\n"
            + "func right(): () throws Failure<int> { () }\n"
            + "func <T, U> run(action: () -> () throws Failure<T>, Failure<U>): () throws Failure<T>, Failure<U> { action() }\n"
            + "func use(): () throws Failure<String>, Failure<int> { run({ left(); right() }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "run");
    }

    [Fact]
    public void EmptyActualSetDoesNotInventEffectTypeArguments()
    {
        var analysis = Analyze("class Failure<T>: Exception {}\n"
            + "func <T, U> run(action: () -> () throws Failure<T>, Failure<U>): () throws Failure<T>, Failure<U> { action() }\n"
            + "func use(): () { run({ () }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "run");
    }

    [Fact]
    public void DuplicateEquivalentEffectsRemainOneSetMember()
    {
        var analysis = Analyze("class Failure<T>: Exception {}\n"
            + "func read(): () throws Failure<String> { () }\n"
            + "func <T> run(action: () -> () throws Failure<T>, Failure<T>): () throws Failure<T> { action() }\n"
            + "func use(): () throws Failure<String> { run({ read(); read() }) }");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.True(analysis.IsComplete);
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "run");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NonMatchingNestedEffectConstructorLeavesParameterUnresolved()
    {
        var analysis = Analyze(FailureTypes
            + "func <T> run(action: () -> () throws ReadFailure<T>): () throws ReadFailure<T> { action() }\n"
            + "func use(): () throws NetworkFailure<int> { run({ network() }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "run");
    }

    [Fact]
    public void IndependentBoundsPruneInvalidPermutationsBeforeSearchBudget()
    {
        const int count = 7;
        var source = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            source.Append("interface Marker").Append(index).AppendLine(" {}");
            source.Append("class Value").Append(index).Append(": Marker").Append(index).AppendLine(" {}");
            source.Append("func read").Append(index).Append("(): () throws Failure<Value").Append(index)
                .AppendLine("> { () }");
        }
        source.AppendLine("class Failure<T>: Exception {}");
        var parameters = string.Join(", ", Enumerable.Range(0, count).Select(index => $"T{index}: Marker{index}"));
        var callbackEffects = string.Join(", ", Enumerable.Range(0, count).Select(index => $"Failure<T{index}>"));
        var actualEffects = string.Join(", ", Enumerable.Range(0, count).Select(index => $"Failure<Value{index}>"));
        var reads = string.Join("; ", Enumerable.Range(0, count).Select(index => $"read{index}()"));
        source.Append("func <").Append(parameters).Append("> relay(action: () -> () throws ")
            .Append(callbackEffects).Append("): () throws ").Append(callbackEffects).AppendLine(" { action() }");
        source.Append("func use(): () throws ").Append(actualEffects).Append(" { relay({ ")
            .Append(reads).AppendLine("; () }) }");

        var analysis = Analyze(source.ToString());

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete,
            string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "relay");
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"app.Value{index}"),
            call.TypeArguments.Select(argument => argument.Name));
    }

    [Fact]
    public void DependentConstructedBoundsRejectAnIncompatibleDirectMapping()
    {
        var analysis = Analyze("interface Link<T> {}\n"
            + "class First: Link<First> {}\nclass Second: Link<Second> {}\n"
            + "func <T: Link<U>, U: Link<T>> pair(first: T, second: U): () = ()\n"
            + "func use(first: First, second: Second): () { pair(first, second) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "pair");
    }

    [Fact]
    public void NestedFunctionEffectsInsideConstructedExceptionsContributeInferenceEvidence()
    {
        var analysis = Analyze("class LeafFailure<T>: Exception {}\n"
            + "class InnerFailure<T>: Exception {}\n"
            + "class OuterFailure<T>: Exception {}\n"
            + "func fail(): () throws OuterFailure<() -> () throws InnerFailure<() -> () throws LeafFailure<String>>> { () }\n"
            + "func <T> relay(action: () -> () throws OuterFailure<() -> () throws InnerFailure<() -> () throws LeafFailure<T>>>): () throws OuterFailure<() -> () throws InnerFailure<() -> () throws LeafFailure<T>>> { action() }\n"
            + "func use(): () throws OuterFailure<() -> () throws InnerFailure<() -> () throws LeafFailure<String>>> { relay({ fail() }) }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete,
            string.Join(" | ", analysis.UnverifiedNodes.Select(node => node.Origin.Text)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "relay");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NestedConstructedExceptionMatchingKeepsSymmetricAssignmentsAmbiguous()
    {
        var analysis = Analyze("class InnerFailure<T>: Exception {}\n"
            + "class OuterFailure<T>: Exception {}\n"
            + "func left(): () throws OuterFailure<() -> () throws InnerFailure<String>> { () }\n"
            + "func right(): () throws OuterFailure<() -> () throws InnerFailure<int>> { () }\n"
            + "func <T, U> relay(action: () -> () throws OuterFailure<() -> () throws InnerFailure<T>>, OuterFailure<() -> () throws InnerFailure<U>>): () throws OuterFailure<() -> () throws InnerFailure<T>>, OuterFailure<() -> () throws InnerFailure<U>> { action() }\n"
            + "func use(): () throws OuterFailure<() -> () throws InnerFailure<String>>, OuterFailure<() -> () throws InnerFailure<int>> { relay({ left(); right() }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "relay");
    }
}
