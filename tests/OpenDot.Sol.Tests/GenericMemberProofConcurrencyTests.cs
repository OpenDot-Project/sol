// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberProofConcurrencyTests
{
    private const string Source = "class Root<T> { func echo(value: T) = value }\n"
        + "func <U> use(owner: Root<U>, value: U): U = owner.echo(value)";

    [Fact]
    public async Task ConcurrentFreeSpecializationsPublishOneCompletedProofSnapshot()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Source);
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            GenericMemberProofTestSource.Specialize(analysis, "use", "int"),
            TestContext.Current.CancellationToken)).ToArray();
        var bodies = await Task.WhenAll(tasks);
        var first = bodies[0];
        Assert.All(bodies, body => Assert.Same(first, body));
        Assert.Equal(BodyBindingStatus.Bound, first.Summary.Status);
        var binding = Assert.Single(first.Calls);
        var proof = first.GetGenericMemberCallProof(binding.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.All(bodies, body => Assert.Same(proof, body.GetGenericMemberCallProof(binding.Call)));
    }

    [Fact]
    public void CanceledWaiterLeavesNoPartialFreeSpecializationCacheEntry()
    {
        var analysis = GenericMemberProofTestSource.Analyze(Source);
        var function = GenericMemberProofTestSource.Function(analysis, "use");
        var arguments = System.Collections.Immutable.ImmutableArray.Create(analysis.Types.GetPrimitive("int"));
        var field = typeof(BodyAnalysis).GetField("_genericBodyGate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object gate = field.GetValue(analysis)!;
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Thread? waitingThread = null;
        Task<SpecializedBodyAnalysis>? request = null;
        Monitor.Enter(gate);
        try
        {
            request = Task.Factory.StartNew(() =>
            {
                waitingThread = Thread.CurrentThread;
                started.Set();
                return analysis.GetGenericBody(function, arguments, cancellation.Token);
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(() => waitingThread is { } thread
                && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            Assert.True(SpinWait.SpinUntil(() => request.IsCompleted, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            Monitor.Exit(gate);
        }
        Assert.NotNull(request);
        Assert.Throws<OperationCanceledException>(() => request.GetAwaiter().GetResult());
        var completed = analysis.GetGenericBody(function, arguments, TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, completed.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound,
            completed.GetGenericMemberCallProof(Assert.Single(completed.Calls).Call)!.Status);
        Assert.Same(completed, analysis.GetGenericBody(function, arguments, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndependentAnalysesNeverShareCanonicalProofTypes()
    {
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var analysis = GenericMemberProofTestSource.Analyze(Source);
            return (Analysis: analysis, Body: GenericMemberProofTestSource.Specialize(analysis, "use", "int"));
        }, TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var result in results.Skip(1))
        {
            Assert.NotSame(results[0].Body, result.Body);
            var foreign = Assert.Single(result.Body.Calls).Receiver!;
            Assert.Throws<ArgumentException>(() => results[0].Analysis.Types.ValidateOwnership(foreign));
        }
    }

    [Fact]
    public void PreCanceledCreateDoesNotPublishAnAnalysis()
    {
        var session = AstTestSources.Session("module app\n" + Source, GenericMemberProofTestSource.Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => BodyAnalysis.Create(sources, cancellation.Token));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound,
            GenericMemberProofTestSource.Specialize(analysis, "use", "int").Summary.Status);
    }

    [Fact]
    public void MemberLookupCannotUseAForeignSharedOwnershipIndex()
    {
        var first = GenericMemberProofTestSource.Analyze(Source);
        var second = GenericMemberProofTestSource.Analyze(Source);
        var error = Assert.Throws<ArgumentException>(() => new MemberLookupResolver(first.Sources.Types,
            first.Types, BodySourceIndex.Get(second.Sources), TestContext.Current.CancellationToken));
        Assert.Equal("sourceIndex", error.ParamName);
    }
}
