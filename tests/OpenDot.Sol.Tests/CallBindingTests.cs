// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class CallBindingTests
{
    private static CallAnalysis Analyze(string text, params string[] extra)
    {
        var source = SourceAnalysis.Create(AstTestSources.Session(new[] { "module app\n" + text }.Concat(extra).ToArray()),
            TestContext.Current.CancellationToken);
        Assert.False(source.HasErrors, string.Join('\n', source.Diagnostics.Select(d => d.Message)));
        return CallAnalysis.Create(source, TestContext.Current.CancellationToken);
    }

    private static CallBinding Call(CallAnalysis analysis, string spelling) => analysis.Bindings.Single(b => b.Call.Origin.Text == spelling);

    [Fact]
    public void ExactDefaultLiteralTypeWinsAndNumericVariablesDoNotWiden()
    {
        var analysis = Analyze("func pick(value: int): int = value\nfunc pick(value: long): long = value\n"
            + "func only(value: long): long = value\nfunc main(value: int): () { pick(1); only(value); only(1) }");
        Assert.Equal("int", Call(analysis, "pick(1)").Selected!.ReturnType!.Name);
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Call(analysis, "only(value)").Status);
        Assert.Equal("long", Call(analysis, "only(1)").Selected!.ReturnType!.Name);
        Assert.Single(analysis.Diagnostics);
    }

    [Fact]
    public void NamedArgumentsKeepEvaluationOrderAndDefaultsStayExplicit()
    {
        var analysis = Analyze("func pick(a: int, b: bool = true, c: int = 2): int = a\nfunc main(): () { pick(c = 4, a = 1) }");
        var selected = Assert.Single(analysis.Bindings).Selected!;
        Assert.Equal(new[] { 2, 0 }, selected.Arguments.Select(a => a.ParameterIndex));
        Assert.Equal(new[] { 0, 1 }, selected.Arguments.Select(a => a.ArgumentIndex));
        Assert.Equal(new[] { 1 }, selected.DefaultParameters);
        Assert.True(analysis.IsComplete);
    }

    [Theory]
    [InlineData("pick(1, a = 2)")]
    [InlineData("pick(b = 1)")]
    [InlineData("pick(a = 1, 2)")]
    [InlineData("pick(a = 1, a = 2)")]
    [InlineData("pick()")]
    [InlineData("pick(1, 2)")]
    public void InvalidArgumentMappingsAreRejected(string call)
    {
        var analysis = Analyze("func pick(a: int): int = a\nfunc main(): () { " + call + " }");
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Assert.Single(analysis.Bindings).Status);
        Assert.Equal("SOL2131", Assert.Single(analysis.Diagnostics).Code);
    }

    [Fact]
    public void VarargExpansionSpreadAndNormalArityPreferenceAreDistinct()
    {
        var analysis = Analyze("func pick(a: int): int = a\nfunc pick(vararg a: int): bool = true\n"
            + "func main(items: []int): () { pick(1); pick(1, 2); pick(*items); pick() }");
        Assert.Equal("int", Call(analysis, "pick(1)").Selected!.ReturnType!.Name);
        Assert.True(Call(analysis, "pick(1, 2)").Selected!.Arguments.All(a => a.IsExpandedVararg));
        Assert.True(Assert.Single(Call(analysis, "pick(*items)").Selected!.Arguments).IsSpread);
        Assert.Empty(Call(analysis, "pick()").Selected!.Arguments);
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void FewerDefaultsAndNonGenericExactTieBreakersAreApplied()
    {
        var analysis = Analyze("func pick(a: int): int = a\nfunc pick(a: int, b: bool = true): bool = b\n"
            + "func <T> pick(a: T): T = a\nfunc main(): () { pick(1) }");
        var selected = Assert.Single(analysis.Bindings).Selected!;
        Assert.Equal(0, selected.Symbol.Arity);
        Assert.Single(selected.ParameterTypes);
    }

    [Fact]
    public void InferredGenericArgumentsSubstituteContainersResultsAndEffects()
    {
        var analysis = Analyze("class Box<T> {}\nclass Failure {}\nfunc <T, E> get(box: Box<T>, error: E): T throws E {}\n"
            + "func main(box: Box<int>, error: Failure): () { get(box, error) }");
        var selected = Assert.Single(analysis.Bindings).Selected!;
        Assert.Equal(new[] { "int", "app.Failure" }, selected.TypeArguments.Select(t => t.Name));
        Assert.Equal("int", selected.ReturnType!.Name);
        Assert.Equal("app.Failure", Assert.Single(selected.Effects).Name);
        Assert.Equal("int", Assert.Single(selected.ParameterTypes[0].Arguments).Name);
    }

    [Fact]
    public void ExplicitGenericArgumentsAndConstraintsFilterCandidates()
    {
        var analysis = Analyze("interface Named {}\nclass Good: Named {}\nclass Bad {}\nfunc <T: Named> take(value: T): T = value\n"
            + "func main(good: Good, bad: Bad): () { take<Good>(good); take<Bad>(bad); take<int, int>(1) }");
        Assert.Equal(CallBindingStatus.Bound, Call(analysis, "take<Good>(good)").Status);
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Call(analysis, "take<Bad>(bad)").Status);
        Assert.Contains("satisfy", Assert.Single(Call(analysis, "take<Bad>(bad)").Candidates).Reason);
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Call(analysis, "take<int, int>(1)").Status);
    }

    [Fact]
    public void RefOutRequireExactWritableLocalStorageAndExplicitMarkers()
    {
        var analysis = Analyze("func change(ref value: int): () {}\nfunc read(out value: int): bool = true\n"
            + "func main(): () { var writable: int = 1; val fixed: int = 1; var wide: long = 0; "
            + "change(ref writable); change(ref fixed); change(writable); change(ref wide); read(out var result) }");
        Assert.Equal(CallBindingStatus.Bound, Call(analysis, "change(ref writable)").Status);
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Call(analysis, "change(ref fixed)").Status);
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Call(analysis, "change(writable)").Status);
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Call(analysis, "change(ref wide)").Status);
        Assert.Equal(CallBindingStatus.Bound, Call(analysis, "read(out var result)").Status);
    }

    [Fact]
    public void ModuleAliasesBindButLocalShadowingCannotBecomeModuleQualification()
    {
        var analysis = Analyze("import api = library\nfunc main(): () { api.pick(1) }\nfunc shadow(api: int): () { api.pick(1) }",
            "module library\nfunc pick(value: int): int = value");
        Assert.Equal(new[] { CallBindingStatus.Bound, CallBindingStatus.Deferred }, analysis.Bindings.Select(b => b.Status));
    }

    [Fact]
    public void NestedCallsAndInferredLiteralLocalsCanSupplyArguments()
    {
        var analysis = Analyze("func pick(value: int): int = value\nfunc main(): () { val a = 1; pick(pick(a)) }");
        Assert.All(analysis.Bindings, b => Assert.Equal(CallBindingStatus.Bound, b.Status));
        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void IncomparableConversionsAreAmbiguousRegardlessOfDeclarationOrder()
    {
        foreach (bool reverse in new[] { false, true })
        {
            string[] functions = { "func pick(a: int, b: long): () {}", "func pick(a: long, b: int): () {}" };
            if (reverse) { Array.Reverse(functions); }
            var analysis = Analyze(string.Join('\n', functions) + "\nfunc main(): () { pick(1, 1) }");
            Assert.Equal(CallBindingStatus.Ambiguous, Assert.Single(analysis.Bindings).Status);
            Assert.Equal("SOL2132", Assert.Single(analysis.Diagnostics).Code);
        }
    }

    [Theory]
    [InlineData("byte", "256")]
    [InlineData("int", "2147483648")]
    [InlineData("uint", "4294967296")]
    [InlineData("ulong", "18446744073709551616")]
    [InlineData("byte", "0x100")]
    public void LiteralBoundsRejectOverflowWithoutImplicitTruncation(string type, string value)
    {
        var analysis = Analyze("func take(value: " + type + "): () {}\nfunc main(): () { take(" + value + ") }");
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Assert.Single(analysis.Bindings).Status);
    }

    [Fact]
    public void UnsupportedCandidatesBlockPrematureSelectionAndBodiesAreNotExecuted()
    {
        var analysis = Analyze("func pick(value: int): int = value\nfunc pick(value: long) = value\nfunc main(): () { pick(1) }");
        Assert.Equal(CallBindingStatus.Deferred, Assert.Single(analysis.Bindings).Status);
        Assert.False(analysis.IsComplete);
        Assert.False(analysis.HasErrors);
        Assert.Null(Assert.Single(analysis.Bindings).Selected);
    }

    [Theory]
    [InlineData("byte", "0xff")]
    [InlineData("short", "32767")]
    [InlineData("uint", "4294967295")]
    [InlineData("long", "9223372036854775807")]
    [InlineData("ulong", "18446744073709551615")]
    [InlineData("half", "1.5")]
    [InlineData("half", "65505")]
    [InlineData("single", "1.5")]
    [InlineData("double", "1")]
    [InlineData("int?", "1")]
    public void RepresentableContextualLiteralsBind(string type, string literal)
    {
        var analysis = Analyze("func accept(value: " + type + "): () {}\nfunc main(): () { accept(" + literal + ") }");
        Assert.Equal(CallBindingStatus.Bound, Assert.Single(analysis.Bindings).Status);
    }

    [Theory]
    [InlineData("debug_assert(true)")]
    [InlineData("size_of<int>()")]
    [InlineData("default<int>()")]
    public void CompilerIntrinsicsRemainDeferredInsteadOfLookingLikeMissingFunctions(string call)
    {
        var analysis = Analyze("func main(): () { " + call + " }");
        Assert.Equal(CallBindingStatus.Deferred, Assert.Single(analysis.Bindings).Status);
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void GenericInferenceDoesNotConfuseSameNamedParametersFromDifferentOwners()
    {
        var analysis = Analyze("func <T> identity(value: T): T = value\nfunc <T> outer(value: T): T = identity(value)");
        var selected = Assert.Single(analysis.Bindings).Selected!;
        var caller = analysis.Sources.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstFunction>().Single(f => f.Name == "outer");
        Assert.Same(Assert.Single(analysis.Sources.Types.GetTypeParameters(caller)), selected.ReturnType);
    }

    [Fact]
    public void Binary128ContextualLiteralBindingRemainsExplicitlyDeferred()
    {
        var analysis = Analyze("func accept(value: quad): () {}\nfunc main(): () { accept(1.5) }");
        Assert.Equal(CallBindingStatus.Deferred, Assert.Single(analysis.Bindings).Status);
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void VarargParameterHasManagedArrayTypeInsideItsBody()
    {
        var analysis = Analyze("func sink(vararg values: int): () {}\nfunc relay(vararg values: int): () { sink(*values) }");
        Assert.Equal(CallBindingStatus.Bound, Assert.Single(analysis.Bindings).Status);
    }
}
