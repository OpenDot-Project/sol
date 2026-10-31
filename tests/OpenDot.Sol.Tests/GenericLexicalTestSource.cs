// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

internal static class GenericLexicalTestSource
{
    internal static (BodyAnalysis Analysis, GenericMemberCallProof Root) Member(string body,
        string declarations = "", string parameters = "value: T", string result = "T",
        string arguments = "value", string type = "int")
    {
        var analysis = GenericMemberProofTestSource.Analyze(declarations + "\nclass Root<T> {\n"
            + "func run(" + parameters + "): " + result + " {\n" + body + "\n}\n}\n"
            + "func use(owner: Root<" + type + ">, value: " + type + "): "
            + (result switch { "T" => type, "T?" => type + "?", "[]T" => "[]" + type, _ => result })
            + " = owner.run(" + arguments + ")");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        return (analysis, analysis.GetGenericMemberCallProof(call.Call)!);
    }

    internal static GenericMemberCallProof Local(GenericMemberCallProof root, string name = "local") =>
        Assert.Single(GenericMemberProofTestSource.Walk(root), proof => proof.Binding.Target.Name == name
            && !proof.IsRecursiveReference);

    internal static LexicalLocalFact Fact(GenericMemberCallProof local, string name) =>
        Assert.Single(local.Binding.LexicalContext!.Locals, fact => fact.Local.Name == name);

    internal static ImmutableArray<ExpressionBinding> Reads(GenericMemberCallProof local, string name) =>
        local.SpecializedBody!.Expressions.Where(binding => binding.Expression is AstAtom atom
            && atom.Spelling == name).ToImmutableArray();

    internal static void Bound(GenericMemberCallProof root)
    {
        Assert.True(root.Status == BodyBindingStatus.Bound,
            root.Status + ": " + string.Join("\n", GenericMemberProofTestSource.Walk(root)
                .SelectMany(proof => proof.SpecializedBody?.Diagnostics ?? [])
                .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.All(GenericMemberProofTestSource.Walk(root), proof =>
            Assert.Equal(BodyBindingStatus.Bound, proof.Status));
    }

    internal static void Invalid(BodyAnalysis analysis, GenericMemberCallProof root, string code)
    {
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal(BodyBindingStatus.Invalid, analysis.GetExpression(root.Binding.Call)!.Status);
        Assert.Contains(root.Binding.Call, analysis.UnverifiedNodes);
        Assert.False(analysis.IsComplete);
    }
}
