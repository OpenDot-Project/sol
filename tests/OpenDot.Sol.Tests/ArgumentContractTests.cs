// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ArgumentContractTests
{
    private static SourceAnalysis Sources(string source) => SourceAnalysis.Create(AstTestSources.Session("module app\n" + source),
        TestContext.Current.CancellationToken);
    private static CallAnalysis Calls(string source)
    {
        var analysis = Sources(source);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        return CallAnalysis.Create(analysis, TestContext.Current.CancellationToken);
    }
    private static AstFunction Function(SourceAnalysis analysis, string owner) => analysis.Declarations.Files.SelectMany(f => f.Nodes)
        .OfType<AstTypeDeclaration>().Single(t => t.Name == owner).Members.OfType<AstFunction>().Single();

    [Theory]
    [InlineData("f(a = 1, 2, 3)")]
    [InlineData("f(1, b = 2, 3)")]
    [InlineData("f(a = 1, 2, c = 3)")]
    [InlineData("f(c = 3, b = 2, a = 1)")]
    public void OrderedMixedArgumentsAndFullyNamedReorderingAreAccepted(string call)
    {
        var analysis = Calls("func f(a,b,c:int): () {}\nfunc main(): () { " + call + " }");
        Assert.Equal(CallBindingStatus.Bound, Assert.Single(analysis.Bindings).Status);
    }

    [Theory]
    [InlineData("f(c = 3, 1, 2)")]
    [InlineData("f(a = 1, c = 3, 2)")]
    [InlineData("f(b = 2, a = 1, 3)")]
    public void PositionalArgumentsCannotFollowSkippedOrReorderedParameters(string call)
    {
        var analysis = Calls("func f(a,b,c:int): () {}\nfunc main(): () { " + call + " }");
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, Assert.Single(analysis.Bindings).Status);
    }

    [Fact]
    public void MiddleVarargConsumesPositionalsAndLaterParametersAreNamed()
    {
        var analysis = Calls("func f(head:int,vararg values:int,done:bool): () {}\n"
            + "func main(): () { f(1,2,3,done = true); f(1,done = true); f(1,2,true) }");
        Assert.Equal(new[] { CallBindingStatus.Bound, CallBindingStatus.Bound, CallBindingStatus.NoApplicableCandidate }, analysis.Bindings.Select(b => b.Status));
        Assert.Equal(new[] { 0, 1, 1, 2 }, analysis.Bindings[0].Selected!.Arguments.Select(a => a.ParameterIndex));
        Assert.Equal(new[] { 1 }, analysis.Bindings[1].Selected!.EmptyVarargParameters);
    }

    [Theory]
    [InlineData("f(values = items, done = true)", CallBindingStatus.Bound)]
    [InlineData("f(values = *items, done = true)", CallBindingStatus.Bound)]
    [InlineData("f(values = 1, done = true)", CallBindingStatus.NoApplicableCandidate)]
    [InlineData("f(values = items, true)", CallBindingStatus.NoApplicableCandidate)]
    [InlineData("f(1, values = items, done = true)", CallBindingStatus.NoApplicableCandidate)]
    public void NamedVarargRequiresAnArrayAndCannotMixWithExpandedElements(string call, CallBindingStatus expected)
    {
        var analysis = Calls("func <T> f(vararg values:T,done:bool): () {}\nfunc main(items:[]int): () { " + call + " }");
        var binding = Assert.Single(analysis.Bindings);
        Assert.Equal(expected, binding.Status);
        if (binding.Selected is { } selected)
        {
            Assert.True(selected.Arguments[0].IsArrayArgument);
            Assert.False(selected.Arguments[0].IsExpandedVararg);
            Assert.Equal("int", Assert.Single(selected.TypeArguments).Name);
        }
    }

    [Fact]
    public void MultipleSpreadsRemainInSourceEvaluationOrder()
    {
        var analysis = Calls("func f(vararg values:int): () {}\nfunc main(items:[]int): () { f(1,*items,2,*items,3) }");
        var arguments = Assert.Single(analysis.Bindings).Selected!.Arguments;
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, arguments.Select(a => a.ArgumentIndex));
        Assert.Equal(new[] { false, true, false, true, false }, arguments.Select(a => a.IsSpread));
    }

    [Fact]
    public void VarargDefaultIsUsedOnlyWhenNoArgumentWasSupplied()
    {
        var analysis = Calls("func source(): []int {}\nfunc f(vararg values:int = source()): () {}\n"
            + "func main(items:[]int): () { f(); f(1); f(values = items) }");
        var calls = analysis.Bindings.Where(b => b.Call.Target?.Origin.Text == "f").ToArray();
        Assert.Equal(new[] { 0 }, calls[0].Selected!.DefaultParameters);
        Assert.Empty(calls[0].Selected!.EmptyVarargParameters);
        Assert.Equal("source()", Assert.Single(calls[0].Selected!.DefaultArguments).Expression.Origin.Text);
        Assert.Empty(calls[1].Selected!.DefaultArguments);
        Assert.Empty(calls[2].Selected!.DefaultArguments);
    }

    [Theory]
    [InlineData("f() { 1 }")]
    [InlineData("f { 1 }")]
    [InlineData("f(a = 2) { 1 }")]
    [InlineData("f(2,3) { 1 }")]
    public void TrailingLambdaAlwaysMapsToFinalParameterAcrossDefaultsAndVararg(string call)
    {
        var analysis = Calls("func f(a:int = 1,vararg values:int,body:()->int): () {}\nfunc main(): () { " + call + " }");
        var binding = Assert.Single(analysis.Bindings);
        Assert.Equal(CallBindingStatus.Deferred, binding.Status);
        var candidate = Assert.Single(binding.Candidates);
        var last = candidate.Arguments[^1];
        Assert.Equal(2, last.ParameterIndex);
        Assert.True(last.IsTrailingLambda);
        Assert.True(binding.Call.Arguments[^1].IsTrailingLambda);
        if (call.StartsWith("f()", StringComparison.Ordinal) || call.StartsWith("f {", StringComparison.Ordinal))
        {
            Assert.Equal(new[] { 0 }, candidate.DefaultParameters);
            Assert.Equal(new[] { 1 }, candidate.EmptyVarargParameters);
        }
    }

    [Fact]
    public void LambdaInsideParenthesesIsNotReorderedPastDefaults()
    {
        var analysis = Calls("func f(a:int = 1,body:()->int): () {}\nfunc main(): () { f({ 1 }); f(body = { 1 }) }");
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, analysis.Bindings[0].Status);
        Assert.Equal(CallBindingStatus.Deferred, analysis.Bindings[1].Status);
        Assert.False(analysis.Bindings[1].Call.Arguments[0].IsTrailingLambda);
    }

    [Fact]
    public void ExplicitGenericCallCanOmitParenthesesBeforeTrailingLambda()
    {
        var analysis = Calls("func <T> f(body:()->T): () {}\nfunc main(): () { f<int> { 1 } }");
        var binding = Assert.Single(analysis.Bindings);
        Assert.IsType<AstGenericApplication>(binding.Call.Target);
        Assert.True(Assert.Single(binding.Call.Arguments).IsTrailingLambda);
        Assert.Equal(CallBindingStatus.Deferred, binding.Status);
    }

    [Fact]
    public void DefaultsBindEarlierParametersWithoutBindingSelfOrLaterParameters()
    {
        var valid = Sources("func f(a:int,b:int = a): () {}");
        Assert.False(valid.HasErrors);
        var reference = Assert.Single(valid.Lexical.Bindings);
        Assert.Equal("a", Assert.Single(reference.Lookup.Locals).Name);
        var invalid = Sources("func f(a:int = b,b:int = b): () {}");
        Assert.Equal(2, invalid.Defaults.Diagnostics.Length);
        Assert.All(invalid.Defaults.Diagnostics, d => Assert.Equal("SOL2136", d.Code));
    }

    [Fact]
    public void DefaultLambdaParameterCanShadowAnUnavailableParameterName()
    {
        var analysis = Sources("func f(a:(int)->int = { a:int -> a }): () {}");
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void OverrideUsesOriginalDefaultExpressionAndCannotRedefineIt()
    {
        var analysis = Sources("open class Base { open func f(value:int = 1): int = value }\n"
            + "class Child: Base { override func f(value:int): int = value }");
        Assert.False(analysis.HasErrors);
        var inherited = analysis.Defaults.GetDefaults(Function(analysis, "Child"));
        Assert.True(inherited.IsComplete);
        Assert.Same(Function(analysis, "Base"), Assert.Single(inherited.Parameters)!.DeclaredBy);
        Assert.Equal("1", inherited.Parameters[0]!.Expression.Origin.Text);
        var invalid = Sources("open class Base { open func f(value:int = 1): int = value }\n"
            + "class Child: Base { override func f(value:int = 2): int = value }");
        Assert.Contains(invalid.Diagnostics, d => d.Code == "SOL2135");
    }

    [Fact]
    public void GenericBaseSubstitutionAndMethodParameterRenamingPreserveDefaults()
    {
        var analysis = Sources("open class Base<T> { open func f(value:T,count:int = 2): T = value }\n"
            + "class Child: Base<int> { override func f(item:int,count:int): int = item }");
        Assert.False(analysis.HasErrors);
        Assert.Equal("2", analysis.Defaults.GetDefaults(Function(analysis, "Child")).Parameters[1]!.Expression.Origin.Text);
    }

    [Fact]
    public void ConflictingInheritedDefaultOriginsAreDiagnosed()
    {
        var analysis = Sources("interface A { func f(value:int = 1): int }\ninterface B { func f(value:int = 2): int }\n"
            + "class Child: A,B { override func f(value:int): int = value }");
        Assert.Contains(analysis.Diagnostics, d => d.Code == "SOL2137");
        Assert.False(analysis.Defaults.GetDefaults(Function(analysis, "Child")).IsComplete);
    }

    [Fact]
    public void DefaultEvaluationPlanPreservesDeclarationOrderAndOriginalExpressions()
    {
        var analysis = Calls("func mark(value:int): int = value\nfunc f(a:int = mark(1),b:int = a,c:int = mark(3)): () {}\n"
            + "func main(): () { f(c = mark(4)) }");
        var call = analysis.Bindings.Single(b => b.Call.Target?.Origin.Text == "f");
        Assert.Equal(new[] { 0, 1 }, call.Selected!.DefaultArguments.Select(d => d.ParameterIndex));
        Assert.Equal(new[] { "mark(1)", "a" }, call.Selected.DefaultArguments.Select(d => d.Expression.Origin.Text));
        using var json = JsonDocument.Parse(CallBindingJson.Serialize(analysis, TestContext.Current.CancellationToken));
        Assert.Contains(json.RootElement.GetProperty("calls").EnumerateArray(), entry => entry.GetProperty("candidates").EnumerateArray()
            .Any(candidate => candidate.GetProperty("defaultExpressions").GetArrayLength() == 2));
    }

    [Theory]
    [InlineData("true", CallBindingStatus.NoApplicableCandidate)]
    [InlineData("2147483648", CallBindingStatus.NoApplicableCandidate)]
    [InlineData("1 + 2", CallBindingStatus.Deferred)]
    [InlineData("42", CallBindingStatus.Bound)]
    public void SelectedDefaultsRequireSupportedCompatibleTyping(string expression, CallBindingStatus expected)
    {
        var analysis = Calls("func f(value:int = " + expression + "): () {}\nfunc main(): () { f() }");
        Assert.Equal(expected, Assert.Single(analysis.Bindings).Status);
    }

    [Fact]
    public void DiamondInheritanceRetainsOneOriginalDefaultWithoutAmbiguity()
    {
        var analysis = Sources("interface Root { func f(value:int = 1): int }\ninterface Left: Root {}\ninterface Right: Root {}\n"
            + "class Child: Left,Right { override func f(value:int): int = value }");
        Assert.False(analysis.HasErrors);
        var defaults = analysis.Defaults.GetDefaults(Function(analysis, "Child"));
        Assert.True(defaults.IsComplete);
        Assert.Same(Function(analysis, "Root"), Assert.Single(defaults.Parameters)!.DeclaredBy);
    }

    [Fact]
    public void GenericMethodParameterIdentitiesAreMatchedByOrdinalForDefaults()
    {
        var analysis = Sources("open class Base { open func <T> f(value:T,count:int = 2): T = value }\n"
            + "class Child: Base { override func <U> f(value:U,count:int): U = value }");
        Assert.False(analysis.HasErrors);
        var defaults = analysis.Defaults.GetDefaults(Function(analysis, "Child"));
        Assert.True(defaults.IsComplete);
        Assert.Equal("2", defaults.Parameters[1]!.Expression.Origin.Text);
    }

    [Fact]
    public void DefaultQueriesRejectForeignOwnershipAndObserveCancellation()
    {
        var first = Sources("func f(value:int = 1): () {}");
        var second = Sources("func f(value:int = 2): () {}");
        var foreign = second.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstFunction>().Single();
        Assert.Throws<ArgumentException>(() => first.Defaults.GetDefaults(foreign));
        Assert.Throws<ArgumentException>(() => DefaultValueAnalysis.Create(first.Types, second.Lexical, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DefaultValueAnalysis.Create(first.Types, first.Lexical, cancellation.Token));
    }
}
