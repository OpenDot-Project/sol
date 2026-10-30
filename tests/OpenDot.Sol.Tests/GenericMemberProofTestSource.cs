// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

internal static class GenericMemberProofTestSource
{
    internal const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    internal static BodyAnalysis Analyze(string declarations)
    {
        var session = AstTestSources.Session("module app\n" + declarations, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    internal static AstFunction Function(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstFunction>()
            .Single(function => function.Name == name);

    internal static BodyCallBinding ClosedCall(BodyAnalysis analysis, string name) =>
        Assert.Single(analysis.Calls, binding => binding.Target.Name == name
            && binding.Receiver is { } receiver && receiver.Arguments.All(IsClosed));

    internal static bool IsClosed(ResolvedType type) => type.Kind != ResolvedTypeKind.Parameter
        && type.Arguments.All(IsClosed) && type.Effects.All(IsClosed);

    internal static SpecializedBodyAnalysis Specialize(BodyAnalysis analysis, string name, string primitive) =>
        analysis.GetGenericBody(Function(analysis, name), [analysis.Types.GetPrimitive(primitive)],
            TestContext.Current.CancellationToken);

    internal static ImmutableArray<GenericMemberCallProof> Walk(GenericMemberCallProof proof)
    {
        var result = ImmutableArray.CreateBuilder<GenericMemberCallProof>();
        var queue = new System.Collections.Generic.Queue<GenericMemberCallProof>();
        queue.Enqueue(proof);
        while (queue.TryDequeue(out var current))
        {
            result.Add(current);
            foreach (var dependency in current.Dependencies) { queue.Enqueue(dependency); }
        }
        return result.ToImmutable();
    }
}
