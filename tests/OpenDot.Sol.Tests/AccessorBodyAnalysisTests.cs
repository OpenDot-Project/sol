// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AccessorBodyAnalysisTests
{
    [Fact]
    public void ClosedPropertyAndIndexerBodiesKeepExactReceiverValueAndParameterTypes()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Box<T>(seed: T) {\n"
            + "var value: T { get = field; set { field = value } } = seed;\n"
            + "this[index: int]: T { get = this.value; set { this.value = value } } }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var definition = analysis.Sources.Declarations.Modules["app"].Declarations.Single();
        var declaration = Assert.IsType<AstTypeDeclaration>(definition.Parts[0]);
        var property = declaration.Members.OfType<AstProperty>().Single();
        var indexer = declaration.Members.OfType<AstIndexer>().Single();
        var getter = property.Accessors.Single(accessor => accessor.Name == "get");
        var integer = analysis.Types.GetPrimitive("int");
        var boolean = analysis.Types.GetPrimitive("bool");
        foreach (var argument in new[] { integer, boolean })
        {
            var owner = analysis.Types.Nominal(definition, [argument]);
            var body = analysis.GetAccessorBody(owner, getter, TestContext.Current.CancellationToken);
            Assert.True(body.IsComplete, string.Join('\n', body.Diagnostics.Select(d => d.Message)));
            Assert.Same(argument, body.Summary.ReturnType);
            var field = Assert.Single(body.Expressions.Values, binding => binding.BackingProperty == property);
            Assert.Same(argument, field.Type); Assert.Same(owner, field.ValueOwner);
            Assert.Same(body, analysis.GetAccessorBody(owner, getter, TestContext.Current.CancellationToken));
            foreach (var accessor in indexer.Accessors)
            {
                var indexed = analysis.GetAccessorBody(owner, accessor, TestContext.Current.CancellationToken);
                Assert.True(indexed.IsComplete, string.Join('\n', indexed.Diagnostics.Select(d => d.Message)));
                Assert.All(indexed.LocalTypes.Where(pair => pair.Key.Name == "index"), pair => Assert.Same(integer, pair.Value));
                if (accessor.Name == "get") { Assert.Same(argument, indexed.Summary.ReturnType); }
                else { Assert.Contains(indexed.LocalTypes, pair => pair.Key.Kind == LocalSymbolKind.AccessorValue && pair.Value == argument); }
            }
        }
        var ordinary = analysis.GetExpression(Assert.IsType<AstExpression>(getter.Body.Node, exactMatch: false));
        Assert.Equal(ResolvedTypeKind.Parameter, ordinary!.Type!.Kind);
    }

    [Fact]
    public void CachedAccessorStillChecksCancellationAndCanonicalWorkspaceOwner()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C { val value: int { get = 1 } }\nclass Other {}");
        var definition = analysis.Sources.Declarations.Modules["app"].Declarations.Single(symbol => symbol.Name == "C");
        var owner = analysis.Types.Nominal(definition, []);
        var accessor = definition.Parts[0].Descendants().OfType<AstAccessor>().Single();
        Assert.True(analysis.GetAccessorBody(owner, accessor, TestContext.Current.CancellationToken).IsComplete);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => analysis.GetAccessorBody(owner, accessor, cancelled.Token));
        var other = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(s => s.Name == "Other"), []);
        Assert.Throws<ArgumentException>(() => analysis.GetAccessorBody(other, accessor, TestContext.Current.CancellationToken));
        var foreign = GenericMemberProofTestSource.Analyze("class C { val value: int { get = 1 } }");
        var foreignType = foreign.Types.Nominal(foreign.Sources.Declarations.Modules["app"].Declarations.Single(), []);
        Assert.Throws<ArgumentException>(() => analysis.GetAccessorBody(foreignType, accessor, TestContext.Current.CancellationToken));
    }
}
