// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Globalization;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalSnapshotStabilityTests
{
    [Fact]
    public void LaterQueriesCannotMutatePublishedLexicalCallFacts()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("val captured = value\n"
            + "func <U> local(item: U): U = item\nlocal<int>(1)\nvalue");
        var local = GenericLexicalTestSource.Local(root);
        var context = local.Binding.LexicalContext!;
        var identity = context.Identity;
        var locals = context.Locals;
        var expressions = local.SpecializedBody!.Expressions;
        var summary = local.SpecializedBody.Summary;
        var other = analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            [analysis.Types.GetPrimitive("long")], context, TestContext.Current.CancellationToken);
        Assert.Equal("long", other.Summary.ReturnType!.Name);
        Assert.Equal(identity, context.Identity);
        Assert.Equal(locals, context.Locals);
        Assert.Equal(expressions, local.SpecializedBody.Expressions);
        Assert.Same(summary, local.SpecializedBody.Summary);
        Assert.Equal("int", summary.ReturnType!.Name);
    }

    [Fact]
    public void ContextIdentityDoesNotDependOnCurrentCulture()
    {
        var (_, root) = GenericLexicalTestSource.Member("val captured = value\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        var context = GenericLexicalTestSource.Local(root).Binding.LexicalContext!;
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = context.Identity;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var arabic = context.Identity;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(turkish, arabic);
            Assert.Equal(turkish, context.Identity);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public void DeclarationOrderIsPreservedAfterDependencyPropagation()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var body = local.SpecializedBody!;
        Assert.Equal(body.Expressions.OrderBy(item => item.Expression.Origin.Source.Path,
            System.StringComparer.Ordinal).ThenBy(item => item.Expression.Origin.Span.Start), body.Expressions);
        Assert.Equal(body.Bodies.OrderBy(item => item.Declaration.Origin.Source.Path,
            System.StringComparer.Ordinal).ThenBy(item => item.Declaration.Origin.Span.Start), body.Bodies);
        Assert.Equal(analysis.Diagnostics.OrderBy(item => item.Source.Path, System.StringComparer.Ordinal)
            .ThenBy(item => item.Span.Start).ThenBy(item => item.Code, System.StringComparer.Ordinal), analysis.Diagnostics);
        Assert.Equal(local.Binding.LexicalContext!.Locals.OrderBy(fact => fact.Local.Id),
            local.Binding.LexicalContext.Locals);
    }

    [Fact]
    public void IdenticalSourceAnalysesProduceIdenticalProofInventories()
    {
        const string body = "val captured = value\nfunc <U> local(item: U): T = captured\nlocal<int>(1)";
        var (_, first) = GenericLexicalTestSource.Member(body);
        var (_, second) = GenericLexicalTestSource.Member(body);
        static string[] Inventory(GenericMemberCallProof root) => GenericMemberProofTestSource.Walk(root)
            .Select(proof => proof.Binding.Target.Name + ":" + proof.Status + ":"
                + proof.Binding.Call.Origin.Span.Start + ":" + proof.Binding.LexicalContext?.Identity).ToArray();
        Assert.Equal(Inventory(first), Inventory(second));
        Assert.NotSame(first.SpecializedBody, second.SpecializedBody);
        Assert.NotSame(GenericLexicalTestSource.Local(first).SpecializedBody,
            GenericLexicalTestSource.Local(second).SpecializedBody);
    }
}
