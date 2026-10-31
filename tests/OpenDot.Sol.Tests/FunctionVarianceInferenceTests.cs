// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FunctionVarianceInferenceTests
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

    private static TypeAnalysis AnalyzeTypes(string source)
    {
        var analysis = TypeAnalysis.Create(AstTestSources.Session([Core, "module app\n" + source]),
            TestContext.Current.CancellationToken);
        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return analysis;
    }

    private static BodyAnalysis AnalyzeBodies(string source)
    {
        var sources = SourceAnalysis.Create(AstTestSources.Session(["module app\n" + source, Core]),
            TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static ResolvedType LocalType(TypeAnalysis analysis, string name) =>
        analysis.GetType(analysis.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstProperty>().Single(property => property.Name == name).Type!);

    [Fact]
    public void FunctionValuesAreContravariantInValueParametersAndCovariantInResults()
    {
        var analysis = AnalyzeTypes("var root: Object\nvar text: Text\nvar animal: Animal\nvar dog: Dog\nvar number: int");
        var types = new TypeSpecialization(analysis);
        var root = LocalType(analysis, "root");
        var text = LocalType(analysis, "text");
        var animal = LocalType(analysis, "animal");
        var dog = LocalType(analysis, "dog");
        var source = types.GetFunction([root], dog, [AstPassingMode.Value]);
        var target = types.GetFunction([text], animal, [AstPassingMode.Value]);

        Assert.Equal(IntrinsicConversion.Reference,
            types.ClassifyConversion(source, target, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(target, source, TestContext.Current.CancellationToken));

        var narrowSource = types.GetFunction([text], dog, [AstPassingMode.Value]);
        var broadTarget = types.GetFunction([root], animal, [AstPassingMode.Value]);
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(narrowSource, broadTarget, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NestedFunctionParametersReverseTheDirectionAgain()
    {
        var analysis = AnalyzeTypes("var root: Object\nvar text: Text\nvar dog: Dog\nvar number: int");
        var types = new TypeSpecialization(analysis);
        var root = LocalType(analysis, "root");
        var text = LocalType(analysis, "text");
        var dog = LocalType(analysis, "dog");
        var number = LocalType(analysis, "number");
        var narrowInput = types.GetFunction([text], dog, [AstPassingMode.Value]);
        var broadInput = types.GetFunction([root], dog, [AstPassingMode.Value]);
        var source = types.GetFunction([narrowInput], number, [AstPassingMode.Value]);
        var target = types.GetFunction([broadInput], number, [AstPassingMode.Value]);

        Assert.Equal(IntrinsicConversion.Reference,
            types.ClassifyConversion(source, target, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(target, source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void NominalGenericArgumentsStayInvariantInsideFunctionParameters()
    {
        var analysis = AnalyzeTypes("class Box<T> {}\nvar animalBox: Box<Animal>\nvar dogBox: Box<Dog>");
        var types = new TypeSpecialization(analysis);
        var animalBox = LocalType(analysis, "animalBox");
        var dogBox = LocalType(analysis, "dogBox");
        var empty = types.GetTuple([]);
        var source = types.GetFunction([animalBox], empty, [AstPassingMode.Value]);
        var target = types.GetFunction([dogBox], empty, [AstPassingMode.Value]);

        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(source, target, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(target, source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FunctionResultNullabilityUsesTheOrdinaryCovariantConversion()
    {
        var analysis = AnalyzeTypes("var animal: Animal\nvar dog: Dog\nvar value: String");
        var types = new TypeSpecialization(analysis);
        var animal = LocalType(analysis, "animal");
        var dog = LocalType(analysis, "dog");
        var value = LocalType(analysis, "value");
        var nullableValue = types.GetNullable(value);
        var source = types.GetFunction([animal], value, [AstPassingMode.Value]);
        var target = types.GetFunction([dog], nullableValue, [AstPassingMode.Value]);

        Assert.Equal(IntrinsicConversion.Reference,
            types.ClassifyConversion(source, target, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(target, source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefAndOutFunctionParametersRequireExactStorageIdentity()
    {
        var analysis = AnalyzeTypes("var root: Object\nvar text: Text");
        var types = new TypeSpecialization(analysis);
        var root = LocalType(analysis, "root");
        var text = LocalType(analysis, "text");
        var empty = types.GetTuple([]);
        var sourceRef = types.GetFunction([root], empty, [AstPassingMode.Ref]);
        var targetRef = types.GetFunction([text], empty, [AstPassingMode.Ref]);
        var sourceOut = types.GetFunction([root], empty, [AstPassingMode.Out]);
        var targetOut = types.GetFunction([text], empty, [AstPassingMode.Out]);
        var mismatchedMode = types.GetFunction([root], empty, [AstPassingMode.Out]);

        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(sourceRef, targetRef, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(sourceOut, targetOut, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(sourceRef, mismatchedMode, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.Identity,
            types.ClassifyConversion(sourceRef, sourceRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FunctionEffectsRemainASetThatCanNarrowButNotExpand()
    {
        var analysis = AnalyzeTypes("var animal: Animal\nvar dog: Dog\nvar failure: IOException\nvar allowed: Exception");
        var types = new TypeSpecialization(analysis);
        var animal = LocalType(analysis, "animal");
        var dog = LocalType(analysis, "dog");
        var failure = LocalType(analysis, "failure");
        var allowed = LocalType(analysis, "allowed");
        var source = types.GetFunction([], dog, [], [failure]);
        var target = types.GetFunction([], animal, [], [allowed]);
        var pure = types.GetFunction([], dog, []);
        var permitsFailure = types.GetFunction([], animal, [], [failure]);

        Assert.Equal(IntrinsicConversion.Reference,
            types.ClassifyConversion(source, target, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(target, source, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.Reference,
            types.ClassifyConversion(pure, permitsFailure, TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None,
            types.ClassifyConversion(permitsFailure, pure, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OneBroadEffectBudgetCoversSeveralNarrowSourceEffects()
    {
        var analysis = AnalyzeBodies("class DiskFailure: IOException {}\n"
            + "class NetworkFailure: IOException {}\n"
            + "func readBoth(): String throws DiskFailure, NetworkFailure = \"value\"\n"
            + "func consume(callback: () -> String throws IOException): String throws IOException = callback()\n"
            + "func use(): String throws IOException = consume(readBoth)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Contains(analysis.Calls, binding => binding.Target.Name == "consume");
    }

    [Fact]
    public void GenericInferenceUsesBothDirectionsForANamedFunctionValue()
    {
        var analysis = AnalyzeBodies("func <T> apply(callback: (T) -> T): T = callback(\"seed\")\n"
            + "func transform(value: Object): String = \"text\"\n"
            + "func use(): String = apply(transform)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void GenericInferenceCombinesLowerAndUpperBoundsFromCallbackShape()
    {
        var analysis = AnalyzeBodies("func <T> apply(value: T, callback: (T) -> T): T = callback(value)\n"
            + "func use(value: Text, callback: (Object) -> Text): Text = apply(value, callback)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Equal("std.core.Text", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void ExpectedFunctionResultInfersInTheOppositeParameterDirection()
    {
        var analysis = AnalyzeBodies("func <T> make(): (T) -> T = { value: T -> value }\n"
            + "func use(): (Text) -> Object = make()");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Equal("std.core.Text", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void ExpectedFunctionResultInfersItsCheckedEffectBudget()
    {
        var analysis = AnalyzeBodies("func <E: Throwable> make(): () -> String throws E = { \"text\" }\n"
            + "func use(): () -> Object throws Exception = make()");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "make");
        Assert.Equal("std.core.Exception", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NestedCallbackEffectEvidenceReversesAtTheOuterParameterBoundary()
    {
        var analysis = AnalyzeBodies("func <E: Throwable> accept(callback: (() -> String throws E) -> ()): () = ()\n"
            + "func store(callback: () -> String throws Exception): () = ()\n"
            + "func use(): () = accept(store)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "accept");
        Assert.Equal("std.core.Exception", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void NestedFunctionParameterInferenceFlipsVarianceAtEveryBoundary()
    {
        var analysis = AnalyzeBodies("func <T> accept(callback: ((T) -> Dog) -> int): int = 1\n"
            + "func use(callback: ((Object) -> Dog) -> int): int = accept(callback)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "accept");
        Assert.Equal("std.core.Object", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void IncompatibleLowerAndCallbackUpperBoundsRejectTheCandidate()
    {
        var analysis = AnalyzeBodies("func <T> apply(value: T, callback: (T) -> T): T = callback(value)\n"
            + "func use(value: Object, callback: (Text) -> Text): Object = apply(value, callback)");

        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code is "SOL2220" or "SOL2221");
    }

    [Fact]
    public void TypedLambdaParameterContributesAnUpperBoundAndAcceptsContravariance()
    {
        var analysis = AnalyzeBodies("func <T> apply(value: T, callback: (T) -> T): T = callback(value)\n"
            + "func use(): String = apply(\"seed\", { value: Object -> \"text\" })");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Equal("std.core.String", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void TypedLambdaParameterTooNarrowForTheInferredTypeIsRejected()
    {
        var analysis = AnalyzeBodies("func <T> apply(value: T, callback: (T) -> T): T = callback(value)\n"
            + "func use(value: Object): Object = apply(value, { text: Text -> text })");

        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }

    [Fact]
    public void GenericRefCallbackEvidenceRequiresExactStorageType()
    {
        var analysis = AnalyzeBodies("func <T> apply(callback: (ref T) -> (), ref value: T): () = ()\n"
            + "func inspect(ref value: Dog): () = ()\n"
            + "func use(ref value: Dog): () = apply(inspect, ref value)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Equal("std.core.Dog", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void GenericRefCallbackEvidenceDoesNotWidenStorageTypes()
    {
        var analysis = AnalyzeBodies("func <T> apply(callback: (ref T) -> (), ref value: T): () = ()\n"
            + "func inspect(ref value: Animal): () = ()\n"
            + "func use(ref value: Dog): () = apply(inspect, ref value)");

        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "apply");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code is "SOL2131" or "SOL2220" or "SOL2221");
    }

    [Fact]
    public void CallbackEffectInferenceAcceptsAStrictActualSubsetAndClosesItsOpenPattern()
    {
        var analysis = AnalyzeBodies("class DiskFailure: IOException {}\n"
            + "func <E: Throwable> execute(callback: () -> String throws E, IOException): String throws E, IOException {\n"
            + "callback()\n}\n"
            + "func readDisk(): String throws DiskFailure = \"disk\"\n"
            + "func use(): String throws IOException = execute(readDisk)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls, binding => binding.Target.Name == "execute");
        Assert.Equal("app.DiskFailure", Assert.Single(call.TypeArguments).Name);
    }

    [Fact]
    public void EffectSubsetMappingSolvesTheActualToOpenFormalMatch()
    {
        var session = AstTestSources.Session(["module app\n"
            + "class DiskFailure: IOException {}\n"
            + "func <E: Throwable> execute(callback: () -> String throws E, IOException): String throws E, IOException { callback() }\n"
            + "func readDisk(): String throws DiskFailure = \"disk\"", Core]);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var types = new TypeSpecialization(sources.Types);
        var functions = sources.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstFunction>().ToArray();
        var target = functions.Single(function => function.Name == "execute");
        var actualDeclaration = functions.Single(function => function.Name == "readDisk");
        var formal = sources.Types.GetType(target.Parameters[0].Type!);
        var actual = types.GetFunction([], sources.Types.GetType(actualDeclaration.ReturnType!), [],
            actualDeclaration.ThrownTypes.Select(sources.Types.GetType));
        var solver = new GenericInferenceSolver(types, TestContext.Current.CancellationToken);
        var inferenceParameter = Assert.Single(sources.Types.GetTypeParameters(target));
        Assert.Contains(formal.Effects, effect => effect == inferenceParameter);
        Assert.Equal("app.DiskFailure", Assert.Single(actual.Effects).Name);
        var diskFailure = sources.Types.GetType(actualDeclaration.ThrownTypes.Single());
        Assert.Equal(ConstraintStatus.Satisfied, types.CheckConstraints(target, [diskFailure], TestContext.Current.CancellationToken).Status);
        var closedFormal = types.Substitute(formal, new Dictionary<ResolvedType, ResolvedType>
            { [inferenceParameter] = diskFailure }, TestContext.Current.CancellationToken);
        Assert.Equal(IntrinsicConversion.Reference,
            types.ClassifyConversion(actual, closedFormal, TestContext.Current.CancellationToken));

        var result = solver.Solve([inferenceParameter],
            ImmutableDictionary<ResolvedType, ResolvedType>.Empty,
            [new(formal, actual, GenericInferenceRelation.Input, "typed callback")]);

        Assert.True(result.Status == GenericInferenceStatus.Solved,
            result.Status + ": " + result.Reason
            + "; evidence=" + string.Join(",", result.Evidence.Select(item => item.Relation + ":" + item.Type.Name)));
        Assert.Equal("app.DiskFailure", Assert.Single(result.Substitution).Value.Name);
    }

    [Fact]
    public void EmptyCallbackEffectSetDoesNotInventAnOpenEffectArgument()
    {
        var analysis = AnalyzeBodies("func <E: Throwable> execute(callback: () -> String throws E, IOException): String throws E, IOException {\n"
            + "callback()\n}\n"
            + "func pure(): String = \"pure\"\n"
            + "func use(): String throws IOException = execute(pure)");

        Assert.DoesNotContain(analysis.Calls, binding => binding.Target.Name == "execute");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void ExplicitEffectArgumentAcceptsTheNarrowerCallbackBudget()
    {
        var analysis = AnalyzeBodies("class DiskFailure: IOException {}\n"
            + "func <E: Throwable> execute(callback: () -> String throws E, IOException): String throws E, IOException {\n"
            + "callback()\n}\n"
            + "func readDisk(): String throws DiskFailure = \"disk\"\n"
            + "func use(): String throws IOException = execute<DiskFailure>(readDisk)");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Contains(analysis.Calls, binding => binding.Target.Name == "execute");
    }
}
