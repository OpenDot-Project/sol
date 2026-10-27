// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericOpenTemplateDeferralTests
{
    [Fact]
    public void OperatorBindingThatDependsOnAnOpenFunctionParameterIsDeferred()
    {
        var session = AstTestSources.Session("module app\nfunc <T> combine(left, right: T): T = left + right");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var function = session.Files.SelectMany(file => file.Nodes).OfType<AstFunction>()
            .Single(declaration => declaration.Name == "combine");

        Assert.False(analysis.HasErrors, string.Join("\n", analysis.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Equal(BodyBindingStatus.Deferred, analysis.GetBody(function)!.Status);
        Assert.Contains(analysis.UnverifiedNodes, node => node is AstOperation operation
            && operation.Operation == "+");
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2207");
    }
}
