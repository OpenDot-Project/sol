// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalConcurrencyTests
{
    private static (BodyAnalysis Analysis, GenericMemberCallProof Local) Create()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U> local(item: U): U = item\n"
            + "local<int>(1)\nvalue");
        GenericLexicalTestSource.Bound(root);
        return (analysis, GenericLexicalTestSource.Local(root));
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneSuccessfulLocalSnapshot()
    {
        var (analysis, local) = Create();
        var requests = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            analysis.GetLexicalBody((AstFunction)local.Binding.Target, local.Binding.TypeArguments,
                local.Binding.LexicalContext!, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken)).ToArray();
        var bodies = await Task.WhenAll(requests);
        Assert.All(bodies, body => Assert.Same(local.SpecializedBody, body));
        Assert.All(bodies, body => Assert.Equal(BodyBindingStatus.Bound, body.Summary.Status));
    }

    [Fact]
    public async Task ConcurrentDifferentArgumentsKeepTheirClosedTypesSeparate()
    {
        var (analysis, local) = Create();
        var types = new[] { analysis.Types.GetPrimitive("int"), analysis.Types.GetPrimitive("long") };
        var requests = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            (Index: index % 2, Body: analysis.GetLexicalBody((AstFunction)local.Binding.Target,
                [types[index % 2]], local.Binding.LexicalContext!, TestContext.Current.CancellationToken)),
            TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(requests);
        foreach (var result in results)
        {
            Assert.Same(types[result.Index], result.Body.Summary.ReturnType);
            Assert.Same(results.First(other => other.Index == result.Index).Body, result.Body);
        }
        Assert.NotSame(results[0].Body, results[1].Body);
    }

    [Fact]
    public void PreCanceledRequestDoesNotReturnAnAlreadyCachedBody()
    {
        var (analysis, local) = Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            local.Binding.TypeArguments, local.Binding.LexicalContext!, cancellation.Token));
        Assert.Same(local.SpecializedBody, analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            local.Binding.TypeArguments, local.Binding.LexicalContext!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CancellationInterruptsASingleFlightWait()
    {
        var (analysis, local) = Create();
        object gate = typeof(BodyAnalysis).GetField("_genericBodyGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(analysis)!;
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Task<SpecializedBodyAnalysis>? request = null;
        Thread? worker = null;
        Monitor.Enter(gate);
        try
        {
            request = Task.Factory.StartNew(() =>
            {
                worker = Thread.CurrentThread;
                started.Set();
                return analysis.GetLexicalBody((AstFunction)local.Binding.Target,
                    [analysis.Types.GetPrimitive("long")], local.Binding.LexicalContext!, cancellation.Token);
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(() => worker is { } thread
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
        var completed = analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            [analysis.Types.GetPrimitive("long")], local.Binding.LexicalContext!, TestContext.Current.CancellationToken);
        Assert.Equal("long", completed.Summary.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, completed.Summary.Status);
    }

    [Fact]
    public async Task SeparateWorkspacesNeverShareLexicalSnapshotsOrTypes()
    {
        var requests = Enumerable.Range(0, 4).Select(_ => Task.Run(Create,
            TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(requests);
        var first = results[0];
        foreach (var other in results.Skip(1))
        {
            Assert.NotSame(first.Local.SpecializedBody, other.Local.SpecializedBody);
            Assert.NotSame(first.Local.Binding.LexicalContext!.Sources, other.Local.Binding.LexicalContext!.Sources);
            Assert.Throws<ArgumentException>(() => first.Analysis.Types.ValidateOwnership(
                other.Local.SpecializedBody!.Summary.ReturnType!));
        }
    }
}
