// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class TypeSpecializationTests
{
    private static TypeAnalysis Analyze(params string[] text) => TypeAnalysis.Create(AstTestSources.Session(text), TestContext.Current.CancellationToken);
    private static AstFunction Function(TypeAnalysis types, string name) => types.Declarations.Files.SelectMany(f => f.Nodes)
        .OfType<AstFunction>().Single(f => f.Name == name);
    private static ResolvedType Property(TypeAnalysis types, string name) => types.GetType(types.Declarations.Files.SelectMany(f => f.Nodes)
        .OfType<AstProperty>().Single(p => p.Name == name).Type!);

    [Fact]
    public void UnusedParametersHaveOwnerAndPositionAndPartialPartsShareThem()
    {
        var types = Analyze("module app\npartial class Box<T, U> {}\nfunc <T> unused(): () {}",
            "module app\npartial class Box<T, U> {}");
        var parts = types.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstTypeDeclaration>().ToArray();
        var first = types.GetTypeParameters(parts[0]);
        Assert.Same(first[0], types.GetTypeParameters(parts[1])[0]);
        Assert.Equal(1, first[1].Parameter!.Ordinal);
        Assert.Same(parts[0], first[1].Parameter!.Owner);
        Assert.NotSame(first[0], types.GetTypeParameters(Function(types, "unused"))[0]);
    }

    [Fact]
    public void SubstitutionReusesWrittenClosedTypesAndLeavesAnalysisFrozen()
    {
        var types = Analyze("module app\nclass Box<T> {}\nfunc <T> wrap(value: T): Box<T?> throws T {}\nvar closed: Box<int?>");
        int count = types.Types.Length;
        var workspace = new TypeSpecialization(types);
        var function = Function(types, "wrap");
        var parameter = Assert.Single(types.GetTypeParameters(function));
        var substitutions = new Dictionary<ResolvedType, ResolvedType> { [parameter] = workspace.GetPrimitive("int") };
        var result = workspace.Substitute(types.GetType(function.ReturnType!), substitutions, TestContext.Current.CancellationToken);
        Assert.Same(Property(types, "closed"), result);
        Assert.Equal(count, types.Types.Length);
        Assert.Same(parameter, types.GetType(function.Parameters[0].Type!));
    }

    [Fact]
    public void FunctionModesEffectsAndNullableNormalizationSurviveSubstitution()
    {
        var types = Analyze("module app\nfunc <T, E> apply(value: (ref T?, out T) -> T throws E): () {}\nvar nullable: int?");
        var workspace = new TypeSpecialization(types);
        var function = Function(types, "apply");
        var parameters = types.GetTypeParameters(function);
        var result = workspace.Substitute(types.GetType(function.Parameters[0].Type!),
            new Dictionary<ResolvedType, ResolvedType> { [parameters[0]] = Property(types, "nullable"), [parameters[1]] = workspace.GetPrimitive("int") },
            TestContext.Current.CancellationToken);
        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Out }, result.ParameterModes);
        Assert.Same(Property(types, "nullable"), result.Arguments[0]);
        Assert.Same(Property(types, "nullable"), result.Arguments[1]);
        Assert.Same(workspace.GetPrimitive("int"), Assert.Single(result.Effects));
    }

    [Fact]
    public void SubstitutionIsSimultaneousAndDoesNotRewriteReplacementAgain()
    {
        var types = Analyze("module app\nfunc <T, U> pair(value: (T, U)): () {}");
        var function = Function(types, "pair");
        var parameters = types.GetTypeParameters(function);
        var workspace = new TypeSpecialization(types);
        var result = workspace.Substitute(types.GetType(function.Parameters[0].Type!),
            new Dictionary<ResolvedType, ResolvedType> { [parameters[0]] = parameters[1], [parameters[1]] = workspace.GetPrimitive("int") },
            TestContext.Current.CancellationToken);
        Assert.Same(parameters[1], result.Arguments[0]);
        Assert.Same(workspace.GetPrimitive("int"), result.Arguments[1]);
    }

    [Fact]
    public void BaseSubstitutionKeepsGenericInvarianceAndInterfaceConstraints()
    {
        var types = Analyze("module app\ninterface Named<T> {}\nopen class Base<T>: Named<T> {}\nclass Child<T>: Base<[]T> {}\n"
            + "func <T: Named<[]int>> accept(value: T): () {}\nvar child: Child<int>\nvar good: Named<[]int>\nvar bad: Named<[]byte>");
        Assert.False(types.HasErrors);
        var workspace = new TypeSpecialization(types);
        var child = Property(types, "child");
        Assert.Equal("app.Base", Assert.Single(workspace.GetDirectBases(child, TestContext.Current.CancellationToken)).Name);
        Assert.Equal(IntrinsicConversion.Reference, workspace.ClassifyConversion(child, Property(types, "good"), TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None, workspace.ClassifyConversion(child, Property(types, "bad"), TestContext.Current.CancellationToken));
        Assert.Equal(ConstraintStatus.Satisfied, workspace.CheckConstraints(Function(types, "accept"), new[] { child }, TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void ObjectConstraintRejectsStructBoxingButAcceptsImplicitClassBase()
    {
        var types = Analyze("module std.core\nopen class Object {}", "module app\nstruct Value {}\nclass Ref {}\n"
            + "func <T: Object> accept(value: T): () {}\nvar value: Value\nvar reference: Ref\nvar root: Object");
        var workspace = new TypeSpecialization(types);
        var function = Function(types, "accept");
        Assert.Equal(ConstraintStatus.Unsatisfied, workspace.CheckConstraints(function, new[] { Property(types, "value") }, TestContext.Current.CancellationToken).Status);
        Assert.Equal(ConstraintStatus.Satisfied, workspace.CheckConstraints(function, new[] { Property(types, "reference") }, TestContext.Current.CancellationToken).Status);
        Assert.Equal(IntrinsicConversion.Boxing, workspace.ClassifyConversion(Property(types, "value"), Property(types, "root"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefLikeInterfaceConstraintUsesConstrainedSatisfactionWithoutBoxing()
    {
        var types = Analyze("module app\ninterface Item {}\nref struct View: Item {}\nfunc <T: Item> good(value: T): () where T: allows ref struct {}\n"
            + "func <T: Item> bad(value: T): () {}\nvar view: View\nvar item: Item");
        var workspace = new TypeSpecialization(types);
        var view = Property(types, "view");
        Assert.Equal(IntrinsicConversion.None, workspace.ClassifyConversion(view, Property(types, "item"), TestContext.Current.CancellationToken));
        Assert.Equal(ConstraintStatus.Satisfied, workspace.CheckConstraints(Function(types, "good"), new[] { view }, TestContext.Current.CancellationToken).Status);
        Assert.Equal(ConstraintStatus.Unsatisfied, workspace.CheckConstraints(Function(types, "bad"), new[] { view }, TestContext.Current.CancellationToken).Status);
    }

    [Fact]
    public void WhereBoundsAndNullableBottomAreChecked()
    {
        var types = Analyze("module app\ninterface A {}\ninterface B {}\nclass Both: A, B {}\nclass Only: A {}\n"
            + "func <T> test(value: T): () where T: A, T: B {}\nvar good: Both\nvar bad: Only\nvar nullable: int?");
        var workspace = new TypeSpecialization(types);
        Assert.Equal(ConstraintStatus.Satisfied, workspace.CheckConstraints(Function(types, "test"), new[] { Property(types, "good") }, TestContext.Current.CancellationToken).Status);
        Assert.Equal(ConstraintStatus.Unsatisfied, workspace.CheckConstraints(Function(types, "test"), new[] { Property(types, "bad") }, TestContext.Current.CancellationToken).Status);
        Assert.Equal(IntrinsicConversion.Reference, workspace.ClassifyConversion(workspace.GetNullable(workspace.GetBottom()), Property(types, "nullable"), TestContext.Current.CancellationToken));
        Assert.Equal(IntrinsicConversion.None, workspace.ClassifyConversion(Property(types, "nullable"), workspace.GetPrimitive("int"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkspaceInterningIsConcurrentAndForeignIdentityIsRejected()
    {
        var types = Analyze("module app\nfunc <T> value(): T? {}");
        var workspace = new TypeSpecialization(types);
        var function = Function(types, "value");
        var parameter = Assert.Single(types.GetTypeParameters(function));
        var substitution = new Dictionary<ResolvedType, ResolvedType> { [parameter] = workspace.GetPrimitive("int") };
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() => workspace.Substitute(types.GetType(function.ReturnType!), substitution,
            TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.Same(results[0], result));
        var other = new TypeSpecialization(types);
        Assert.Throws<ArgumentException>(() => other.ValidateOwnership(results[0]));
        Assert.Throws<ArgumentException>(() => types.ValidateOwnership(results[0]));
    }

    [Fact]
    public void LimitsCancellationAndInvalidManagedStorageHaveExplicitFailures()
    {
        var types = Analyze("module app\nref struct View {}\nfunc <T> array(value: []T): () {}\nvar view: View");
        var function = Function(types, "array");
        var parameter = Assert.Single(types.GetTypeParameters(function));
        var workspace = new TypeSpecialization(types, instanceLimit: 1);
        workspace.GetPrimitive("int");
        Assert.Equal(SpecializationFailure.ResourceLimit, Assert.Throws<TypeSpecializationException>(() => workspace.GetPrimitive("byte")).Failure);
        Assert.Equal(SpecializationFailure.InvalidType, Assert.Throws<TypeSpecializationException>(() => workspace.Substitute(
            types.GetType(function.Parameters[0].Type!), new Dictionary<ResolvedType, ResolvedType> { [parameter] = Property(types, "view") },
            TestContext.Current.CancellationToken)).Failure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.Substitute(parameter, new Dictionary<ResolvedType, ResolvedType>(), cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypeSpecialization(types, depthLimit: 129));
        Assert.Throws<ArgumentException>(() => workspace.GetPrimitive("ubyte"));
    }

    [Fact]
    public void DepthBudgetAndForeignSubstitutionsCannotBypassOwnership()
    {
        var types = Analyze("module app\nfunc <T> nested(value: [][]T): () {}\nfunc empty(): () {}");
        var workspace = new TypeSpecialization(types, depthLimit: 1);
        var function = Function(types, "nested");
        var parameter = Assert.Single(types.GetTypeParameters(function));
        var replacements = new Dictionary<ResolvedType, ResolvedType> { [parameter] = workspace.GetPrimitive("int") };
        Assert.Equal(SpecializationFailure.ResourceLimit, Assert.Throws<TypeSpecializationException>(() => workspace.Substitute(
            types.GetType(function.Parameters[0].Type!), replacements, TestContext.Current.CancellationToken)).Failure);
        var foreign = new TypeSpecialization(Analyze("module other\nvar n: int"));
        Assert.Throws<ArgumentException>(() => workspace.Substitute(parameter,
            new Dictionary<ResolvedType, ResolvedType> { [parameter] = foreign.GetPrimitive("int") }, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => workspace.Substitute(parameter,
            new Dictionary<ResolvedType, ResolvedType> { [workspace.GetPrimitive("int")] = parameter }, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => workspace.CheckConstraints(function, Array.Empty<ResolvedType>(), TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.CheckConstraints(Function(types, "empty"), Array.Empty<ResolvedType>(), cancellation.Token));
    }
}
