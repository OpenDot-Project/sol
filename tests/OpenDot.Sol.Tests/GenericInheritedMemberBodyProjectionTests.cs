// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberBodyProjectionTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Fact]
    public void ComposesOwnerArgumentsAcrossMultipleInheritedEdgesBeforeRebinding()
    {
        var analysis = Analyze("module app\n"
            + "class Cell<T> {}\nclass Pair<A, B> {}\n"
            + "interface Root<R> { func <U> convert(value: Pair<R, []U?>): Pair<R, []U?> = value }\n"
            + "interface Middle<M>: Root<Cell<[]M>> {}\nclass Leaf<L>: Middle<L> {}");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver,
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        Assert.Null(result.Projection.Reason);
        var projection = Assert.Single(result.Projection.Candidates);
        var path = Assert.Single(projection.Paths);
        Assert.Equal(3, path.Length);
        Assert.Equal(receiver, path[0]);
        Assert.Equal(projection.ConstructedOwner, path[^1]);
        Assert.Same(analysis.Sources.Declarations.GetDeclaredSymbol(root), projection.ConstructedOwner.Declaration);
        Assert.Equal("app.Root", projection.ConstructedOwner.Name);

        var closedString = Nominal(analysis, "String");
        var cell = Constructed(analysis, "Cell", analysis.Types.GetArray(closedString));
        Assert.Equal(cell, Assert.Single(projection.ConstructedOwner.Arguments));
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.Equal(projection.ConstructedOwner, result.Body.ConstructedOwner);
        Assert.Equal(cell, Assert.Single(result.Body.OwnerTypeArguments));
        Assert.Equal("int", Assert.Single(result.Body.TypeArguments).Name);

        var expectedArrayElement = analysis.Types.GetNullable(analysis.Types.GetPrimitive("int"));
        var expectedPair = Constructed(analysis, "Pair", cell, analysis.Types.GetArray(expectedArrayElement));
        Assert.Equal(expectedPair, result.Body.Summary.ReturnType);
        var value = method.Descendants().OfType<AstAtom>().Single(atom => atom.Spelling == "value");
        var expression = result.Body.GetExpression(value)!;
        Assert.Same(value, expression.Expression);
        Assert.Equal(expectedPair, expression.Type);
    }

    [Fact]
    public void ConvergingDiamondRetainsBothPathsAndBuildsOneCanonicalBody()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "interface Left<T>: Root<T> {}\ninterface Right<T>: Root<T> {}\n"
            + "class Diamond<T>: Left<T>, Right<T> {}");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Diamond", Nominal(analysis, "String"));
        var methodArgument = analysis.Types.GetPrimitive("long");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [methodArgument],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(2, candidate.Paths.Length);
        Assert.All(candidate.Paths, path =>
        {
            Assert.Equal(3, path.Length);
            Assert.Equal(receiver, path[0]);
            Assert.Equal(candidate.ConstructedOwner, path[^1]);
        });
        Assert.NotSame(candidate.Paths[0][1].Declaration, candidate.Paths[1][1].Declaration);
        string routeNames = string.Join(" ", candidate.Paths.Select(path => path[1].Name));
        Assert.Contains("Left", routeNames, StringComparison.Ordinal);
        Assert.Contains("Right", routeNames, StringComparison.Ordinal);
        Assert.Equal("std.core.String", Assert.Single(candidate.ConstructedOwner.Arguments).Name);
        Assert.Null(result.Projection.Reason);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.Equal(candidate.ConstructedOwner, result.Body.ConstructedOwner);
        Assert.Equal(methodArgument, result.Body.Summary.ReturnType);
        Assert.Single(result.Body.OwnerTypeArguments);
        Assert.Single(result.Body.TypeArguments);
    }

    [Fact]
    public void ConflictingConstructedOwnerArgumentsRemainAmbiguousAndDoNotBuildABody()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface IntRoot: Root<int> {}\ninterface TextRoot: Root<String> {}\n"
            + "class Conflicted: IntRoot, TextRoot {}", allowSourceErrors: true);
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Conflicted");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Ambiguous, result.Projection.Status);
        Assert.Null(result.Projection.Reason);
        Assert.Null(result.Body);
        Assert.Equal(2, result.Projection.Candidates.Length);
        Assert.All(result.Projection.Candidates, candidate =>
        {
            Assert.Single(candidate.Paths);
            Assert.Equal(receiver, candidate.Paths[0][0]);
            Assert.Equal(candidate.ConstructedOwner, candidate.Paths[0][^1]);
            Assert.Same(analysis.Sources.Declarations.GetDeclaredSymbol(root), candidate.ConstructedOwner.Declaration);
        });
        Assert.Contains(result.Projection.Candidates.Select(candidate =>
            Assert.Single(candidate.ConstructedOwner.Arguments).Name), name => name == "int");
        Assert.Contains(result.Projection.Candidates.Select(candidate =>
            Assert.Single(candidate.ConstructedOwner.Arguments).Name), name => name == "std.core.String");
    }

    [Fact]
    public void MissingOwnerProjectionHasNoCandidatesOrSpecializedBody()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\nclass Unrelated {}");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Unrelated");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Missing, result.Projection.Status);
        Assert.Empty(result.Projection.Candidates);
        Assert.Null(result.Body);
        Assert.Null(result.Projection.Reason);
    }

    [Fact]
    public void ProjectsEveryConstructedTypeParameterBoundBeforeBuildingTheMemberBody()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "func <R> preserve(value: R): R where R: Root<String> = value");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var preserve = Declaration<AstFunction>(analysis, "preserve");
        var receiver = analysis.Sources.Types.GetType(preserve.Parameters.Single().Type!);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver,
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        var path = Assert.Single(candidate.Paths);
        Assert.Equal(2, path.Length);
        Assert.Equal(ResolvedTypeKind.Parameter, path[0].Kind);
        Assert.Equal(candidate.ConstructedOwner, path[^1]);
        Assert.Equal("std.core.String", Assert.Single(candidate.ConstructedOwner.Arguments).Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.Equal("int", result.Body.Summary.ReturnType!.Name);
        Assert.Same(method, result.Body.Declaration);
    }

    [Fact]
    public void ProjectsBoundsOwnedByGenericTypeDeclarations()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Carrier<R> where R: Root<String> { func pass(value: R): R = value }");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var carrierMethod = Declaration<AstTypeDeclaration>(analysis, "Carrier")
            .Members.OfType<AstFunction>().Single(function => function.Name == "pass");
        var receiver = analysis.Sources.Types.GetType(carrierMethod.Parameters.Single().Type!);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(ResolvedTypeKind.Parameter, candidate.Paths[0][0].Kind);
        Assert.Equal("std.core.String", Assert.Single(candidate.ConstructedOwner.Arguments).Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.Equal("std.core.String", result.Body.Summary.ReturnType!.Name);
    }

    [Fact]
    public void SameNamedMethodAndOwnerParametersUseOnlyTheRequestedIdentityBounds()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Carrier<T> where T: Root<String> {\n"
            + "  func <T> pass(value: T): T where T: Root<int> = value\n"
            + "}");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var rootMethod = Assert.Single(root.Members.OfType<AstFunction>());
        var carrier = Declaration<AstTypeDeclaration>(analysis, "Carrier");
        var method = carrier.Members.OfType<AstFunction>().Single(function => function.Name == "pass");
        var ownerParameter = analysis.Sources.Types.GetTypeParameters(carrier).Single();
        var methodParameter = analysis.Sources.Types.GetTypeParameters(method).Single();
        var receiver = analysis.Sources.Types.GetType(method.Parameters.Single().Type!);
        Assert.NotSame(ownerParameter, methodParameter);
        Assert.NotSame(ownerParameter.Parameter, methodParameter.Parameter);
        Assert.Same(methodParameter.Parameter, receiver.Parameter);

        var result = analysis.GetGenericMemberBodyForReceiver(rootMethod, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal("int", Assert.Single(candidate.ConstructedOwner.Arguments).Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.Equal("int", result.Body.Summary.ReturnType!.Name);
    }

    [Fact]
    public void UnresolvedParameterBoundOrReceiverNeverPublishesKnownOwnerPaths()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "func <R> bounded(value: R): R where R: Root<String>, R: Missing = value\n"
            + "func broken(value: Missing): Missing = value", allowSourceErrors: true);
        var rootMethod = Assert.Single(Declaration<AstTypeDeclaration>(analysis, "Root")
            .Members.OfType<AstFunction>());
        var bounded = Declaration<AstFunction>(analysis, "bounded");
        var broken = Declaration<AstFunction>(analysis, "broken");
        var boundedReceiver = analysis.Sources.Types.GetType(bounded.Parameters.Single().Type!);
        var errorReceiver = analysis.Sources.Types.GetType(broken.Parameters.Single().Type!);

        var unresolvedBound = analysis.GetGenericMemberBodyForReceiver(rootMethod, boundedReceiver, [],
            TestContext.Current.CancellationToken);
        var unresolvedReceiver = analysis.GetGenericMemberBodyForReceiver(rootMethod, errorReceiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(ResolvedTypeKind.Parameter, boundedReceiver.Kind);
        Assert.True(errorReceiver.IsError);
        Assert.Equal(OwnerProjectionStatus.LimitExceeded, unresolvedBound.Projection.Status);
        Assert.Equal(OwnerProjectionStatus.LimitExceeded, unresolvedReceiver.Projection.Status);
        Assert.Empty(unresolvedBound.Projection.Candidates);
        Assert.Empty(unresolvedReceiver.Projection.Candidates);
        Assert.Null(unresolvedBound.Body);
        Assert.Null(unresolvedReceiver.Body);
    }

    [Fact]
    public void DirectOwnerRouteUsesTheReceiverAsItsSingleCanonicalPath()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Root", Nominal(analysis, "String"));
        var methodArgument = analysis.Types.GetPrimitive("int");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [methodArgument],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        Assert.Null(result.Projection.Reason);
        var candidate = Assert.Single(result.Projection.Candidates);
        var path = Assert.Single(candidate.Paths);
        Assert.Single(path);
        Assert.Same(receiver, candidate.ConstructedOwner);
        Assert.Same(receiver, path[0]);
        Assert.Same(method, result.Body!.Declaration);
        Assert.Same(candidate.ConstructedOwner, result.Body.ConstructedOwner);
        Assert.Equal(methodArgument, result.Body.Summary.ReturnType);
    }

    [Fact]
    public void SubstitutesNestedNullableArrayFunctionAndTupleOwnerArguments()
    {
        var analysis = Analyze("module app\n"
            + "class Box<T> {}\n"
            + "interface Root<A, B, C, D> { func preserve(value: D): D = value }\n"
            + "interface Middle<M>: Root<Box<[]M?>, (M, int), (M) -> M, (M, String)> {}\n"
            + "class Leaf<L>: Middle<L> {}");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var text = Nominal(analysis, "String");
        var integer = analysis.Types.GetPrimitive("int");
        var expectedNested = Constructed(analysis, "Box",
            analysis.Types.GetArray(analysis.Types.GetNullable(text)));
        var expectedPair = analysis.Types.GetTuple([text, integer]);
        var expectedFunction = analysis.Types.GetFunction([text], text, [AstPassingMode.Value]);
        var expectedResult = analysis.Types.GetTuple([text, text]);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        Assert.Null(result.Projection.Reason);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(expectedNested, candidate.ConstructedOwner.Arguments[0]);
        Assert.Equal(expectedPair, candidate.ConstructedOwner.Arguments[1]);
        Assert.Equal(expectedFunction, candidate.ConstructedOwner.Arguments[2]);
        Assert.Equal(expectedResult, candidate.ConstructedOwner.Arguments[3]);
        Assert.Same(candidate.ConstructedOwner, result.Body!.ConstructedOwner);
        Assert.Equal(expectedResult, result.Body.Summary.ReturnType);
    }

    [Fact]
    public void ChainedTypeParameterBoundsReachAClosedNominalOwner()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "func <S: Root<String>, R: S> inspect(value: R): R = value");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var inspect = Declaration<AstFunction>(analysis, "inspect");
        var receiver = analysis.Sources.Types.GetType(inspect.Parameters.Single().Type!);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        var path = Assert.Single(candidate.Paths);
        Assert.Equal(3, path.Length);
        Assert.Equal(ResolvedTypeKind.Parameter, path[0].Kind);
        Assert.Equal(ResolvedTypeKind.Parameter, path[1].Kind);
        Assert.Equal(candidate.ConstructedOwner, path[2]);
        Assert.Equal("std.core.String", Assert.Single(candidate.ConstructedOwner.Arguments).Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
    }

    [Fact]
    public void ExceedingTheInheritedPathDepthBudgetDoesNotPublishPartialCandidates()
    {
        const int depth = 129;
        var source = new StringBuilder("module app\nopen class Node0<T> { func echo(value: T): T = value }\n");
        for (int index = 1; index <= depth; index++)
        {
            source.Append("open class Node").Append(index).Append("<T>: Node")
                .Append(index - 1).Append("<T> {}\n");
        }
        var analysis = Analyze(source.ToString());
        var root = Declaration<AstTypeDeclaration>(analysis, "Node0");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Node" + depth, Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.LimitExceeded, result.Projection.Status);
        Assert.Empty(result.Projection.Candidates);
        Assert.Null(result.Body);
        Assert.NotNull(result.Projection.Reason);
    }

    [Fact]
    public void ARouteAtTheMaximumSupportedDepthStillProducesABoundBody()
    {
        const int depth = 128;
        var source = new StringBuilder("module app\n")
            .Append("interface Root<T> { func echo(value: T): T = value }\n");
        for (int index = 0; index < depth - 1; index++)
        {
            string parent = index + 1 == depth - 1 ? "Root" : "Node" + (index + 1);
            source.Append("interface Node").Append(index).Append("<T>: ").Append(parent).Append("<T> {}\n");
        }
        source.Append("class Leaf<T>: Node0<T> {}");
        var analysis = Analyze(source.ToString());
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        var path = Assert.Single(candidate.Paths);
        Assert.Equal(depth + 1, path.Length);
        Assert.Equal("app.Root", candidate.ConstructedOwner.Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
    }

    [Fact]
    public void OverDepthAlternativeInvalidatesAnAlreadyDiscoveredOwnerCandidate()
    {
        const int deepNodes = 128;
        var source = new StringBuilder("module app\n")
            .Append("interface Root<T> { func echo(value: T): T = value }\n");
        for (int index = 0; index < deepNodes; index++)
        {
            string parent = index + 1 == deepNodes ? "Root" : "Node" + (index + 1);
            source.Append("interface Node").Append(index).Append("<T>: ").Append(parent).Append("<T> {}\n");
        }
        source.Append("class Leaf<T>: Root<T>, Node0<T> {}");
        var analysis = Analyze(source.ToString());
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.LimitExceeded, result.Projection.Status);
        Assert.Empty(result.Projection.Candidates);
        Assert.Null(result.Body);
        Assert.Contains("128-edge depth", result.Projection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceedingThePerOwnerPathBudgetDoesNotPublishPartialCandidates()
    {
        const int layers = 8;
        var source = new StringBuilder("module app\n")
            .Append("interface Root<T> { func echo(value: T): T = value }\n");
        for (int layer = 1; layer <= layers; layer++)
        {
            string parent = layer == 1 ? "Root" : "Join" + (layer - 1);
            source.Append("interface Left").Append(layer).Append("<T>: ").Append(parent).Append("<T> {}\n")
                .Append("interface Right").Append(layer).Append("<T>: ").Append(parent).Append("<T> {}\n")
                .Append("interface Join").Append(layer).Append("<T>: Left").Append(layer).Append("<T>, Right")
                .Append(layer).Append("<T> {}\n");
        }
        source.Append("class Leaf<T>: Join").Append(layers).Append("<T> {}\n");
        var analysis = Analyze(source.ToString());
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.LimitExceeded, result.Projection.Status);
        Assert.Empty(result.Projection.Candidates);
        Assert.Null(result.Body);
        Assert.NotNull(result.Projection.Reason);
    }

    [Fact]
    public void ExceedingTheGlobalPathStateBudgetDoesNotPublishPartialCandidates()
    {
        const int layers = 7;
        const int receiverBranches = 40;
        var source = new StringBuilder("module app\n")
            .Append("interface Root<T> { func echo(value: T): T = value }\n");
        for (int layer = 1; layer <= layers; layer++)
        {
            string parent = layer == 1 ? "Root" : "Join" + (layer - 1);
            source.Append("interface Left").Append(layer).Append("<T>: ").Append(parent).Append("<T> {}\n")
                .Append("interface Right").Append(layer).Append("<T>: ").Append(parent).Append("<T> {}\n")
                .Append("interface Join").Append(layer).Append("<T>: Left").Append(layer).Append("<T>, Right")
                .Append(layer).Append("<T> {}\n");
        }
        for (int branch = 0; branch < receiverBranches; branch++)
        {
            source.Append("class Marker").Append(branch).Append(" {}\n");
        }
        source.Append("class Leaf: ");
        for (int branch = 0; branch < receiverBranches; branch++)
        {
            if (branch != 0) { source.Append(", "); }
            source.Append("Join").Append(layers).Append("<Marker").Append(branch).Append('>');
        }
        source.Append(" {}\n");

        var analysis = Analyze(source.ToString(), allowSourceErrors: true);
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.LimitExceeded, result.Projection.Status);
        Assert.Empty(result.Projection.Candidates);
        Assert.Null(result.Body);
        Assert.Contains("8192-path-state", result.Projection.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalPrintedOwnerNamesFromSeparateWorkspacesKeepCanonicalIdentitySeparate()
    {
        const string source = "module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "class Leaf<T>: Root<T> {}";
        var first = Analyze(source);
        var second = Analyze(source);
        var firstRoot = Declaration<AstTypeDeclaration>(first, "Root");
        var secondRoot = Declaration<AstTypeDeclaration>(second, "Root");
        var firstMethod = Assert.Single(firstRoot.Members.OfType<AstFunction>());
        var secondMethod = Assert.Single(secondRoot.Members.OfType<AstFunction>());
        var firstReceiver = Constructed(first, "Leaf", Nominal(first, "String"));
        var secondReceiver = Constructed(second, "Leaf", Nominal(second, "String"));
        var firstResult = first.GetGenericMemberBodyForReceiver(firstMethod, firstReceiver,
            [first.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);
        var secondResult = second.GetGenericMemberBodyForReceiver(secondMethod, secondReceiver,
            [second.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);
        var firstOwner = Assert.Single(firstResult.Projection.Candidates).ConstructedOwner;
        var secondOwner = Assert.Single(secondResult.Projection.Candidates).ConstructedOwner;

        Assert.Equal("app.Root", firstOwner.Name);
        Assert.Equal(firstOwner.Name, secondOwner.Name);
        Assert.NotSame(firstOwner.Declaration, secondOwner.Declaration);
        Assert.NotSame(firstOwner, secondOwner);
        Assert.NotSame(firstResult.Body, secondResult.Body);
        Assert.Same(firstMethod, firstResult.Body!.Declaration);
        Assert.Same(secondMethod, secondResult.Body!.Declaration);
    }

    [Fact]
    public void RouteOrderingIsStableWhenSourceFilesArePresentedInDifferentOrders()
    {
        const string rootAndRight = "module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface Zeta<T>: Root<T> {}";
        const string leftAndLeaf = "module app\n"
            + "interface Alpha<T>: Root<T> {}\n"
            + "class Leaf<T>: Zeta<T>, Alpha<T> {}";
        var forward = AnalyzeFiles(false, rootAndRight, leftAndLeaf, Core);
        var reverse = AnalyzeFiles(false, leftAndLeaf, rootAndRight, Core);
        var forwardRoot = Declaration<AstTypeDeclaration>(forward, "Root");
        var reverseRoot = Declaration<AstTypeDeclaration>(reverse, "Root");
        var forwardMethod = Assert.Single(forwardRoot.Members.OfType<AstFunction>());
        var reverseMethod = Assert.Single(reverseRoot.Members.OfType<AstFunction>());
        var forwardReceiver = Constructed(forward, "Leaf", Nominal(forward, "String"));
        var reverseReceiver = Constructed(reverse, "Leaf", Nominal(reverse, "String"));
        var forwardResult = forward.GetGenericMemberBodyForReceiver(forwardMethod, forwardReceiver, [],
            TestContext.Current.CancellationToken);
        var reverseResult = reverse.GetGenericMemberBodyForReceiver(reverseMethod, reverseReceiver, [],
            TestContext.Current.CancellationToken);
        var forwardCandidate = Assert.Single(forwardResult.Projection.Candidates);
        var reverseCandidate = Assert.Single(reverseResult.Projection.Candidates);
        string[] forwardPaths = forwardCandidate.Paths
            .Select(path => string.Join(" -> ", path.Select(type => type.Name))).ToArray();
        string[] reversePaths = reverseCandidate.Paths
            .Select(path => string.Join(" -> ", path.Select(type => type.Name))).ToArray();

        Assert.Equal(OwnerProjectionStatus.Unique, forwardResult.Projection.Status);
        Assert.Equal(OwnerProjectionStatus.Unique, reverseResult.Projection.Status);
        Assert.Equal(2, forwardPaths.Length);
        Assert.Equal(forwardPaths, reversePaths);
        Assert.Contains(forwardPaths, path => path.Contains("Alpha", StringComparison.Ordinal));
        Assert.Contains(forwardPaths, path => path.Contains("Zeta", StringComparison.Ordinal));
        Assert.Equal(BodyBindingStatus.Bound, forwardResult.Body!.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound, reverseResult.Body!.Summary.Status);
    }

    [Fact]
    public void UnresolvedReachableBaseRouteFailsClosedWithoutPublishingKnownOwners()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface Broken<T>: Root<T>, Missing<T> {}\n"
            + "class Leaf<T>: Broken<T> {}", allowSourceErrors: true);
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.LimitExceeded, result.Projection.Status);
        Assert.Empty(result.Projection.Candidates);
        Assert.Null(result.Body);
        Assert.Contains("unresolved", result.Projection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsForeignReceiverFunctionAndMethodArgumentsBeforeReturningAProjection()
    {
        const string source = "module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "class Leaf<T>: Root<T> {}";
        var analysis = Analyze(source);
        var foreign = Analyze(source);
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var foreignRoot = Declaration<AstTypeDeclaration>(foreign, "Root");
        var foreignMethod = Assert.Single(foreignRoot.Members.OfType<AstFunction>());
        var foreignReceiver = Constructed(foreign, "Leaf", Nominal(foreign, "String"));
        var methodArgument = analysis.Types.GetPrimitive("int");

        var foreignReceiverError = Assert.Throws<ArgumentException>(() =>
            analysis.GetGenericMemberBodyForReceiver(method, foreignReceiver, [methodArgument],
                TestContext.Current.CancellationToken));
        var foreignFunctionError = Assert.Throws<ArgumentException>(() =>
            analysis.GetGenericMemberBodyForReceiver(foreignMethod, receiver, [methodArgument],
                TestContext.Current.CancellationToken));
        var foreignMethodArgumentError = Assert.Throws<ArgumentException>(() =>
            analysis.GetGenericMemberBodyForReceiver(method, receiver, [foreign.Types.GetPrimitive("int")],
                TestContext.Current.CancellationToken));
        var wrongArityError = Assert.Throws<ArgumentException>(() =>
            analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
                TestContext.Current.CancellationToken));

        Assert.Contains("another specialization workspace", foreignReceiverError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("another source analysis", foreignFunctionError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("another specialization workspace", foreignMethodArgumentError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("method type argument", wrongArityError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialDeclaringOwnerPartsResolveToOneCanonicalProjectedOwner()
    {
        var session = AstTestSources.Session(
            "module app\nopen partial class Root<T> {}\nclass Leaf<T>: Root<T> {}",
            "module app\nopen partial class Root<T> { func echo(value: T): T = value }",
            Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var parts = session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>()
            .Where(declaration => declaration.Name == "Root").ToArray();
        Assert.Equal(2, parts.Length);
        var symbol = sources.Declarations.GetDeclaredSymbol(parts[0]);
        Assert.Same(symbol, sources.Declarations.GetDeclaredSymbol(parts[1]));
        var method = Assert.Single(parts[1].Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var owner = Assert.Single(result.Projection.Candidates).ConstructedOwner;
        Assert.Same(symbol, owner.Declaration);
        Assert.Same(method, result.Body!.Declaration);
        Assert.Same(owner.Declaration, result.Body.ConstructedOwner!.Declaration);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
    }

    [Fact]
    public void OpenOwnerProjectionDoesNotPublishABody()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Derived<T>: Root<T> {}\n"
            + "func <U> inspect(value: Derived<U>) { () }");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var inspect = Declaration<AstFunction>(analysis, "inspect");
        var receiver = analysis.Sources.Types.GetType(inspect.Parameters.Single().Type!);

        var error = Assert.Throws<ArgumentException>(() => analysis.GetGenericMemberBodyForReceiver(
            method, receiver, [], TestContext.Current.CancellationToken));

        Assert.Contains("closed type arguments", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreCanceledProjectionRequestPropagatesCancellation()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\nclass Leaf<T>: Root<T> {}");
        var root = Declaration<AstTypeDeclaration>(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => analysis.GetGenericMemberBodyForReceiver(
            method, receiver, [], cancellation.Token));

        var active = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);
        var repeated = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);
        Assert.Equal(OwnerProjectionStatus.Unique, active.Projection.Status);
        Assert.Same(active.Projection, repeated.Projection);
        Assert.Same(active.Body, repeated.Body);
    }

    private static BodyAnalysis Analyze(string source, bool allowSourceErrors = false) =>
        AnalyzeFiles(allowSourceErrors, source, Core);

    private static BodyAnalysis AnalyzeFiles(bool allowSourceErrors, params string[] files)
    {
        var session = AstTestSources.Session(files);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        if (!allowSourceErrors)
        {
            Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
                .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        }
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static TDeclaration Declaration<TDeclaration>(BodyAnalysis analysis, string name)
        where TDeclaration : AstDeclaration => analysis.Sources.Declarations.Files
            .SelectMany(file => file.Nodes).OfType<TDeclaration>()
            .Single(declaration => declaration.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name)
    {
        var declaration = Declaration<AstTypeDeclaration>(analysis, name);
        return analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(declaration)!, []);
    }

    private static ResolvedType Constructed(BodyAnalysis analysis, string name,
        params ResolvedType[] arguments)
    {
        var declaration = Declaration<AstTypeDeclaration>(analysis, name);
        return analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(declaration)!,
            arguments.ToImmutableArray());
    }
}
