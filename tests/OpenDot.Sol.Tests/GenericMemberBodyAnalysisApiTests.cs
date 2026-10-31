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

public sealed class GenericMemberBodyAnalysisApiTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\nopen class RuntimeException: Exception {}";
    private const string Source = "module app\n"
        + "class Holder<T> { func <U> combine(left: T, right: U): U = right }\n"
        + "class Other<T> { func <U> combine(left: T, right: U): U = right }\n"
        + "open class Base<T> { func <U> echo(left: T, right: U): U = right }\n"
        + "class Derived: Base<int> {}\n"
        + "func exercise(integer: Holder<int>, text: Holder<String>, other: Other<String>, inherited: Derived) {\n"
        + "    integer.combine<int>(1, 2)\n"
        + "    text.combine<int>(\"text\", 3)\n"
        + "    text.combine<String>(\"text\", \"result\")\n"
        + "    other.combine<String>(\"other\", \"result\")\n"
        + "    inherited.echo<int>(1, 2)\n    ()\n"
        + "}\n"
        + "func <T> openOwner(holder: Holder<T>, value: T) { () }";

    private sealed record Fixture(BodyAnalysis Analysis, AstFunction HolderMethod,
        AstFunction BaseMethod, BodyCallBinding[] HolderCalls, BodyCallBinding OtherCall,
        BodyCallBinding InheritedCall, ResolvedType DerivedType, ResolvedType OpenHolderType,
        ResolvedType OpenMethodArgument);

    private static Fixture CreateFixture()
    {
        var session = AstTestSources.Session(Source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var declarations = session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>().ToArray();
        var holder = Assert.Single(declarations, declaration => declaration.Name == "Holder");
        var baseType = Assert.Single(declarations, declaration => declaration.Name == "Base");
        var holderMethod = Assert.Single(holder.Members.OfType<AstFunction>());
        var baseMethod = Assert.Single(baseType.Members.OfType<AstFunction>());
        var calls = analysis.Calls.OrderBy(binding => binding.Call.Origin.Span.Start).ToArray();
        var holderCalls = calls.Where(binding => ReferenceEquals(binding.Target, holderMethod)).ToArray();
        var otherMethod = Assert.Single(declarations.Single(declaration => declaration.Name == "Other")
            .Members.OfType<AstFunction>());
        var otherCall = Assert.Single(calls, binding => ReferenceEquals(binding.Target, otherMethod));
        var inheritedCall = Assert.Single(calls, binding => ReferenceEquals(binding.Target, baseMethod));
        var openOwner = session.Files.SelectMany(file => file.Nodes).OfType<AstFunction>()
            .Single(function => function.Name == "openOwner");
        var exercise = session.Files.SelectMany(file => file.Nodes).OfType<AstFunction>()
            .Single(function => function.Name == "exercise");
        var methodTypeParameter = sources.Types.GetTypeParameters(holderMethod).Single();
        return new(analysis, holderMethod, baseMethod, holderCalls, otherCall, inheritedCall,
            sources.Types.GetType(exercise.Parameters.Single(parameter => parameter.Name == "inherited").Type!),
            sources.Types.GetType(openOwner.Parameters.Single(parameter => parameter.Name == "holder").Type!),
            methodTypeParameter);
    }

    private static SpecializedBodyAnalysis GetGenericMemberBody(BodyAnalysis analysis, AstFunction function,
        ResolvedType owner, ImmutableArray<ResolvedType> methodArguments,
        CancellationToken cancellationToken = default) =>
        analysis.GetGenericMemberBody(function, owner, methodArguments, cancellationToken);

    private static ArgumentException ThrowsArgumentException(Action action) => Assert.Throws<ArgumentException>(action);

    [Fact]
    public void CacheKeyAndResultMetadataIncludeOwnerAndMethodVectors()
    {
        var fixture = CreateFixture();
        var calls = fixture.HolderCalls;
        Assert.Equal(3, calls.Length);

        var first = GetGenericMemberBody(fixture.Analysis, fixture.HolderMethod,
            calls[0].Receiver!, calls[0].TypeArguments, TestContext.Current.CancellationToken);
        var same = GetGenericMemberBody(fixture.Analysis, fixture.HolderMethod,
            calls[0].Receiver!, calls[0].TypeArguments, TestContext.Current.CancellationToken);
        var changedOwner = GetGenericMemberBody(fixture.Analysis, fixture.HolderMethod,
            calls[1].Receiver!, calls[1].TypeArguments, TestContext.Current.CancellationToken);
        var changedMethodArguments = GetGenericMemberBody(fixture.Analysis, fixture.HolderMethod,
            calls[1].Receiver!, calls[2].TypeArguments, TestContext.Current.CancellationToken);

        Assert.Same(first, same);
        Assert.NotSame(first, changedOwner);
        Assert.NotSame(changedOwner, changedMethodArguments);
        Assert.Equal(BodyBindingStatus.Bound, first.Summary.Status);
        Assert.Equal("int", Assert.Single(first.OwnerTypeArguments).Name);
        Assert.Equal("int", Assert.Single(first.TypeArguments).Name);
        Assert.Equal("app.Holder", first.ConstructedOwner!.Name);
        Assert.Equal("std.core.String", Assert.Single(changedOwner.OwnerTypeArguments).Name);
        Assert.Equal("std.core.String", Assert.Single(changedMethodArguments.TypeArguments).Name);
    }

    [Fact]
    public void RequiresTheDirectCanonicalOwnerAndExactMethodArity()
    {
        var fixture = CreateFixture();
        var directCall = fixture.HolderCalls[0];
        var inheritedReceiver = fixture.InheritedCall.Receiver!;

        var wrongOwner = ThrowsArgumentException(() => GetGenericMemberBody(fixture.Analysis,
            fixture.HolderMethod, fixture.OtherCall.Receiver!, directCall.TypeArguments,
            TestContext.Current.CancellationToken));
        var inheritedProjection = ThrowsArgumentException(() => GetGenericMemberBody(fixture.Analysis,
            fixture.BaseMethod, fixture.DerivedType, fixture.InheritedCall.TypeArguments,
            TestContext.Current.CancellationToken));
        var wrongArity = ThrowsArgumentException(() => GetGenericMemberBody(fixture.Analysis,
            fixture.HolderMethod, directCall.Receiver!, ImmutableArray<ResolvedType>.Empty,
            TestContext.Current.CancellationToken));

        Assert.Contains("direct constructed owner", wrongOwner.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct constructed owner", inheritedProjection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("method type argument", wrongArity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(inheritedReceiver);
    }

    [Fact]
    public void RejectsOpenAndForeignTypeArgumentsBeforeRebinding()
    {
        var fixture = CreateFixture();
        var openOwner = fixture.OpenHolderType;
        var methodArgument = fixture.HolderCalls[0].TypeArguments;

        var openOwnerFailure = ThrowsArgumentException(() => GetGenericMemberBody(fixture.Analysis,
            fixture.HolderMethod, openOwner, methodArgument, TestContext.Current.CancellationToken));
        var openMethodFailure = ThrowsArgumentException(() => GetGenericMemberBody(fixture.Analysis,
            fixture.HolderMethod, fixture.HolderCalls[0].Receiver!, [fixture.OpenMethodArgument],
            TestContext.Current.CancellationToken));

        var foreign = CreateFixture();
        var foreignOwnerFailure = ThrowsArgumentException(() => GetGenericMemberBody(fixture.Analysis,
            fixture.HolderMethod, foreign.HolderCalls[0].Receiver!, methodArgument,
            TestContext.Current.CancellationToken));

        Assert.Contains("closed type arguments", openOwnerFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed type arguments", openMethodFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("another specialization workspace", foreignOwnerFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanceledRequestDoesNotReturnAPreviouslyCachedBody()
    {
        var fixture = CreateFixture();
        var call = fixture.HolderCalls[0];
        var closed = GetGenericMemberBody(fixture.Analysis, fixture.HolderMethod, call.Receiver!,
            call.TypeArguments, TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() => fixture.Analysis.GetGenericMemberBody(
            fixture.HolderMethod, call.Receiver!, call.TypeArguments, canceled.Token));
        var active = GetGenericMemberBody(fixture.Analysis, fixture.HolderMethod, call.Receiver!,
            call.TypeArguments, TestContext.Current.CancellationToken);

        Assert.Same(closed, active);
    }

    [Fact]
    public void PartialOwnerPartsShareTheCanonicalParameterAndBodyView()
    {
        var session = AstTestSources.Session(
            "module app\nclass Text {}\npartial class PartialHolder<T> {}",
            "module app\npartial class PartialHolder<T> { func echo(value: T): T = value }");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var ownerParts = session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>()
            .Where(declaration => declaration.Name == "PartialHolder").ToArray();
        Assert.Equal(2, ownerParts.Length);
        var method = Assert.Single(ownerParts[1].Members.OfType<AstFunction>());
        var symbol = sources.Declarations.GetDeclaredSymbol(ownerParts[0])!;
        Assert.Same(symbol, sources.Declarations.GetDeclaredSymbol(ownerParts[1]));
        var textDeclaration = session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>()
            .Single(declaration => declaration.Name == "Text");
        var text = analysis.Types.Nominal(sources.Declarations.GetDeclaredSymbol(textDeclaration)!, []);
        var owner = analysis.Types.Nominal(symbol, [text]);

        var view = analysis.GetGenericMemberBody(method, owner, [], TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, view.Summary.Status);
        Assert.Same(symbol, view.ConstructedOwner!.Declaration);
        Assert.Equal(text, Assert.Single(view.OwnerTypeArguments));
        Assert.Equal(text, view.Summary.ReturnType);
    }

    [Fact]
    public void NestedOwnerIsRejectedBeforeBodyPublication()
    {
        var session = AstTestSources.Session(
            "module app\nclass Outer<T> { class Inner<U> { func echo(value: U): U = value } }");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var outer = Assert.Single(session.Files[0].Root.Declarations.OfType<AstTypeDeclaration>());
        var nested = Assert.Single(outer.Members.OfType<AstTypeDeclaration>());
        var method = Assert.Single(nested.Members.OfType<AstFunction>());
        var intType = analysis.Types.GetPrimitive("int");
        var owner = analysis.Types.Nominal(sources.Declarations.GetDeclaredSymbol(nested)!, [intType]);

        var failure = Assert.Throws<ArgumentException>(() => analysis.GetGenericMemberBody(
            method, owner, [], TestContext.Current.CancellationToken));

        Assert.Contains("Nested generic owners", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationIsObservedWhileAnotherMemberBodyRequestOwnsTheGate()
    {
        var fixture = CreateFixture();
        var call = fixture.HolderCalls[0];
        var gateField = typeof(BodyAnalysis).GetField("_genericBodyGate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(BodyAnalysis).FullName, "_genericBodyGate");
        var gate = gateField.GetValue(fixture.Analysis)!;
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        Thread? workerThread = null;
        Task<SpecializedBodyAnalysis>? waitingRequest = null;
        var gateHolder = Task.Factory.StartNew(() =>
        {
            Monitor.Enter(gate);
            try
            {
                gateHeld.Set();
                releaseGate.Wait(TestContext.Current.CancellationToken);
            }
            finally { Monitor.Exit(gate); }
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
                "The test could not acquire the generic body cache gate.");
            var waiter = Task.Factory.StartNew(() =>
            {
                workerThread = Thread.CurrentThread;
                started.Set();
                return fixture.Analysis.GetGenericMemberBody(fixture.HolderMethod, call.Receiver!,
                    call.TypeArguments, cancellation.Token);
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            waitingRequest = waiter;

            Assert.True(started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
                "The generic body request did not start.");
            bool waitingOnGate = SpinWait.SpinUntil(() => workerThread is { } thread
                && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2));
            Assert.True(waitingOnGate, "The request did not wait on the held generic body gate.");

            cancellation.Cancel();
            var completed = await Task.WhenAny(waiter,
                Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
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
}
