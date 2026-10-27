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

public sealed class GenericBodyCacheCancellationTests
{
    private const string Source = "module app\nfunc <T> plus(left, right: T): T = left + right";

    private static (BodyAnalysis Analysis, AstFunction Function) Analyze()
    {
        var session = AstTestSources.Session(Source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var function = session.Files.SelectMany(file => file.Nodes).OfType<AstFunction>()
            .Single(declaration => declaration.Name == "plus");
        return (analysis, function);
    }

    private static object GetGenericBody(BodyAnalysis analysis, AstFunction function,
        ResolvedType argument, CancellationToken cancellationToken)
    {
        var method = typeof(BodyAnalysis).GetMethod("GetGenericBody", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(BodyAnalysis).FullName, "GetGenericBody");
        return method.Invoke(analysis, [function, ImmutableArray.Create(argument), cancellationToken])!;
    }

    [Fact]
    public void CacheSharesOnlySuccessfulViewsByFunctionAndOrderedTypeIdentity()
    {
        var (analysis, function) = Analyze();
        var integer = analysis.Types.GetPrimitive("int");
        var boolean = analysis.Types.GetPrimitive("bool");

        var first = GetGenericBody(analysis, function, integer, TestContext.Current.CancellationToken);
        var second = GetGenericBody(analysis, function, integer, TestContext.Current.CancellationToken);
        var failedFirst = GetGenericBody(analysis, function, boolean, TestContext.Current.CancellationToken);
        var failedSecond = GetGenericBody(analysis, function, boolean, TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.NotSame(failedFirst, failedSecond);
    }

    [Fact]
    public async Task CancellationIsObservedWhileAnotherRequestOwnsTheSingleFlightGate()
    {
        var (analysis, function) = Analyze();
        var gateField = typeof(BodyAnalysis).GetField("_genericBodyGate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(BodyAnalysis).FullName, "_genericBodyGate");
        var gate = gateField.GetValue(analysis)!;
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        Thread? workerThread = null;
        Task<object>? waitingRequest = null;
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
                "The test could not acquire the cache gate.");
            var waiter = Task.Factory.StartNew(() =>
            {
                workerThread = Thread.CurrentThread;
                started.Set();
                return GetGenericBody(analysis, function, analysis.Types.GetPrimitive("int"), cancellation.Token);
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            waitingRequest = waiter;

            Assert.True(started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
                "The cache waiter did not start.");
            bool waitingOnGate = SpinWait.SpinUntil(() => workerThread is { } thread
                && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2));
            Assert.True(waitingOnGate, "The worker did not reach the held specialization gate.");

            cancellation.Cancel();
            var completed = await Task.WhenAny(waiter,
                Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Same(waiter, completed);
            var failure = await Assert.ThrowsAsync<TargetInvocationException>(async () => await waiter);
            Assert.IsType<OperationCanceledException>(failure.InnerException);
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
                catch (TargetInvocationException) { }
                catch (OperationCanceledException) { }
            }
        }
    }
}
