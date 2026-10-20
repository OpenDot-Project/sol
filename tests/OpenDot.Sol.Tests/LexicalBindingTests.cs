// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LexicalBindingTests
{
    private static LexicalAnalysis Bind(string body)
    {
        return LexicalAnalysis.Create(AstTestSources.Session("module app\n" + body), TestContext.Current.CancellationToken);
    }

    private static NameBinding[] Named(LexicalAnalysis analysis, string name)
    {
        return analysis.Bindings.Where(binding => binding.Reference.Spelling == name)
            .OrderBy(binding => binding.Reference.Origin.Span.Start).ToArray();
    }

    [Fact]
    public void FunctionParametersBindWithIndependentGroupedNames()
    {
        var analysis = Bind("func add(a,b:int):int = a + b");
        Assert.False(analysis.HasErrors);
        Assert.Equal(LocalSymbolKind.Parameter, Assert.Single(Named(analysis, "a")).Lookup.Locals[0].Kind);
        Assert.Equal("b", Assert.Single(Named(analysis, "b")).Lookup.Locals[0].Name);
        Assert.NotEqual(Named(analysis, "a")[0].Lookup.Locals[0].Id, Named(analysis, "b")[0].Lookup.Locals[0].Id);
    }

    [Fact]
    public void LocalInitializerUsesTheOuterNameAndLaterReadsUseTheLocal()
    {
        var analysis = Bind("func f(x:int) { val x = x; use(x) }");
        var references = Named(analysis, "x");
        Assert.Equal(2, references.Length);
        Assert.Equal(LocalSymbolKind.Parameter, Assert.Single(references[0].Lookup.Locals).Kind);
        Assert.Equal(LocalSymbolKind.Variable, Assert.Single(references[1].Lookup.Locals).Kind);
    }

    [Fact]
    public void ADeclarationDoesNotBindAnEarlierUse()
    {
        var analysis = Bind("func f() { use(later); val later = 1; use(later) }");
        var references = Named(analysis, "later");
        Assert.True(references[0].Lookup.IsMissing);
        Assert.True(references[1].Lookup.IsLocal);
    }

    [Fact]
    public void InnerBlockBindingsDoNotEscapeToTheOuterBlock()
    {
        var analysis = Bind("func f() { if ready { val hidden = 1; use(hidden) }; use(hidden) }");
        var references = Named(analysis, "hidden");
        Assert.True(references[0].Lookup.IsLocal);
        Assert.True(references[1].Lookup.IsMissing);
    }

    [Fact]
    public void SameScopeDuplicatesAreDiagnosticsButNestedShadowingIsAllowed()
    {
        var duplicate = Bind("func f() { val x = 1; val x = 2 }");
        Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Code == "SOL2120");
        var nested = Bind("func f() { val x = 1; if ready { val x = 2; use(x) }; use(x) }");
        Assert.Empty(nested.Diagnostics);
        var references = Named(nested, "x");
        Assert.NotEqual(references[0].Lookup.Locals[0].Id, references[1].Lookup.Locals[0].Id);
    }

    [Fact]
    public void CatchBindingsAreVisibleOnlyWithinTheirHandler()
    {
        var analysis = Bind("func f() { try { work() } catch(error: E) { use(error) }; use(error) }");
        var references = Named(analysis, "error");
        Assert.Equal(LocalSymbolKind.Catch, Assert.Single(references[0].Lookup.Locals).Kind);
        Assert.True(references[1].Lookup.IsMissing);
    }

    [Fact]
    public void PayloadCaseBindingsDoNotLeakIntoSiblingCases()
    {
        var analysis = Bind("func f() { switch value { case Result.Value(item): use(item); default: use(item) } }");
        var references = Named(analysis, "item");
        Assert.Equal(LocalSymbolKind.Pattern, Assert.Single(references[0].Lookup.Locals).Kind);
        Assert.True(references[1].Lookup.IsMissing);
    }

    [Fact]
    public void ForEachBindingIsNotVisibleInItsIterableOrAfterTheLoop()
    {
        var analysis = Bind("func f(item:int) { for val item in range(item) { use(item) }; use(item) }");
        var references = Named(analysis, "item");
        Assert.Equal(LocalSymbolKind.Parameter, Assert.Single(references[0].Lookup.Locals).Kind);
        Assert.Equal(LocalSymbolKind.Variable, Assert.Single(references[1].Lookup.Locals).Kind);
        Assert.Equal(LocalSymbolKind.Parameter, Assert.Single(references[2].Lookup.Locals).Kind);
    }

    [Fact]
    public void ThreeClauseForOwnsItsInitializerAcrossAllClauses()
    {
        var analysis = Bind("func f() { for var i = 0; i < 5; i += 1 { use(i) }; use(i) }");
        var references = Named(analysis, "i");
        Assert.Equal(4, references.Length);
        Assert.All(references.Take(3), reference => Assert.True(reference.Lookup.IsLocal));
        Assert.True(references[3].Lookup.IsMissing);
    }

    [Fact]
    public void DestructuringDiscardsDoNotCreateSymbols()
    {
        var analysis = Bind("func f() { val (x, _) = pair; use(x) }");
        Assert.True(Assert.Single(Named(analysis, "x")).Lookup.IsLocal);
        Assert.DoesNotContain(analysis.Scopes.SelectMany(scope => scope.Symbols), symbol => symbol.Name == "_");
    }

    [Fact]
    public void OutVariableBindingRemainsVisibleInFollowingStatements()
    {
        var analysis = Bind("func f() { read(out var item); use(item) }");
        var symbol = Assert.Single(Assert.Single(Named(analysis, "item")).Lookup.Locals);
        Assert.True(symbol.IsMutable);
        Assert.Equal(LocalSymbolKind.Variable, symbol.Kind);
    }

    [Fact]
    public void LambdaParametersShadowOuterLocalsWithoutImplicitIt()
    {
        var analysis = Bind("func f(x:int) { val callback = { x -> x }; val other = { it } }");
        Assert.False(Assert.Single(Named(analysis, "x")).CapturesOuterLocal);
        Assert.True(Assert.Single(Named(analysis, "it")).Lookup.IsMissing);
    }

    [Fact]
    public void CapturesAreCandidatesWithoutPretendingToProveEscapeSafety()
    {
        var analysis = Bind("func f(x:int) { val callback = { x }; use(x) }");
        var references = Named(analysis, "x");
        Assert.True(references[0].CapturesOuterLocal);
        Assert.False(references[1].CapturesOuterLocal);
        Assert.Same(references[0].Lookup.Locals[0], references[1].Lookup.Locals[0]);
    }

    [Fact]
    public void LocalFunctionsCanCaptureOuterParameters()
    {
        var analysis = Bind("func outer(x:int) { func nested():int = x }");
        Assert.True(Assert.Single(Named(analysis, "x")).CapturesOuterLocal);
    }

    [Fact]
    public void AnonymousFunctionParametersDoNotBecomeOuterBindings()
    {
        var analysis = Bind("func f() { val callback = func(x:int):int { x }; use(x) }");
        var references = Named(analysis, "x");
        Assert.True(references[0].Lookup.IsLocal);
        Assert.False(references[0].CapturesOuterLocal);
        Assert.True(references[1].Lookup.IsMissing);
    }

    [Fact]
    public void LocalFunctionsSupportRecursionAfterTheirDeclarationStarts()
    {
        var analysis = Bind("func outer() { func local() { local() }; local() }");
        var references = Named(analysis, "local");
        Assert.Equal(2, references.Length);
        Assert.All(references, reference => Assert.Equal(LocalSymbolKind.Function, Assert.Single(reference.Lookup.Locals).Kind));
    }

    [Fact]
    public void LocalFunctionOverloadsRemainCandidatesForLaterCallBinding()
    {
        var analysis = Bind("func outer() { func local(x:int) {} func local(x:byte) {} local(1) }");
        Assert.Empty(analysis.Diagnostics);
        var candidates = Assert.Single(Named(analysis, "local")).Lookup.Locals;
        Assert.Equal(2, candidates.Length);
        Assert.All(candidates, candidate => Assert.Equal(LocalSymbolKind.Function, candidate.Kind));
    }

    [Fact]
    public void ModuleFunctionsRemainDeclarationSymbolsRatherThanLocals()
    {
        var analysis = Bind("func f() { g() }\nfunc g() {}");
        var binding = Assert.Single(Named(analysis, "g"));
        Assert.False(binding.Lookup.IsLocal);
        Assert.Equal(LookupScope.Module, binding.Lookup.Declarations.Scope);
    }

    [Fact]
    public void ConstructorOnlyParametersDoNotLeakIntoMethods()
    {
        var analysis = Bind("class C(seed:int, val value:int) { val initial = seed; init { use(seed) }; func f() { use(seed); use(value) } }");
        var seed = Named(analysis, "seed");
        Assert.Equal(3, seed.Length);
        Assert.True(seed[0].Lookup.IsLocal);
        Assert.True(seed[1].Lookup.IsLocal);
        Assert.True(seed[2].Lookup.IsMissing);
        Assert.Equal(LocalSymbolKind.ConstructorProperty, Assert.Single(Assert.Single(Named(analysis, "value")).Lookup.Locals).Kind);
    }

    [Fact]
    public void SetterValueIsAnExplicitContextBinding()
    {
        var analysis = Bind("class C { var count:int { get = 0; set = store(value) } }");
        var symbol = Assert.Single(Assert.Single(Named(analysis, "value")).Lookup.Locals);
        Assert.Equal(LocalSymbolKind.AccessorValue, symbol.Kind);
        Assert.Equal("int", symbol.WrittenType!.Shape.CanonicalName);
    }

    [Fact]
    public void ForeignReferencesAndCancelledConstructionAreRejected()
    {
        var first = Bind("func f(x:int) = x");
        var second = Bind("func f(x:int) = x");
        var foreign = Assert.Single(Named(second, "x")).Reference;
        Assert.Throws<ArgumentException>(() => first.GetBinding(foreign));
        Assert.Throws<ArgumentException>(() => first.Lookup(foreign, "x"));
        Assert.Throws<OperationCanceledException>(() => LexicalAnalysis.Create(first.Declarations, new CancellationToken(true)));
    }

    [Fact]
    public void TypeAndLexicalResultsShareOneExplicitDeclarationSession()
    {
        var declarations = AstTestSources.Session("module app\nfunc f(x:int):int = x");
        var analysis = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.False(analysis.HasErrors);
        Assert.Same(declarations, analysis.Types.Declarations);
        Assert.Same(declarations, analysis.Lexical.Declarations);
    }
}
