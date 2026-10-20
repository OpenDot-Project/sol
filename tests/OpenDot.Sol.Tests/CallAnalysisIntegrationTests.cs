// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class CallAnalysisIntegrationTests
{
    private static CallAnalysis Analyze(params string[] sources) => CallAnalysis.Create(
        SourceAnalysis.Create(AstTestSources.Session(sources), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

    [Fact]
    public void DriverAndInspectionPreserveSourceFilesWithoutPaths()
    {
        var compilation = FrontendCompilation.Parse(new[]
        {
            SourceText.FromString("module app\nfunc <T> identity(value: T): T = value", "C:/private/api.sol"),
            SourceText.FromString("module app\nfunc main(): () { identity(1) }", "C:/private/main.sol"),
        }, cancellationToken: TestContext.Current.CancellationToken);
        var analysis = compilation.AnalyzeCalls(new() { RequireCore = false }, TestContext.Current.CancellationToken);
        Assert.True(analysis.IsComplete);
        string json = CallBindingJson.Serialize(analysis, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.Equal(json, CallBindingJson.Serialize(analysis, TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("files").GetArrayLength());
        var call = Assert.Single(document.RootElement.GetProperty("calls").EnumerateArray());
        Assert.Equal(1, call.GetProperty("file").GetInt32());
        Assert.Equal("Bound", call.GetProperty("status").GetString());
        var candidate = Assert.Single(call.GetProperty("candidates").EnumerateArray());
        int returnId = candidate.GetProperty("returnType").GetInt32();
        Assert.Equal("int", document.RootElement.GetProperty("types")[returnId].GetProperty("name").GetString());
    }

    [Fact]
    public async Task InspectionSupportsConcurrentReadersAndForeignCallsAreRejected()
    {
        var analysis = Analyze("module app\nfunc f(value: int): int = value\nfunc main(): () { f(1) }");
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => CallBindingJson.Serialize(analysis,
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Equal(results[0], result));
        var other = Analyze("module app\nfunc main(): () { f(1) }");
        Assert.Throws<ArgumentException>(() => analysis.GetBinding(Assert.Single(other.Bindings).Call));
    }

    [Fact]
    public void UnknownLocalAndMemberCallsAreDeferredButUnknownDirectNameIsDiagnosed()
    {
        var analysis = Analyze("module app\nfunc main(callback: (int)->int, owner: int): () { callback(1); owner.f(1); missing(1) }");
        Assert.Equal(new[] { CallBindingStatus.Deferred, CallBindingStatus.Deferred, CallBindingStatus.Unresolved }, analysis.Bindings.Select(b => b.Status));
        Assert.Equal("SOL2130", Assert.Single(analysis.Diagnostics).Code);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void WrongArityIsRejectedBeforeUnsupportedArgumentTyping()
    {
        var analysis = Analyze("module app\nfunc f(value: int): int = value\nfunc f(value: int, other: int): int = other\n"
            + "func main(): () { f({ 1 }) }");
        var candidates = Assert.Single(analysis.Bindings).Candidates;
        Assert.Contains(candidates, c => c.Symbol.Parts[0] is AstFunction { Parameters.Length: 2 }
            && c.Status == CallCandidateStatus.Rejected);
    }

    [Fact]
    public void ConstraintFailureInsideSpecializedResultCannotProduceBoundCall()
    {
        var analysis = Analyze("module app\ninterface Named {}\nclass Box<T: Named> {}\nfunc <T> wrap(value: T): Box<T> {}\n"
            + "func main(): () { wrap(1) }");
        var binding = Assert.Single(analysis.Bindings);
        Assert.Equal(CallBindingStatus.NoApplicableCandidate, binding.Status);
        Assert.Contains("satisfy", Assert.Single(binding.Candidates).Reason);
    }

    [Fact]
    public void RefLikeSubstitutionCannotHideInsideOrdinaryGenericStorage()
    {
        var types = TypeAnalysis.Create(AstTestSources.Session("module app\nclass Box<T> {}\nref struct View {}\n"
            + "func <T> wrap(value: T): Box<T> where T: allows ref struct {}\nvar view: View"), TestContext.Current.CancellationToken);
        var function = types.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstFunction>().Single();
        var view = types.Types.Single(t => t.Name == "app.View");
        var workspace = new TypeSpecialization(types);
        var substitution = new Dictionary<ResolvedType, ResolvedType> { [Assert.Single(types.GetTypeParameters(function))] = view };
        var exception = Assert.Throws<TypeSpecializationException>(() => workspace.Substitute(types.GetType(function.ReturnType!),
            substitution, TestContext.Current.CancellationToken));
        Assert.Equal(SpecializationFailure.InvalidType, exception.Failure);
    }

    [Fact]
    public void ImportedAmbiguityCannotBeResolvedByChoosingDifferentModuleOverload()
    {
        var analysis = Analyze("module app\nimport a\nimport b\nfunc main(): () { f(1) }",
            "module a\nfunc f(value: int): int = value", "module b\nfunc f(value: bool): bool = value");
        Assert.Equal(CallBindingStatus.Ambiguous, Assert.Single(analysis.Bindings).Status);
        Assert.Null(Assert.Single(analysis.Bindings).Selected);
    }

    [Fact]
    public void DiagnosticBudgetsAndCancellationArePreserved()
    {
        var declarations = AnalysisSession.Create(new[] { AstTestSources.Parse("func main(): () { missing(1); another(2) }") },
            new() { RequireCore = false, DiagnosticLimit = 1 }, TestContext.Current.CancellationToken);
        var sources = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        var analysis = CallAnalysis.Create(sources, TestContext.Current.CancellationToken);
        Assert.Single(analysis.Diagnostics);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CallAnalysis.Create(sources, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => CallBindingJson.Serialize(analysis, cancellation.Token));
    }

    [Fact]
    public void ReferenceUpcastsNullableLiftingAndNullLiteralAreIntrinsic()
    {
        var analysis = Analyze("module app\nopen class Base {}\nclass Child: Base {}\nfunc accept(value: Base?): () {}\n"
            + "func main(child: Child): () { accept(child); accept(null) }");
        Assert.All(analysis.Bindings, b => Assert.Equal(CallBindingStatus.Bound, b.Status));
    }

    [Fact]
    public void DefaultIntrinsicAtStatementStartDoesNotTerminateSwitchCaseRecovery()
    {
        var analysis = Analyze("module app\nfunc main(value: int): () { switch value { case 1: default<int>(); default: default<int>() } }");
        Assert.False(analysis.Sources.HasErrors);
        Assert.Equal(2, analysis.Bindings.Length);
        Assert.All(analysis.Bindings, b => Assert.Equal(CallBindingStatus.Deferred, b.Status));
    }

    [Fact]
    public void ManagedArrayUpcastAndPrimitiveBoxingHaveDifferentRanks()
    {
        var analysis = Analyze("module std.core\nclass Object {}", "module app\nfunc accept(value: Object): () {}\n"
            + "func main(values: []int): () { accept(values); accept(1) }");
        Assert.All(analysis.Bindings, b => Assert.Equal(CallBindingStatus.Bound, b.Status));
        Assert.Equal(new[] { (int)IntrinsicConversion.Reference, (int)IntrinsicConversion.Boxing },
            analysis.Bindings.Select(b => Assert.Single(b.Selected!.ConversionRanks)));
    }

    [Fact]
    public void ConstructorDelegationRemainsOutsideDirectCallAnalysis()
    {
        var analysis = Analyze("module app\nclass Item(value: int) { constructor(): this(0) {} }");
        Assert.False(analysis.Sources.HasErrors);
        Assert.Empty(analysis.Diagnostics);
        Assert.Empty(analysis.Bindings);
    }
}
