// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericObjectMethodSpecializationDeterminismTests
{
    private const string Declarations = "object Utility {\n"
        + "    func <T> identity(value: T): T = value\n"
        + "    func <T> relay(value: T): T = identity<T>(value)\n"
        + "}\n"
        + "object Main { func main(args: []String): () { BODY } }";

    private static FirCompilation Compile(string body) => FirCompilation.Create(
        [AstTestSources.Parse(Declarations.Replace("BODY", body, System.StringComparison.Ordinal))],
        TestContext.Current.CancellationToken);

    private static VerifiedFirProgram Valid(string body)
    {
        var compilation = Compile(body);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program;
    }

    private static string[] SpecializationOrder(VerifiedFirProgram program) => program.Program.Functions
        .Where(function => !function.SpecializationArguments.IsEmpty)
        .Select(Identity)
        .ToArray();

    private static string[] SpecializationIdentitySet(VerifiedFirProgram program) =>
        SpecializationOrder(program).OrderBy(identity => identity, System.StringComparer.Ordinal).ToArray();

    private static string Identity(FirFunction function) => function.Name + "<"
        + string.Join(",", function.SpecializationArguments.Select(type => type.Name)) + ">";

    [Fact]
    public void IdenticalCompilationInputsKeepClosedMethodOrderAndSerializedBytesStable()
    {
        const string body = "val number = Utility.identity<int>(1); val text = Utility.relay<String>(\"x\")";
        var first = Valid(body);
        var second = Valid(body);

        Assert.Equal(SpecializationOrder(first), SpecializationOrder(second));
        Assert.Equal(Encoding.UTF8.GetBytes(FirJson.Serialize(first, TestContext.Current.CancellationToken)),
            Encoding.UTF8.GetBytes(FirJson.Serialize(second, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public void ReorderingCallRootsPreservesClosedMethodIdentitySet()
    {
        var forward = Valid("val number = Utility.identity<int>(1); val text = Utility.relay<String>(\"x\")");
        var reversed = Valid("val text = Utility.relay<String>(\"x\"); val number = Utility.identity<int>(1)");

        var expected = new[]
        {
            "identity<int>",
            "identity<std.core.String>",
            "relay<std.core.String>",
        };
        Assert.Equal(expected, SpecializationIdentitySet(forward));
        Assert.Equal(expected, SpecializationIdentitySet(reversed));
    }
}
