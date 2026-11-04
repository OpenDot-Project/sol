// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class NativeSourceSelectionTests
{
    [Fact]
    public void ValueBindingsRetainCanonicalPropertiesPrimaryStorageAndBackingFields()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C(var value: int) { var stored: int = 0;\n"
            + "var custom: int { get = field; set { field = value } } = 0;\n"
            + "func read(): int = this.value + this.stored + this.custom }\n"
            + "val top: int = 1; func use(input: C): int = input.stored + top");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var primary = analysis.Expressions.Single(binding => binding.Expression.Origin.Text == "this.value");
        Assert.Equal("value", primary.PrimaryProperty!.Name);
        Assert.Equal("C", primary.ValueOwner!.Declaration!.Name);
        var property = analysis.Expressions.Single(binding => binding.Expression.Origin.Text == "input.stored");
        Assert.Equal(SymbolKind.Property, property.ValueSymbol!.Kind);
        Assert.Equal("stored", property.ValueSymbol.Name);
        Assert.Same(primary.ValueOwner, property.ValueOwner);
        var global = analysis.Expressions.Single(binding => binding.Expression is AstAtom { Spelling: "top" });
        Assert.Equal("top", global.ValueSymbol!.Name); Assert.Null(global.ValueOwner);
        var fields = analysis.Expressions.Where(binding => binding.Expression is AstAtom { Spelling: "field" }).ToArray();
        Assert.NotEmpty(fields); Assert.All(fields, binding => Assert.Equal("custom", binding.BackingProperty!.Name));
        var input = analysis.Expressions.Single(binding => binding.Expression is AstAtom { Spelling: "input" });
        Assert.Equal(LocalSymbolKind.Parameter, input.ValueLocal!.Kind);
    }

    [Fact]
    public void IndexerSelectionKeepsNamedEvaluationOrderAndDefaultOriginsForBothOperations()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C { this[first: int, second: int = 2]: int { get = first + second; set {} } }\n"
            + "func use(c: C): int { c[second = 4, first = 3] = 1; c[first = 7] }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var uses = analysis.InterfaceUses.Where(pair => pair.Key.Site.SyntaxKind == NodeKind.IndexExpression).ToArray();
        var write = Assert.Single(uses, pair => pair.Key.Kind == InterfaceOperationKind.Set).Value;
        var read = Assert.Single(uses, pair => pair.Key.Kind == InterfaceOperationKind.Get).Value;
        Assert.Equal([1, 0], write.Arguments.Select(argument => argument.ParameterIndex));
        Assert.Empty(write.DefaultParameters);
        Assert.Equal([0], read.Arguments.Select(argument => argument.ParameterIndex));
        Assert.Equal([1], read.DefaultParameters);
        Assert.IsType<AstIndexer>(read.Selection.Implementation!.Member.Symbol.Parts[0]);
        Assert.Same(read.Selection.Implementation.Member.Symbol, write.Selection.Implementation!.Member.Symbol);
    }
}
