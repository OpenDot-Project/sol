// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaReceiverBudgetTests
{
    [Fact]
    public void InheritedLookupBeyondDepthBudgetKeepsAnIncompleteReceiverCapture()
    {
        var sources = new List<string>
        {
            "module app",
            "open class C0 { val far: int = 1 }",
        };
        for (int index = 1; index <= 132; index++)
        {
            sources.Add($"open class C{index}: C{index - 1} {{ }}");
        }
        sources.Add("class Leaf: C132 { func callback(): () -> int = { far } }");

        var session = AstTestSources.Session(System.String.Join("\n", sources));
        var analysis = LexicalAnalysis.Create(session, TestContext.Current.CancellationToken);
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Leaf", capture.Owner.Name);
        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.False(capture.IsComplete);
        Assert.Empty(capture.Members);
        Assert.Equal("far", Assert.Single(capture.References).Spelling);
    }
}
