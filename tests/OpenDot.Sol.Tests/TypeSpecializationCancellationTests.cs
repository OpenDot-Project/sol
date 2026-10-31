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

public sealed class TypeSpecializationCancellationTests
{
    [Theory]
    [InlineData("substitute")]
    [InlineData("bases")]
    [InlineData("conversion")]
    [InlineData("declaration-constraints")]
    [InlineData("constructed-constraints")]
    public void CancellationInterruptsWaitForTheSpecializationWorkspace(string operation)
    {
        var (specialization, function, parameter) = CreateWorkspace();
        var integer = specialization.GetPrimitive("int");
        var gateField = typeof(TypeSpecialization).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(TypeSpecialization).FullName, "_gate");
        var gate = gateField.GetValue(specialization)!;
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Thread? workerThread = null;
        Task? waitingRequest = null;

        Monitor.Enter(gate);
        try
        {
            var request = Task.Factory.StartNew(() =>
            {
                workerThread = Thread.CurrentThread;
                started.Set();
                Execute(operation, specialization, function, parameter, integer, cancellation.Token);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            waitingRequest = request;

            Assert.True(SpinWait.SpinUntil(() => started.IsSet, TimeSpan.FromSeconds(2)),
                "The specialization waiter did not start.");
            Assert.True(SpinWait.SpinUntil(() => workerThread is { } thread
                && (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2)),
                "The worker did not reach the held specialization gate.");

            cancellation.Cancel();
            Assert.True(SpinWait.SpinUntil(() => request.IsCompleted, TimeSpan.FromSeconds(2)),
                "A canceled specialization request remained blocked on the gate.");
            Assert.True(request.IsFaulted, "The waiting request did not observe cancellation.");
            Assert.IsType<OperationCanceledException>(request.Exception?.GetBaseException());
        }
        finally
        {
            cancellation.Cancel();
            Monitor.Exit(gate);
            if (waitingRequest is not null)
            {
                Assert.True(SpinWait.SpinUntil(() => waitingRequest.IsCompleted, TimeSpan.FromSeconds(2)),
                    "The specialization worker did not finish after the gate was released.");
            }
        }
    }

    private static void Execute(string operation, TypeSpecialization specialization, AstFunction function,
        ResolvedType parameter, ResolvedType integer, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "substitute":
                specialization.Substitute(parameter, new System.Collections.Generic.Dictionary<ResolvedType, ResolvedType>(), cancellationToken);
                break;
            case "bases":
                specialization.GetDirectBases(integer, cancellationToken);
                break;
            case "conversion":
                specialization.ClassifyConversion(integer, integer, cancellationToken);
                break;
            case "declaration-constraints":
                specialization.CheckConstraints(function, [integer], cancellationToken);
                break;
            case "constructed-constraints":
                specialization.CheckTypeConstraints(integer, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static (TypeSpecialization Specialization, AstFunction Function, ResolvedType Parameter) CreateWorkspace()
    {
        var session = AstTestSources.Session("module app\nfunc <T> identity(value: T): T = value");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var function = session.Files.SelectMany(file => file.Nodes).OfType<AstFunction>()
            .Single(declaration => declaration.Name == "identity");
        var specialization = new TypeSpecialization(sources.Types);
        var parameter = sources.Types.GetTypeParameters(function).Single();
        return (specialization, function, parameter);
    }
}
