// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionAstSnapshotCloneTests
{
    [Fact]
    public void SamePsiBuildsIndependentFunctionReferencesWithIdenticalSerializedProjection()
    {
        const string source = "module clone.contract\n"
            + "val callback = func(first, second: String = \"fallback\", ref source: String, out target: int): String "
            + "throws IOException, RuntimeException = source";
        var syntax = SyntaxTree.Parse(SourceText.FromString(source),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(syntax.HasErrors);
        var psi = PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken);
        var first = AstSnapshot.Create(psi, TestContext.Current.CancellationToken);
        var second = AstSnapshot.Create(psi, TestContext.Current.CancellationToken);
        var firstFunction = Assert.Single(first.Nodes.OfType<AstAnonymousFunction>());
        var secondFunction = Assert.Single(second.Nodes.OfType<AstAnonymousFunction>());

        AstVerifier.Verify(first, TestContext.Current.CancellationToken);
        AstVerifier.Verify(second, TestContext.Current.CancellationToken);
        Assert.NotSame(firstFunction, secondFunction);
        Assert.Equal(firstFunction.Id, secondFunction.Id);
        Assert.Same(firstFunction.Origin.Element, secondFunction.Origin.Element);
        Assert.NotSame(firstFunction.Body.Node, secondFunction.Body.Node);
        Assert.NotSame(firstFunction.ReturnType, secondFunction.ReturnType);
        Assert.Equal(new[] { "IOException", "RuntimeException" },
            firstFunction.ThrownTypes.Select(type => type.Shape.CanonicalName));
        Assert.Equal(firstFunction.ThrownTypes.Select(type => type.Shape.CanonicalName),
            secondFunction.ThrownTypes.Select(type => type.Shape.CanonicalName));

        Assert.Equal(4, firstFunction.Parameters.Length);
        Assert.Same(firstFunction.Parameters[0].NameOrigin.Element, secondFunction.Parameters[0].NameOrigin.Element);
        Assert.Same(firstFunction.Parameters[0].GroupOrigin.Element, firstFunction.Parameters[1].GroupOrigin.Element);
        Assert.Same(secondFunction.Parameters[0].GroupOrigin.Element, secondFunction.Parameters[1].GroupOrigin.Element);
        Assert.Same(firstFunction.Parameters[0].Type, firstFunction.Parameters[1].Type);
        Assert.Same(secondFunction.Parameters[0].Type, secondFunction.Parameters[1].Type);
        Assert.NotSame(firstFunction.Parameters[0].Type, secondFunction.Parameters[0].Type);
        Assert.Same(firstFunction.Parameters[0].DefaultValue, firstFunction.Parameters[1].DefaultValue);
        Assert.Same(secondFunction.Parameters[0].DefaultValue, secondFunction.Parameters[1].DefaultValue);
        Assert.NotSame(firstFunction.Parameters[0].DefaultValue, secondFunction.Parameters[0].DefaultValue);

        Assert.Same(firstFunction, first.GetParent(firstFunction.Body.Node!));
        Assert.Same(secondFunction, second.GetParent(secondFunction.Body.Node!));
        Assert.Same(firstFunction, first.GetParent(firstFunction.ReturnType!));
        Assert.Same(secondFunction, second.GetParent(secondFunction.ReturnType!));
        Assert.All(firstFunction.ThrownTypes, type => Assert.Same(firstFunction, first.GetParent(type)));
        Assert.All(secondFunction.ThrownTypes, type => Assert.Same(secondFunction, second.GetParent(type)));
        Assert.Throws<System.ArgumentException>(() => first.ValidateOwnership(secondFunction.Body.Node!));
        Assert.Throws<System.ArgumentException>(() => first.ValidateOwnership(secondFunction.ReturnType!));
        Assert.Throws<System.ArgumentException>(() => first.ValidateOwnership(secondFunction.Parameters[0].Type!));
        Assert.Equal(AstJson.Serialize(first, TestContext.Current.CancellationToken),
            AstJson.Serialize(second, TestContext.Current.CancellationToken));
    }
}
