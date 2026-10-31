// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionCacheTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Fact]
    public void RepeatedProjectionReusesOnlyTheCompletedCanonicalBody()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var methodArguments = ImmutableArray.Create(analysis.Types.GetPrimitive("int"));

        var first = analysis.GetGenericMemberBodyForReceiver(method, receiver, methodArguments,
            TestContext.Current.CancellationToken);
        var second = analysis.GetGenericMemberBodyForReceiver(method, receiver, methodArguments,
            TestContext.Current.CancellationToken);

        Assert.Same(first.Projection, second.Projection);
        Assert.Equal(first.Projection.Status, second.Projection.Status);
        var firstCandidate = Assert.Single(first.Projection.Candidates);
        var secondCandidate = Assert.Single(second.Projection.Candidates);
        Assert.Same(firstCandidate.ConstructedOwner, secondCandidate.ConstructedOwner);
        Assert.Equal(firstCandidate.Paths.Length, secondCandidate.Paths.Length);
        Assert.Same(first.Body, second.Body);
        Assert.Equal(BodyBindingStatus.Bound, second.Body!.Summary.Status);
    }

    [Fact]
    public void DifferentReceiverPathsToTheSameCanonicalOwnerShareBodyButKeepTheirRoutes()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "interface Mid<T>: Root<T> {}\n"
            + "class Left<T>: Mid<T> {}\n"
            + "class Right<T>: Mid<T> {}");
        var method = Method(analysis, "echo");
        var argument = Nominal(analysis, "String");
        var left = Constructed(analysis, "Left", argument);
        var right = Constructed(analysis, "Right", argument);
        var methodArguments = ImmutableArray.Create(analysis.Types.GetPrimitive("long"));

        var leftResult = analysis.GetGenericMemberBodyForReceiver(method, left, methodArguments,
            TestContext.Current.CancellationToken);
        var rightResult = analysis.GetGenericMemberBodyForReceiver(method, right, methodArguments,
            TestContext.Current.CancellationToken);

        var leftCandidate = Assert.Single(leftResult.Projection.Candidates);
        var rightCandidate = Assert.Single(rightResult.Projection.Candidates);
        Assert.Same(leftCandidate.ConstructedOwner, rightCandidate.ConstructedOwner);
        Assert.Same(leftCandidate.ConstructedOwner.Declaration, rightCandidate.ConstructedOwner.Declaration);
        Assert.Equal(leftCandidate.ConstructedOwner.Arguments.ToArray(), rightCandidate.ConstructedOwner.Arguments.ToArray());
        Assert.Equal(left, leftCandidate.Paths[0][0]);
        Assert.Equal(right, rightCandidate.Paths[0][0]);
        Assert.Same(leftResult.Body, rightResult.Body);
    }

    [Fact]
    public void MethodTypeArgumentVectorsSelectIndependentBodyCacheEntries()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var integer = analysis.GetGenericMemberBodyForReceiver(method, receiver,
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);
        var longInteger = analysis.GetGenericMemberBodyForReceiver(method, receiver,
            [analysis.Types.GetPrimitive("long")], TestContext.Current.CancellationToken);
        var integerAgain = analysis.GetGenericMemberBodyForReceiver(method, receiver,
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, integer.Body!.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound, longInteger.Body!.Summary.Status);
        Assert.Equal("int", integer.Body.Summary.ReturnType!.Name);
        Assert.Equal("long", longInteger.Body.Summary.ReturnType!.Name);
        Assert.NotSame(integer.Body, longInteger.Body);
        Assert.Same(integer.Body, integerAgain.Body);
    }

    [Fact]
    public void OwnerTypeArgumentVectorsSelectIndependentBodyCacheEntries()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var textReceiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var integerReceiver = Constructed(analysis, "Leaf", analysis.Types.GetPrimitive("int"));

        var text = analysis.GetGenericMemberBodyForReceiver(method, textReceiver, [],
            TestContext.Current.CancellationToken);
        var integer = analysis.GetGenericMemberBodyForReceiver(method, integerReceiver, [],
            TestContext.Current.CancellationToken);
        var textAgain = analysis.GetGenericMemberBodyForReceiver(method, textReceiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal("std.core.String", text.Body!.Summary.ReturnType!.Name);
        Assert.Equal("int", integer.Body!.Summary.ReturnType!.Name);
        Assert.NotSame(text.Body, integer.Body);
        Assert.Same(text.Body, textAgain.Body);
        Assert.Equal(Nominal(analysis, "String"), Assert.Single(text.Body.OwnerTypeArguments));
        Assert.Equal(analysis.Types.GetPrimitive("int"), Assert.Single(integer.Body.OwnerTypeArguments));
    }

    [Fact]
    public void InvalidSpecializationIsReturnedButIsNotAddedToTheSuccessCache()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): String = value }\n"
            + "class Leaf<T>: Root<T> {}", allowSourceErrors: true);
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", analysis.Types.GetPrimitive("int"));

        var first = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);
        var second = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, first.Projection.Status);
        Assert.Equal(BodyBindingStatus.Invalid, first.Body!.Summary.Status);
        Assert.Equal(BodyBindingStatus.Invalid, second.Body!.Summary.Status);
        Assert.NotSame(first.Body, second.Body);
    }

    [Fact]
    public async Task ConcurrentProjectionRequestsShareOneImmutableRouteProofAndBody()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U = marker }\n"
            + "interface Left<T>: Root<T> {}\n"
            + "interface Right<T>: Root<T> {}\n"
            + "class Diamond<T>: Left<T>, Right<T> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Diamond", Nominal(analysis, "String"));
        var arguments = ImmutableArray.Create(analysis.Types.GetPrimitive("int"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            analysis.GetGenericMemberBodyForReceiver(method, receiver, arguments,
                cancellationToken))).ToArray();

        var results = await Task.WhenAll(tasks);

        var expectedProjection = results[0].Projection;
        var expectedBody = results[0].Body;
        Assert.NotNull(expectedBody);
        Assert.All(results, result =>
        {
            Assert.Same(expectedProjection, result.Projection);
            Assert.Same(expectedBody, result.Body);
            Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
            Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        });
        Assert.Equal(2, Assert.Single(expectedProjection.Candidates).Paths.Length);
    }

    [Fact]
    public void MissingAndAmbiguousCompletedOutcomesAreReusedWithoutCreatingBodies()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface TextPath: Root<String> {}\n"
            + "interface IntegerPath: Root<int> {}\n"
            + "class Conflicted: TextPath, IntegerPath {}\n"
            + "class Unrelated {}", allowSourceErrors: true);
        var method = Method(analysis, "echo");
        var conflicted = Constructed(analysis, "Conflicted");
        var unrelated = Constructed(analysis, "Unrelated");

        var ambiguousFirst = analysis.GetGenericMemberBodyForReceiver(method, conflicted, [],
            TestContext.Current.CancellationToken);
        var ambiguousAgain = analysis.GetGenericMemberBodyForReceiver(method, conflicted, [],
            TestContext.Current.CancellationToken);
        var missingFirst = analysis.GetGenericMemberBodyForReceiver(method, unrelated, [],
            TestContext.Current.CancellationToken);
        var missingAgain = analysis.GetGenericMemberBodyForReceiver(method, unrelated, [],
            TestContext.Current.CancellationToken);

        Assert.Same(ambiguousFirst.Projection, ambiguousAgain.Projection);
        Assert.Same(missingFirst.Projection, missingAgain.Projection);
        Assert.Equal(OwnerProjectionStatus.Ambiguous, ambiguousFirst.Projection.Status);
        Assert.Equal(OwnerProjectionStatus.Missing, missingFirst.Projection.Status);
        Assert.Null(ambiguousFirst.Body);
        Assert.Null(ambiguousAgain.Body);
        Assert.Null(missingFirst.Body);
        Assert.Null(missingAgain.Body);
    }

    [Fact]
    public async Task CancellationIsObservedWhileProjectionWaitsForTheAnalysisCacheGate()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var gateField = typeof(BodyAnalysis).GetField("_genericBodyGate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(BodyAnalysis).FullName, "_genericBodyGate");
        var gate = gateField.GetValue(analysis)!;
        var testToken = TestContext.Current.CancellationToken;
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        Thread? waitingThread = null;
        Task<GenericMemberBodyProjectionAnalysis>? waitingRequest = null;
        var gateHolder = Task.Factory.StartNew(() =>
        {
            Monitor.Enter(gate);
            try
            {
                gateHeld.Set();
                releaseGate.Wait(testToken);
            }
            finally { Monitor.Exit(gate); }
        }, testToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(2), testToken),
                "The test could not acquire the generic analysis cache gate.");
            var waiter = Task.Factory.StartNew(() =>
            {
                waitingThread = Thread.CurrentThread;
                started.Set();
                return analysis.GetGenericMemberBodyForReceiver(method, receiver, [], cancellation.Token);
            }, testToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            waitingRequest = waiter;

            Assert.True(started.Wait(TimeSpan.FromSeconds(2), testToken),
                "The projected body request did not start.");
            bool waitingOnGate = SpinWait.SpinUntil(() => waitingThread is { } thread
                && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2));
            Assert.True(waitingOnGate, "The request did not wait on the held analysis cache gate.");

            cancellation.Cancel();
            var completed = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(2), testToken));
            Assert.Same(waiter, completed);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiter);
        }
        finally
        {
            cancellation.Cancel();
            releaseGate.Set();
            try { await gateHolder; }
            catch (OperationCanceledException) { }
            if (waitingRequest is not null)
            {
                try { await waitingRequest; }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static BodyAnalysis Analyze(string source, bool allowSourceErrors = false)
    {
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        if (!allowSourceErrors)
        {
            Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
                .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        }
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstFunction Method(BodyAnalysis analysis, string name) => analysis.Sources.Declarations.Files
        .SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>().SelectMany(owner => owner.Members)
        .OfType<AstFunction>().Single(method => method.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name)
    {
        var declaration = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);
        return analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(declaration)!, []);
    }

    private static ResolvedType Constructed(BodyAnalysis analysis, string name, params ResolvedType[] arguments)
    {
        var declaration = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);
        return analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(declaration)!,
            arguments.ToImmutableArray());
    }
}
