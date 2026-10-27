// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericOwnerMethodConstraintTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable {}\n"
        + "open class Exception: Throwable {}\nopen class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string source)
    {
        var declarations = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void MethodConstraintUsesClosedReceiverOwnerSubstitution()
    {
        var analysis = Analyze("interface Named {}\nclass Derived: Named {}\n"
            + "class Holder<T> { func <U> accept(value: U): U where U: T = value }\n"
            + "func use(holder: Holder<Named>, value: Derived): Derived = holder.accept(value)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = Assert.Single(analysis.Calls);
        Assert.Equal("accept", call.Target.Name);
        Assert.Equal("app.Derived", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("app.Named", Assert.Single(call.Receiver!.Arguments).Name);
        Assert.Equal("app.Derived", call.ReturnType!.Name);
    }

    [Fact]
    public void MethodConstraintStillRejectsAnArgumentOutsideTheOwnerBound()
    {
        var analysis = Analyze("interface Named {}\nclass Unrelated {}\n"
            + "class Holder<T> { func <U> accept(value: U): U where U: T = value }\n"
            + "func use(holder: Holder<Named>, value: Unrelated): Unrelated = holder.accept(value)");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.Empty(analysis.Calls);
    }
}
