// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FunctionValueOverloadTests
{
    private const string Core = "module std.core\n"
        + "open class Object {}\n"
        + "open class Animal: Object {}\n"
        + "class Dog: Animal {}\n"
        + "class Text: Object {}\n"
        + "class String: Object {}\n"
        + "open class Throwable: Object {}\n"
        + "open class Exception: Throwable {}\n"
        + "open class IOException: Exception {}";

    private static BodyAnalysis Analyze(params string[] modules)
    {
        var sources = SourceAnalysis.Create(AstTestSources.Session(modules.Append(Core).ToArray()),
            TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstFunction SelectedTarget(BodyAnalysis analysis, string name) =>
        Assert.Single(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == name)
            .FunctionValueTarget!;

    [Fact]
    public void ExpectedTypeSelectsTheExactCandidateOverAContravariantCandidate()
    {
        var analysis = Analyze("module app\n"
            + "func choose(value: Object): int = 1\n"
            + "func choose(value: Text): int = 2\n"
            + "func use(): () { val callback: (Text) -> int = choose }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var target = SelectedTarget(analysis, "choose");
        Assert.Equal("std.core.Text", analysis.Sources.Types.GetType(target.Parameters[0].Type!).Name);
        var binding = Assert.Single(analysis.Expressions, item => ReferenceEquals(item.FunctionValueTarget, target));
        var expectedSyntax = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstProperty>().Single(property => property.Name == "callback").Type!;
        var expected = analysis.Sources.Types.GetType(expectedSyntax);
        Assert.Equal(IntrinsicConversion.Identity,
            analysis.Types.ClassifyConversion(binding.Type!, expected, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExpectedTypeSelectsAQualifiedModuleOverload()
    {
        var analysis = Analyze("module helpers\n"
            + "func choose(value: Object): int = 1\n"
            + "func choose(value: Text): int = 2",
            "module app\nimport helpers\n"
            + "func use(): () { val callback: (Text) -> int = helpers.choose }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal("std.core.Text", analysis.Sources.Types.GetType(SelectedTarget(analysis, "choose")
            .Parameters[0].Type!).Name);
    }

    [Fact]
    public void EquallyRankedVarianceCompatibleOverloadsRemainAmbiguous()
    {
        var analysis = Analyze("module app\n"
            + "func choose(value: Object): Dog { for {} }\n"
            + "func choose(value: Animal): Animal { for {} }\n"
            + "func use(): () { val callback: (Dog) -> Animal = choose }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2212");
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "choose");
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void OverloadedFunctionValueWithoutExpectedSignatureStaysDeferred()
    {
        var analysis = Analyze("module app\n"
            + "func choose(value: Object): int = 1\n"
            + "func choose(value: Text): int = 2\n"
            + "func use(): () { val callback = choose }");

        Assert.False(analysis.HasErrors);
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "choose");
    }

    [Fact]
    public void NongenericCandidateWinsAnOtherwiseEqualGenericFunctionValueRank()
    {
        var analysis = Analyze("module app\n"
            + "func <T> choose(value: T): T = value\n"
            + "func choose(value: Text): Text = value\n"
            + "func use(): () { val callback: (Text) -> Text = choose }");

        Assert.False(analysis.HasErrors);
        Assert.True(analysis.IsComplete);
        Assert.Empty(SelectedTarget(analysis, "choose").TypeParameters);
    }

    [Fact]
    public void ExpectedFunctionSignatureClosesGenericFunctionValueArguments()
    {
        var analysis = Analyze("module app\n"
            + "func <T> identity(value: T): T = value\n"
            + "func use(): () { val callback: (Text) -> Text = identity }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
        var binding = Assert.Single(analysis.Expressions,
            item => item.FunctionValueTarget?.Name == "identity");
        Assert.Equal("std.core.Text", Assert.Single(binding.FunctionValueTypeArguments).Name);
        Assert.Equal("std.core.Text", Assert.Single(binding.Type!.Arguments.Take(1)).Name);
    }

    [Fact]
    public void InferredExpressionBodyResultParticipatesInFunctionValueSignature()
    {
        var analysis = Analyze("module app\n"
            + "func identity(value: String) = value\n"
            + "func use(): () { val callback: (String) -> String = identity }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
        Assert.Equal("std.core.String", analysis.GetBody(SelectedTarget(analysis, "identity"))!.ReturnType!.Name);
        Assert.Equal("std.core.String", Assert.Single(analysis.Expressions,
            binding => binding.FunctionValueTarget?.Name == "identity").Type!.Arguments[^1].Name);
    }

    [Fact]
    public void GenericExpressionBodyResultClosesFromTheExpectedFunctionType()
    {
        var analysis = Analyze("module app\n"
            + "func <T> identity(value: T) = value\n"
            + "func use(): () { val callback: (Text) -> Text = identity }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.True(analysis.IsComplete);
        var binding = Assert.Single(analysis.Expressions,
            item => item.FunctionValueTarget?.Name == "identity");
        Assert.Equal("std.core.Text", Assert.Single(binding.FunctionValueTypeArguments).Name);
        Assert.Equal("std.core.Text", binding.Type!.Arguments[^1].Name);
    }

    [Fact]
    public void StatelessObjectMethodCanInferItsFunctionValueResultType()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("object Identity {\n"
            + "func <T> identity(value: T) = value\n}"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> String = Identity.identity\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var reference = Assert.Single(compilation.Program.Program.Functions
            .SelectMany(function => function.Body?.Statements ?? [])
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        Assert.Equal("std.core.String", Assert.Single(compilation.Program.Program.Functions[reference.Target]
            .SpecializationArguments).Name);
    }

    [Fact]
    public void ExpectedFunctionSignatureUsesFunctionVarianceForGenericEvidence()
    {
        var analysis = Analyze("module app\n"
            + "func <T> identity(value: T): T = value\n"
            + "func use(): () { val callback: (Dog) -> Animal = identity }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var binding = Assert.Single(analysis.Expressions,
            item => item.FunctionValueTarget?.Name == "identity");
        Assert.Equal("std.core.Dog", Assert.Single(binding.FunctionValueTypeArguments).Name);
        var expected = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstProperty>().Single(property => property.Name == "callback").Type!;
        Assert.Equal(IntrinsicConversion.Reference,
            analysis.Types.ClassifyConversion(binding.Type!, analysis.Sources.Types.GetType(expected),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExpectedCheckedEffectBudgetClosesGenericFunctionValueEffects()
    {
        var analysis = Analyze("module app\n"
            + "func <E: Throwable> read(): String throws E = \"text\"\n"
            + "func use(): () { val callback: () -> String throws Exception = read }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var binding = Assert.Single(analysis.Expressions,
            item => item.FunctionValueTarget?.Name == "read");
        Assert.Equal("std.core.Exception", Assert.Single(binding.FunctionValueTypeArguments).Name);
        Assert.Equal("std.core.Exception", Assert.Single(binding.Type!.Effects).Name);
    }

    [Fact]
    public void GenericFunctionValueWithUninferredParameterRemainsDeferred()
    {
        var analysis = Analyze("module app\n"
            + "func <T> create(): int = 1\n"
            + "func use(): () { val callback: () -> int = create }");

        Assert.False(analysis.HasErrors);
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "create");
    }

    [Fact]
    public void UnresolvedGenericCandidateKeepsTheEntireFunctionValueOverloadGroupDeferred()
    {
        var analysis = Analyze("module app\n"
            + "func choose(): int = 1\n"
            + "func <T> choose(): int = 2\n"
            + "func use(): () { val callback: () -> int = choose }");

        Assert.False(analysis.HasErrors);
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "choose");
    }

    [Fact]
    public void GenericFunctionValueCandidateMustSatisfyItsClosedConstraints()
    {
        var analysis = Analyze("module app\n"
            + "func <T: Throwable> identity(value: T): T = value\n"
            + "func use(): () { val callback: (String) -> String = identity }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "identity");
    }

    [Fact]
    public void EquallyRankedClosedGenericFunctionValuesRemainAmbiguous()
    {
        var analysis = Analyze("module app\n"
            + "func <T> choose(value: T, suffix: String): T = value\n"
            + "func <U> choose(prefix: String, value: U): U = value\n"
            + "func use(): () { val callback: (String, String) -> String = choose }");

        Assert.True(analysis.Diagnostics.Any(diagnostic => diagnostic.Code == "SOL2212"),
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message))
            + " bindings=" + string.Join(',', analysis.Expressions.Select(binding =>
                binding.FunctionValueTarget?.Name + ":" + binding.Status)));
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "choose");
    }

    [Fact]
    public void ReceiverBoundMethodValueIsDeferredWithoutAClosureContract()
    {
        var analysis = Analyze("module app\n"
            + "class Service { func convert(value: Text): Text = value }\n"
            + "func use(service: Service): () { val callback: (Text) -> Text = service.convert }");

        Assert.False(analysis.HasErrors);
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "convert");
    }

    [Fact]
    public void GenericMethodValueOnAStatelessSingletonClosesFromItsExpectedType()
    {
        const string declarations = "object Identity { func <T> identity(value: T): T = value }\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> String = Identity.identity; val answer = callback(\"sol\")\n"
            + "} }";
        var analysis = Analyze("module app\n" + declarations);
        var methodReference = Assert.Single(analysis.Expressions,
            binding => binding.Expression is AstMember { Name: "identity" });
        Assert.Equal("identity", methodReference.FunctionValueTarget?.Name);
        Assert.Equal("std.core.String", Assert.Single(methodReference.FunctionValueTypeArguments).Name);

        var compilation = FirCompilation.Create([AstTestSources.Parse(declarations)], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + "@"
                + diagnostic.Span.Start + " " + diagnostic.Message)));
        var program = compilation.Program.Program;
        var reference = Assert.Single(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        var target = program.Functions[reference.Target];
        Assert.Equal("identity", target.Name);
        Assert.Equal("std.core.String", Assert.Single(target.SpecializationArguments).Name);
        Assert.Contains(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer),
            initializer => initializer is FirIndirectCall);
    }

    [Fact]
    public void StatelessSingletonMethodOverloadsUseTheExpectedFunctionType()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("object Convert {\n"
            + "func choose(value: int): String = \"number\"\n"
            + "func choose(value: String): String = value\n}\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> String = Convert.choose; val answer = callback(\"sol\")\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program.Program;
        var reference = Assert.Single(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        Assert.Equal("std.core.String", program.Functions[reference.Target].Parameters[0].Type.Name);
        Assert.Single(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
    }

    [Fact]
    public void GenericFactoryCanReturnAClosedStatelessSingletonMethodReference()
    {
        const string declarations = "object Identity { func <T> identity(value: T): T = value }\n"
            + "func <T> makeIdentity(): (T) -> T = Identity.identity\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> String = makeIdentity<String>(); val answer = callback(\"sol\")\n"
            + "} }";
        var compilation = FirCompilation.Create([AstTestSources.Parse(declarations)], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program.Program;
        var factory = Assert.Single(program.Functions,
            function => function.Name == "makeIdentity" && !function.SpecializationArguments.IsEmpty);
        var reference = Assert.Single(factory.Body!.Statements.OfType<FirReturn>()
            .Select(statement => statement.Value).OfType<FirFunctionReference>());
        Assert.Equal("identity", program.Functions[reference.Target].Name);
        Assert.Equal("std.core.String", Assert.Single(program.Functions[reference.Target].SpecializationArguments).Name);
        Assert.Contains(program.Functions.Single(function => function.Id == program.Entry).Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer),
            initializer => initializer is FirIndirectCall);
    }

    [Fact]
    public void StatefulSingletonMethodValuesRemainDeferred()
    {
        var analysis = Analyze("module app\n"
            + "object Stateful { val seed = 1; func <T> identity(value: T): T = value }\n"
            + "func use(): () { val callback: (String) -> String = Stateful.identity }");

        Assert.False(analysis.HasErrors);
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "identity");
    }

    [Fact]
    public void ImportsFromDifferentModulesDoNotBecomeOneFunctionValueOverloadSet()
    {
        var session = AstTestSources.Session([
            "module first\nfunc choose(value: Object): int = 1",
            "module second\nfunc choose(value: Text): int = 2",
            "module app\nimport first\nimport second\nfunc use(): () { val callback: (Text) -> int = choose }",
            Core]);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2002");
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "choose");
    }

    [Fact]
    public void FunctionValueGroupWithNoCompatibleCandidateReportsATypeError()
    {
        var analysis = Analyze("module app\n"
            + "func choose(value: String): String = value\n"
            + "func choose(value: Object): String = \"text\"\n"
            + "func use(): () { val callback: (int) -> int = choose }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "choose");
    }

    [Fact]
    public void VerifiedFirLowersANonCapturingFunctionValueToAnIndirectCall()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("object Main {\n"
            + "func main(args: []String): () { val callback: (int) -> int = identity; val answer = callback(41) }\n"
            + "}\n"
            + "func identity(value: int): int = value")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var verified = compilation.Program;
        var program = verified.Program;
        var reference = Assert.Single(program.Functions.Where(function => function.Body is not null).SelectMany(function => function.Body!.Statements)
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        Assert.Equal("identity", program.Functions[reference.Target].Name);
        var call = Assert.Single(program.Functions.Where(function => function.Body is not null).SelectMany(function => function.Body!.Statements)
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
        Assert.Equal("int", call.Type.Name);
        Assert.IsType<FirRead>(call.Function);
        Assert.Equal(0, Assert.Single(call.Arguments).Parameter);
        var callSite = Assert.Single(verified.FlowGraphs.Single(graph => graph.FunctionId == program.Entry).Blocks
            .SelectMany(block => block.Calls));
        Assert.Null(callSite.Target);

        string lowering = LoweringTrace.Inspect(compilation.Program, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("FunctionType", lowering, System.StringComparison.Ordinal);
        Assert.Contains("IndirectCall", lowering, System.StringComparison.Ordinal);
    }

    [Fact]
    public void IndirectRefAndOutCallsKeepExactWritableStorageAndFlowFacts()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func increment(ref value: int): () { value += 1 }\n"
            + "func initialize(out value: int): () { value = 42 }\n"
            + "object Main { func main(args: []String): () {\n"
            + "val step: (ref int) -> () = increment; var current = 0; step(ref current);\n"
            + "val seed: (out int) -> () = initialize; var output: int; seed(out output)\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var verified = compilation.Program;
        var program = verified.Program;
        var calls = program.Functions.Where(function => function.Body is not null).SelectMany(function => function.Body!.Statements)
            .OfType<FirEvaluate>().Select(evaluation => evaluation.Value).OfType<FirIndirectCall>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal(AstPassingMode.Ref, Assert.Single(calls[0].Arguments).Mode);
        Assert.Equal(AstPassingMode.Out, Assert.Single(calls[1].Arguments).Mode);
        var entryGraph = verified.FlowGraphs.Single(graph => graph.FunctionId == program.Entry);
        Assert.Contains(entryGraph.Blocks.SelectMany(block => block.Calls), site => site.Target is null);
        Assert.Contains(entryGraph.Blocks.SelectMany(block => block.Writes), slot => slot >= 0);
    }

    [Fact]
    public void IndirectCallCarriesItsDeclaredCheckedEffectsIntoFirFlow()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("import std.stdio\n"
            + "func identity(value: int): int throws IOException = value\n"
            + "object Main { func main(args: []String): () throws IOException {\n"
            + "val callback: (int) -> int throws IOException = identity; val value = callback(1)\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var verified = compilation.Program;
        var program = verified.Program;
        var call = Assert.Single(program.Functions.Where(function => function.Body is not null).SelectMany(function => function.Body!.Statements)
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
        Assert.Equal("std.stdio.IOException", Assert.Single(call.Function.Type.Effects).Name);
        var callSite = Assert.Single(verified.FlowGraphs.Single(graph => graph.FunctionId == program.Entry).Blocks
            .SelectMany(block => block.Calls));
        Assert.Null(callSite.Target);
        Assert.Equal("std.stdio.IOException", Assert.Single(callSite.Effects).Name);
        using var json = JsonDocument.Parse(FirJson.Serialize(verified, TestContext.Current.CancellationToken));
        Assert.Equal(7, json.RootElement.GetProperty("schema").GetInt32());
        Assert.Contains("\"kind\": \"FirIndirectCall\"", json.RootElement.GetRawText(), System.StringComparison.Ordinal);
        Assert.Contains("\"target\": null", json.RootElement.GetRawText(), System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"Target\"", json.RootElement.GetRawText(), System.StringComparison.Ordinal);
        var serializedType = Assert.Single(json.RootElement.GetProperty("types").EnumerateArray(),
            item => item.GetProperty("id").GetInt32() == call.Function.Type.Id);
        Assert.Equal(["Value"], serializedType.GetProperty("parameterModes").EnumerateArray()
            .Select(mode => mode.GetString()).ToArray());
        Assert.Equal(call.Function.Type.Effects.Select(effect => effect.Id),
            serializedType.GetProperty("effects").EnumerateArray().Select(effect => effect.GetInt32()));
    }

    [Fact]
    public void FunctionValuesPassThroughParametersAndReturnValues()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func identity(value: int): int = value\n"
            + "func provide(): (int) -> int = identity\n"
            + "func <T> apply(value: T, callback: (T) -> T): T = callback(value)\n"
            + "object Main { func main(args: []String): () { val answer = apply(41, provide()) } }")],
            TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var verified = compilation.Program;
        var program = verified.Program;
        var returnedReference = Assert.IsType<FirFunctionReference>(Assert.IsType<FirReturn>(
            Assert.Single(program.Functions.Single(function => function.Name == "provide").Body!.Statements)).Value);
        Assert.Equal("identity", program.Functions[returnedReference.Target].Name);
        var specializedApply = Assert.Single(program.Functions.Where(function => function.Name == "apply"));
        Assert.Equal("int", Assert.Single(specializedApply.SpecializationArguments).Name);
        var indirect = Assert.IsType<FirIndirectCall>(Assert.IsType<FirReturn>(
            Assert.Single(specializedApply.Body!.Statements)).Value);
        Assert.IsType<FirRead>(indirect.Function);
        Assert.Contains(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer), initializer => initializer is FirCall);
    }

    [Fact]
    public void NativeFirResolvesAnOverloadedReferenceBeforeLoweringTheCall()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func choose(value: int): int = 1\n"
            + "func choose(value: String): int = 2\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> int = choose; val answer = callback(\"sol\")\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var verified = compilation.Program;
        var program = verified.Program;
        var reference = Assert.Single(program.Functions.Where(function => function.Name == "main")
            .SelectMany(function => function.Body!.Statements).OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        Assert.Equal("choose", program.Functions[reference.Target].Name);
        Assert.Equal("std.core.String", program.Functions[reference.Target].Parameters[0].Type.Name);
        Assert.Single(program.Functions.Where(function => function.Name == "main")
            .SelectMany(function => function.Body!.Statements).OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
    }

    [Fact]
    public void NativeFirSpecializesGenericFunctionValueAndTracksNestedReferenceReachability()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func <T> identity(value: T): T = value\n"
            + "func <T> makeIdentity(): (T) -> T = identity\n"
            + "object Main { func main(args: []String): () {\n"
            + "val callback: (String) -> String = makeIdentity<String>(); val answer = callback(\"sol\")\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program.Program;
        var factory = Assert.Single(program.Functions,
            function => function.Name == "makeIdentity" && !function.SpecializationArguments.IsEmpty);
        var identityReference = Assert.Single(factory.Body!.Statements.OfType<FirReturn>()
            .Select(statement => statement.Value).OfType<FirFunctionReference>());
        var identity = program.Functions[identityReference.Target];
        Assert.Equal("identity", identity.Name);
        Assert.Equal("std.core.String", Assert.Single(identity.SpecializationArguments).Name);
        var sourceFactory = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>().Single(function => function.Name == "makeIdentity");
        var specializedFactory = program.Analysis.GetGenericBody(sourceFactory, factory.SpecializationArguments,
            TestContext.Current.CancellationToken);
        var binding = Assert.Single(specializedFactory.Expressions,
            expression => expression.FunctionValueTarget?.Name == "identity");
        Assert.Equal(identity.SpecializationArguments, binding.FunctionValueTypeArguments);
        Assert.Contains(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer),
            initializer => initializer is FirIndirectCall);
    }

    [Fact]
    public void NativeFirSpecializesGenericVarargFunctionValueAsAnArrayParameter()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func <T> first(vararg values: T): T = values[0]\n"
            + "object Main { func main(args: []String): () {\n"
            + "val firstText: ([]String) -> String = first; val answer = firstText([\"sol\"])\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var program = compilation.Program.Program;
        var reference = Assert.Single(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        var target = program.Functions[reference.Target];
        Assert.Equal("std.core.String", Assert.Single(target.SpecializationArguments).Name);
        var parameter = Assert.Single(target.Parameters);
        Assert.Equal(ResolvedTypeKind.Array, parameter.Type.Kind);
        Assert.Equal("std.core.String", parameter.Type.Arguments[0].Name);
        Assert.Equal(AstPassingMode.Value, parameter.Mode);
        var call = Assert.Single(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirIndirectCall>());
        Assert.IsType<FirArrayLiteral>(Assert.Single(call.Arguments).Value);
    }

    [Fact]
    public void NativeFirSpecializesGenericOutFunctionValueAndTracksItsDefiniteWrite()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func <T> set(out target: T, value: T): () { target = value }\n"
            + "object Main { func main(args: []String): () {\n"
            + "val initialize: (out String, String) -> () = set; var result: String; initialize(out result, \"sol\"); val answer = result\n"
            + "} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var verified = compilation.Program;
        var program = verified.Program;
        var reference = Assert.Single(program.Functions.Single(function => function.Name == "main").Body!.Statements
            .OfType<FirDeclare>().Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        var target = program.Functions[reference.Target];
        Assert.Equal("std.core.String", Assert.Single(target.SpecializationArguments).Name);
        Assert.Equal(AstPassingMode.Out, target.Parameters[0].Mode);
        Assert.Equal("std.core.String", target.Parameters[0].Type.Name);
        var main = program.Functions.Single(function => function.Id == program.Entry);
        int resultSlot = Assert.Single(main.Slots, slot => slot.Name == "result").Id;
        Assert.Contains(verified.FlowGraphs.Single(graph => graph.FunctionId == program.Entry).Blocks,
            block => block.Writes.Contains(resultSlot));
    }

    [Fact]
    public void NativeFirAdmitsNonCapturingLambdasAndArraysOfFunctionPointers()
    {
        var closure = FirCompilation.Create([AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val callback: () -> int = { 42 }\n} }")], TestContext.Current.CancellationToken);
        Assert.NotNull(closure.Program);
        Assert.Empty(closure.Diagnostics);

        var array = FirCompilation.Create([AstTestSources.Parse("object Main { func main(args: []String) {\n"
            + "val callbacks: [](() -> int) = [answer]\nval result = callbacks[0]()\n} }\n"
            + "func answer(): int = 42")], TestContext.Current.CancellationToken);
        Assert.NotNull(array.Program);
        Assert.Empty(array.Diagnostics);
    }

    [Fact]
    public void BottomReturningIndirectCallTerminatesWithoutEmittingAValueReturn()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("func terminate(callback: () -> !): ! = callback()\n"
            + "object Main { func main(args: []String): () {} }")], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null,
            string.Join('\n', compilation.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var verified = compilation.Program;
        var function = verified.Program.Functions.Single(candidate => candidate.Name == "terminate");
        var returned = Assert.IsType<FirReturn>(Assert.Single(function.Body!.Statements));
        var call = Assert.IsType<FirIndirectCall>(returned.Value);
        Assert.Equal(ResolvedTypeKind.Bottom, call.Type.Kind);
        string trace = LoweringTrace.Inspect(verified, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("IndirectCall", trace, System.StringComparison.Ordinal);
        Assert.Contains("Unreachable", trace, System.StringComparison.Ordinal);
    }
}
