// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Cst;

public enum NodeKind
{
    CompilationUnit, ModuleDeclaration, ImportDeclaration, QualifiedName, Name,
    ObjectDeclaration, FunctionDeclaration, ParameterList, ParameterGroup, Type,
    Binding, Block, ReturnStatement, ThrowStatement, Assignment, ExpressionStatement,
    LiteralExpression, NameExpression, PositionalExpression, UnaryExpression,
    BinaryExpression, CallExpression, ArgumentList, MemberExpression, IndexExpression,
    TupleExpression, ArrayExpression, LambdaExpression, Error,
}

/// <summary>Snapshot-relative concrete structure; original tokens and trivia remain in SyntaxTree.</summary>
public sealed record SyntaxNode(NodeKind Kind, TextSpan Span, ImmutableArray<SyntaxNode> Children);
