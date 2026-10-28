// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class InterfaceEscapingParameterContractTests
{
    [Fact]
    public void InterfaceNonescapingCallbackContractAllowsBorrowedArgument()
    {
        var analysis = Analyze("module app\ninterface Runner { func invoke(callback: () -> int): int }\n"
            + "class LocalRunner: Runner { override func invoke(callback: () -> int): int = callback() }\n"
            + "func use(runner: Runner, ref value: int): int = runner.invoke({ -> value })");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.NonescapingCallArgument);
    }

    [Fact]
    public void InterfaceEscapingCallbackContractRejectsBorrowedArgument()
    {
        var analysis = Analyze("module app\ninterface Runner { func retain(@escaping callback: () -> int): int }\n"
            + "class LocalRunner: Runner { override func retain(@escaping callback: () -> int): int = callback() }\n"
            + "func use(runner: Runner, ref value: int): int = runner.retain({ -> value })");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void InterfaceImplementationMustPreserveEscapingAnnotation()
    {
        var analysis = Analyze("module app\ninterface Runner { func invoke(@escaping callback: () -> int): int }\n"
            + "class LocalRunner: Runner { override func invoke(callback: () -> int): int = callback() }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2265");
    }

    [Fact]
    public void ConstructedGenericInterfaceReceiverKeepsNonescapingContract()
    {
        var analysis = Analyze("module app\ninterface Runner<T> { func invoke(callback: () -> T): T }\n"
            + "func use(runner: Runner<int>, ref value: int): int = runner.invoke({ -> value })");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.NonescapingCallArgument);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var declarations = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(declarations.HasErrors, string.Join('\n', declarations.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(declarations, TestContext.Current.CancellationToken);
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics
        .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
