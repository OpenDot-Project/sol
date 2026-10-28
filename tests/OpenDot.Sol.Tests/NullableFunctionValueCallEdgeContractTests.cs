// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NullableFunctionValueCallEdgeContractTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nopen class IOException: Exception {}\n";

    private sealed record Scenario(SourceAnalysis Sources, BodyAnalysis Bodies);

    [Fact]
    public void ProvenNullBranchIsRejectedWhileTheElseBranchInvokesTheProvenNonNullValue()
    {
        var scenario = Analyze("func invoke(callback: (() -> int)?): int { "
            + "if callback == null { callback() } else { callback() } }");
        var calls = Calls(scenario, "callback()");
        var diagnostic = Assert.Single(scenario.Bodies.Diagnostics,
            candidate => candidate.Code == "SOL2243");

        Assert.Equal(2, calls.Length);
        Assert.Equal(calls[0].Origin.Span, diagnostic.Span);
        Assert.Equal("callback()", calls[0].Origin.Text);
        Assert.Equal("callback()", calls[1].Origin.Text);
        Assert.Equal(BodyBindingStatus.Bound, scenario.Bodies.GetExpression(calls[1])?.Status);
        Assert.Equal("int", Assert.IsType<ResolvedType>(scenario.Bodies.GetExpression(calls[1])?.Type).Name);
    }

    [Fact]
    public void AssigningANonNullFunctionInTheNullBranchMakesTheJoinNonNull()
    {
        var scenario = Analyze("func target(): int = 19\n"
            + "func invoke(callback: (() -> int)?): int { var current = callback; "
            + "if current == null { current = target }; current() }");
        var call = Assert.Single(Calls(scenario, "current()"));
        var assignment = Assert.Single(scenario.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAssignment>());
        var assignmentValue = Assert.IsAssignableFrom<AstExpression>(assignment.Value);
        var destination = Assert.Single(scenario.Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols),
            symbol => symbol.Name == "current");
        var nullableType = Assert.IsType<ResolvedType>(scenario.Bodies.GetLocalType(destination));
        var assignedType = Assert.IsType<ResolvedType>(scenario.Bodies.GetExpression(assignmentValue)?.Type);

        Assert.True(nullableType.IsNullable);
        Assert.Equal(ResolvedTypeKind.Function, nullableType.Arguments[0].Kind);
        Assert.Same(nullableType.Arguments[0], assignedType);
        Assert.Empty(scenario.Bodies.Diagnostics);
        Assert.True(scenario.Bodies.IsComplete);
        Assert.Equal(BodyBindingStatus.Bound, scenario.Bodies.GetExpression(call)?.Status);
        Assert.Equal("int", Assert.IsType<ResolvedType>(scenario.Bodies.GetExpression(call)?.Type).Name);
    }

    [Fact]
    public void AssignmentOnOnlyOneUnrelatedBranchDoesNotProveTheCallableNonNull()
    {
        var scenario = Analyze("func target(): int = 19\n"
            + "func invoke(enabled: bool, initial: (() -> int)?): int { var current = initial; "
            + "if enabled { current = target }; current() }");
        var call = Assert.Single(Calls(scenario, "current()"));
        var diagnostic = Assert.Single(scenario.Bodies.Diagnostics,
            candidate => candidate.Code == "SOL2243");

        Assert.Equal(call.Origin.Span, diagnostic.Span);
        Assert.Equal(BodyBindingStatus.Invalid, scenario.Bodies.GetExpression(call)?.Status);
    }

    [Fact]
    public void FunctionValueOutAssignmentCanBeCheckedAndInvokedAfterTheCall()
    {
        var scenario = Analyze("func provide(out destination: (() -> int)?): () { destination = func() { 27 } }\n"
            + "func invoke(): int { var current: (() -> int)?; "
            + "val install: (out (() -> int)?) -> () = provide; install(out current); "
            + "if current == null { return 0 }; current() }");
        var call = Assert.Single(Calls(scenario, "current()"));

        Assert.Empty(scenario.Bodies.Diagnostics);
        Assert.True(scenario.Bodies.IsComplete);
        Assert.Equal(BodyBindingStatus.Bound, scenario.Bodies.GetExpression(call)?.Status);
        Assert.Equal("int", Assert.IsType<ResolvedType>(scenario.Bodies.GetExpression(call)?.Type).Name);
    }

    [Fact]
    public void FunctionValueOutCallInvalidatesAnEarlierNonNullRefinement()
    {
        var scenario = Analyze("func clear(out destination: (() -> int)?): () { destination = null }\n"
            + "func invoke(callback: (() -> int)?): int { var current = callback; "
            + "if current == null { return 0 }; "
            + "val reset: (out (() -> int)?) -> () = clear; reset(out current); current() }");
        var call = Assert.Single(Calls(scenario, "current()"));
        var diagnostic = Assert.Single(scenario.Bodies.Diagnostics,
            candidate => candidate.Code == "SOL2243");

        Assert.Equal(call.Origin.Span, diagnostic.Span);
        Assert.Equal("current()", Encoding.UTF8.GetString(diagnostic.Source.Slice(diagnostic.Span)));
    }

    [Fact]
    public void MutableNonNullRefinementIsNotCarriedIntoTheCheckedExceptionCatchEdge()
    {
        var scenario = Analyze("func invoke(callback: (() -> int throws IOException)?, failure: IOException): int throws IOException { "
            + "var current = callback; if current == null { return 0 }; "
            + "val invalidate: () -> () throws IOException = { current = null; throw failure }; "
            + "try { invalidate() } catch (error: IOException) { return current() } }");
        var calls = Calls(scenario, "current()");
        var diagnostic = Assert.Single(scenario.Bodies.Diagnostics,
            candidate => candidate.Code == "SOL2243");

        Assert.Single(calls);
        Assert.Equal(calls[0].Origin.Span, diagnostic.Span);
        Assert.Equal("current()", Encoding.UTF8.GetString(diagnostic.Source.Slice(diagnostic.Span)));
    }

    [Fact]
    public void ImmutableNonNullAliasSurvivesTheCheckedExceptionCatchEdge()
    {
        var scenario = Analyze("func invoke(callback: (() -> int throws IOException)?): int throws IOException { "
            + "val current = callback; if current == null { return 0 }; "
            + "try { return current() } catch (error: IOException) { return current() } }");
        var calls = Calls(scenario, "current()");

        Assert.Empty(scenario.Bodies.Diagnostics);
        Assert.True(scenario.Bodies.IsComplete);
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call =>
        {
            Assert.Equal(BodyBindingStatus.Bound, scenario.Bodies.GetExpression(call)?.Status);
            Assert.Equal("int", Assert.IsType<ResolvedType>(scenario.Bodies.GetExpression(call)?.Type).Name);
        });
    }

    [Fact]
    public void FinallyWriteInvalidatesTheProofForCallsAfterCleanupButNotBeforeIt()
    {
        var scenario = Analyze("func invoke(callback: (() -> int)?): int { var current = callback; "
            + "if current == null { return 0 }; "
            + "try { val result = current() } finally { current = null }; current() }");
        var calls = Calls(scenario, "current()");
        var diagnostic = Assert.Single(scenario.Bodies.Diagnostics,
            candidate => candidate.Code == "SOL2243");

        Assert.Equal(2, calls.Length);
        Assert.Equal(calls[1].Origin.Span, diagnostic.Span);
        Assert.Equal(BodyBindingStatus.Bound, scenario.Bodies.GetExpression(calls[0])?.Status);
        Assert.Equal("int", Assert.IsType<ResolvedType>(scenario.Bodies.GetExpression(calls[0])?.Type).Name);
    }

    private static Scenario Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, Diagnostics(sources.Diagnostics));
        return new(sources, BodyAnalysis.Create(sources, TestContext.Current.CancellationToken));
    }

    private static AstCall[] Calls(Scenario scenario, string text) => scenario.Sources.Declarations.Files
        .SelectMany(file => file.Root.Descendants()).OfType<AstCall>()
        .Where(call => call.Origin.Text == text).OrderBy(call => call.Origin.Span.Start).ToArray();

    private static string Diagnostics(System.Collections.Immutable.ImmutableArray<OpenDot.Sol.Diagnostics.Diagnostic> diagnostics)
        => string.Join('\n', diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Span
            + " " + diagnostic.Message));
}
