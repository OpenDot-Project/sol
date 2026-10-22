// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ControlFlowTests
{
    [Theory]
    [InlineData("if ready { work() }")]
    [InlineData("if ready { first() } else { second() }")]
    [InlineData("if a { first() } else if b { second() } else { third() }")]
    [InlineData("if (test() { $0 }) { work() }")]
    [InlineData("for { break }")]
    [InlineData("for active { continue }")]
    [InlineData("for i < 10 { i += 1 }")]
    [InlineData("for var i: int = 0; i < 10; i += 1 { work(i) }")]
    [InlineData("for var i: int = 0\ni < 10\ni += 1 { work(i) }")]
    [InlineData("for val item in items { use(item) }")]
    [InlineData("for var item in items { use(item) }")]
    [InlineData("for val (key, _) in pairs { use(key) }")]
    [InlineData("val (first, second) = pair()")]
    [InlineData("switch value { case 0: return\ncase 1: work()\ndefault: other() }")]
    [InlineData("switch value { case Command.Stop: return\ncase Command.Move(x, _): use(x) }")]
    [InlineData("switch value { case is Failure: handle()\ndefault: return }")]
    [InlineData("switch value { case Move(x, y): use(x, y) }")]
    [InlineData("try { work() } catch (error: IOException) { handle(error) }")]
    [InlineData("try { work() } finally { cleanup() }")]
    [InlineData("try { work() } catch (error: A) { first() } catch (error: B) { second() } finally { cleanup() }")]
    [InlineData("val result = try { read() } catch (error: IOException) { fallback() }")]
    [InlineData("defer { close() }\nunsafe { nativeCall() }")]
    [InlineData("val callback = { x: int, y: int -> if x > y { return@done x }\nx + y }")]
    [InlineData("val callback = done@ { ref x: int, out y: int -> y = x\nreturn@done }")]
    [InlineData("val callback = { -> 42 }")]
    [InlineData("val callback = { f: (int) -> int -> f(1) }")]
    [InlineData("val callback: (ref int, out int) -> bool = { ref x: int, out y: int -> y = x\ntrue }")]
    [InlineData("val callback = func(x: int): int { return x + 1 }")]
    [InlineData("val callback = func(): () throws IOException { throw IOException(\"x\") }")]
    [InlineData("val result = combine(20, 22) { x, y -> x + y }")]
    [InlineData("val result = value ?: throw Failure(\"missing\")")]
    [InlineData("val result = (value as? Item) ?: fallback()\nval check = value !is Item")]
    [InlineData("val result = value is Item && ready\nval cast = value as Item")]
    [InlineData("for active { value++\nnext()\nvalue--\ncontinue }")]
    [InlineData("swap(left = ref a, right = ref b)\ntryRead(value = out var parsed)\nuse(*values)")]
    public void ControlAndFunctionSyntaxParses(string body)
    {
        var tree = ParseBody(body);
        Assert.Empty(tree.Diagnostics);
        Assert.False(tree.HasErrors);
        AssertLossless(tree);
        CstWriter.Serialize(tree);
    }

    [Theory]
    [InlineData("if ready work()")]
    [InlineData("if ready\n{ work() }")]
    [InlineData("if ready {}\nelse {}")]
    [InlineData("if ready {} /*\n*/ else {}")]
    [InlineData("if ready {} else work()")]
    [InlineData("for\n{}")]
    [InlineData("for active\n{}")]
    [InlineData("for var i = 0; ; i += 1 {}")]
    [InlineData("for var i = 0; i < 10; {}")]
    [InlineData("for var i = 0; i < 10; i + 1 {}")]
    [InlineData("for val item: int in items {}")]
    [InlineData("for val item = 1 in items {}")]
    [InlineData("for val () in items {}")]
    [InlineData("for val (a,) in items {}")]
    [InlineData("switch value\n{ default: return }")]
    [InlineData("switch value { work() }")]
    [InlineData("switch value { case 1 return }")]
    [InlineData("switch value { case is : return }")]
    [InlineData("switch value { case Command.Move(Nested(x)): return }")]
    [InlineData("switch value { case Command.Move(1, 2): return }")]
    [InlineData("switch value { case Command.Move\n(x): return }")]
    [InlineData("try { work() }")]
    [InlineData("try work() catch (error: A) {}")]
    [InlineData("try {} catch error: A {}")]
    [InlineData("try {} catch (error) {}")]
    [InlineData("try {} finally work()")]
    [InlineData("defer close()")]
    [InlineData("unsafe\n{}")]
    [InlineData("val bad = if ready { 1 } else { 2 }")]
    [InlineData("val bad = [ref x]")]
    [InlineData("val bad = (out x, 1)")]
    [InlineData("val bad: (ref int) = value")]
    [InlineData("call(out var)")]
    [InlineData("val bad = { x, -> x }")]
    [InlineData("val bad = label@ call()")]
    public void MalformedSyntaxReportsBoundedSourceDiagnostics(string body)
    {
        var tree = ParseBody(body, errorLimit: 8);
        Assert.True(tree.HasErrors);
        Assert.InRange(tree.Diagnostics.Length, 1, 8);
        Assert.All(tree.Diagnostics, diagnostic => tree.Source.Validate(diagnostic.Span));
        AssertLossless(tree);
        CstWriter.Serialize(tree);
    }

    [Fact]
    public void AllFourForHeadersHaveDistinctStructure()
    {
        var tree = ParseBody("for {}\nfor ready {}\nfor var i = 0; i < 2; i += 1 {}\nfor val (key, _) in pairs {}");
        Assert.Empty(tree.Diagnostics);
        var loops = Descendants(tree.Root).Where(n => n.Kind == NodeKind.ForStatement).ToArray();
        Assert.Equal(4, loops.Length);
        Assert.Equal(new[] { NodeKind.Block }, loops[0].Children.Select(n => n.Kind));
        Assert.Equal(new[] { NodeKind.ForCondition, NodeKind.Block }, loops[1].Children.Select(n => n.Kind));
        Assert.Equal(new[] { NodeKind.ForInitializer, NodeKind.ForCondition, NodeKind.ForUpdate, NodeKind.Block },
            loops[2].Children.Select(n => n.Kind));
        Assert.Equal(NodeKind.ForEachBinding, loops[3].Children[0].Kind);
        var names = Descendants(loops[3].Children[0]).Where(n => n.Kind == NodeKind.Name);
        Assert.Equal(new[] { "key", "_" }, names.Select(n => tree.Source.GetText(n.Span)));
    }

    [Fact]
    public void SwitchPatternsAndCasesRetainIndependentBodies()
    {
        var tree = ParseBody("switch command { case Command.Move(x, _): use(x)\ncase is Failure: fail()\ndefault: fallback() }");
        Assert.Empty(tree.Diagnostics);
        var dispatch = Descendants(tree.Root).Single(n => n.Kind == NodeKind.SwitchStatement);
        Assert.Equal(4, dispatch.Children.Length);
        Assert.Equal(NodeKind.PayloadPattern, dispatch.Children[1].Children[0].Kind);
        Assert.Equal(NodeKind.TypePattern, dispatch.Children[2].Children[0].Kind);
        Assert.All(dispatch.Children.Skip(1), branch => Assert.Equal(NodeKind.SwitchCase, branch.Kind));
        Assert.Equal("default: fallback()", tree.Source.GetText(dispatch.Children[3].Span).Trim());
    }

    [Fact]
    public void TryResultAndHandlersAreDistinctNodes()
    {
        var tree = ParseBody("val result = try { 1 } catch (error: Failure) { 2 } finally { close() }");
        Assert.Empty(tree.Diagnostics);
        var result = Descendants(tree.Root).Single(n => n.Kind == NodeKind.TryExpression);
        Assert.Equal(new[] { NodeKind.Block, NodeKind.CatchClause, NodeKind.FinallyClause }, result.Children.Select(n => n.Kind));
        Assert.Equal("error", tree.Source.GetText(result.Children[1].Children[0].Span));
    }

    [Fact]
    public void ArgumentsPreserveNamesModesSpreadAndOutDeclarations()
    {
        var tree = ParseBody("call(left = ref a, right = out b, parsed = out var value, *items)");
        Assert.Empty(tree.Diagnostics);
        var args = Descendants(tree.Root).Single(n => n.Kind == NodeKind.ArgumentList).Children;
        Assert.Equal(new[] { NodeKind.RefArgument, NodeKind.OutArgument, NodeKind.OutVariable, NodeKind.SpreadArgument },
            args.Select(n => n.Children[^1].Kind));
        Assert.Equal("parsed", tree.Source.GetText(args[2].Children[0].Span));
        Assert.Equal("value", tree.Source.GetText(args[2].Children[1].Children[0].Span));
    }

    [Fact]
    public void TypedLambdaAndLocalReturnRetainTheirHeaderAndLabel()
    {
        var tree = ParseBody("val callback = done@ { ref x: int, out y: int -> y = x\nreturn@done }");
        Assert.Empty(tree.Diagnostics);
        var lambda = Descendants(tree.Root).Single(n => n.Kind == NodeKind.LambdaExpression);
        Assert.Equal(NodeKind.LambdaParameters, lambda.Children[0].Kind);
        Assert.Equal(2, lambda.Children[0].Children.Length);
        Assert.StartsWith("ref x", tree.Source.GetText(lambda.Children[0].Children[0].Span), StringComparison.Ordinal);
        var localReturn = Descendants(lambda).Single(n => n.Kind == NodeKind.ReturnStatement);
        Assert.Equal("done", tree.Source.GetText(Assert.Single(localReturn.Children).Span));
    }

    [Fact]
    public void ReturnNewlineDoesNotAbsorbFollowingExpression()
    {
        var tree = ParseBody("return\nvalue");
        Assert.Empty(tree.Diagnostics);
        var block = tree.Root.Children[1].Children[^1];
        Assert.Equal(new[] { NodeKind.ReturnStatement, NodeKind.ExpressionStatement }, block.Children.Select(n => n.Kind));
        Assert.Empty(block.Children[0].Children);
    }

    [Fact]
    public void MissingTryBraceRecoversAtCatchAndPreservesFollowingFunction()
    {
        var tree = ParseBody("try { work()\ncatch (error: Failure) { handle() }", suffix: "\nfunc after() {}\n");
        Assert.True(tree.HasErrors);
        Assert.Contains(Descendants(tree.Root), n => n.Kind == NodeKind.CatchClause);
        Assert.Equal(2, tree.Root.Children.Count(n => n.Kind == NodeKind.FunctionDeclaration));
    }

    [Fact]
    public void DeepElseIfChainsRespectNestingBudget()
    {
        var body = string.Concat(Enumerable.Repeat("if ready {} else ", 1000)) + "{}";
        var tree = ParseBody(body, errorLimit: 10);
        Assert.Contains(tree.Diagnostics, d => d.Code == "SOL1104");
        Assert.InRange(tree.Diagnostics.Length, 1, 10);
        CstWriter.Serialize(tree);
    }

    [Fact]
    public void DestructuringDoesNotTurnIntoATopLevelPropertyProduction()
    {
        var tree = SyntaxTree.Parse(SourceText.FromString("module app\nval (a, b) = pair()"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(tree.HasErrors);
    }

    [Fact]
    public void CastPrecedenceAndTrailingLambdaStructureArePreserved()
    {
        var tree = ParseBody("val value = a * b as Number + c\nval callback = call(1) { x: int -> x }");
        Assert.Empty(tree.Diagnostics);
        var value = Descendants(tree.Root).First(n => n.Kind == NodeKind.Binding).Children[1];
        Assert.Equal(NodeKind.BinaryExpression, value.Kind);
        Assert.Equal(NodeKind.CastExpression, value.Children[0].Children[1].Kind);
        var call = Descendants(tree.Root).Single(n => n.Kind == NodeKind.CallExpression);
        Assert.Equal(new[] { NodeKind.NameExpression, NodeKind.ArgumentList, NodeKind.LambdaExpression }, call.Children.Select(n => n.Kind));
    }

    [Fact]
    public void RandomControlTokensKeepRecoveryBounded()
    {
        string[] pieces = ["if", "else", "for", "switch", "case", "default", "try", "catch", "finally", "defer",
            "unsafe", "return", "break", "continue", "ref", "out", "val", "var", "in", "x", "1", "{", "}", "(", ")", ":", ";", "=", ",", "->"];
        var random = new Random(20261020);
        for (int sample = 0; sample < 256; sample++)
        {
            string body = string.Join(' ', Enumerable.Range(0, sample).Select(_ => pieces[random.Next(pieces.Length)]));
            var tree = ParseBody(body, errorLimit: 12);
            Assert.InRange(tree.Diagnostics.Length, 0, 12);
            AssertLossless(tree);
            CstWriter.Serialize(tree);
        }
    }

    [Fact]
    public void TokenDeletionMutationsAlwaysTerminateAndPreserveInput()
    {
        const string text = "module mutations\nfunc f() { for val (key, _) in pairs { if ready { use(key) } else { continue } }\n"
            + "val answer = try { compute() { x: int -> x + 1 } } catch (error: Failure) { throw error } finally { close() }\n"
            + "switch answer { case Result.Value(x): consume(x)\ndefault: return } }";
        var source = SourceText.FromString(text);
        var tokens = Lexer.Lex(source, cancellationToken: TestContext.Current.CancellationToken).Tokens;
        foreach (var token in tokens.Where(token => token.Span.Length > 0))
        {
            string mutated = text.Remove(token.Span.Start, token.Span.Length);
            var tree = SyntaxTree.Parse(SourceText.FromString(mutated), errorLimit: 12,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.InRange(tree.Diagnostics.Length, 0, 12);
            AssertLossless(tree);
            var second = SyntaxTree.Parse(tree.Source, errorLimit: 12, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(CstWriter.Serialize(tree), CstWriter.Serialize(second));
            Assert.Equal(tree.Diagnostics.Select(d => (d.Code, d.Span)), second.Diagnostics.Select(d => (d.Code, d.Span)));
        }
    }

    private static SyntaxTree ParseBody(string body, int errorLimit = 100, string suffix = "") =>
        SyntaxTree.Parse(SourceText.FromString("module control.flow\nfunc test() {\n" + body + "\n}\n" + suffix),
            errorLimit, cancellationToken: TestContext.Current.CancellationToken);

    private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(node);
        while (stack.TryPop(out var next))
        {
            yield return next;
            foreach (var child in next.Children.Reverse()) { stack.Push(child); }
        }
    }

    private static void AssertLossless(SyntaxTree tree)
    {
        int cursor = 0;
        foreach (var token in tree.Tokens)
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                Assert.Equal(cursor, trivia.Span.Start);
                cursor = trivia.Span.End;
            }
            Assert.Equal(cursor, token.Span.Start);
            cursor = token.Span.End;
        }
        Assert.Equal(tree.Source.Length, cursor);
    }
}
