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
    IfStatement, ForStatement, ForCondition, ForInitializer, ForUpdate, ForEachBinding,
    SwitchStatement, SwitchCase, TypePattern, PayloadPattern, PatternBindings,
    TryStatement, TryExpression, CatchClause, FinallyClause, DeferStatement, UnsafeStatement,
    BreakStatement, ContinueStatement, Destructuring, LambdaParameters, LambdaParameter,
    LabelledExpression, AnonymousFunctionExpression, ThrowExpression, TypeTestExpression, CastExpression,
    Argument, RefArgument, OutArgument, OutVariable, SpreadArgument, RefExpression,
    Annotation, FileAnnotation, AnnotationTarget, Modifier, PassingMode, OperatorName,
    ClassDeclaration, StructDeclaration, RefStructDeclaration, InterfaceDeclaration, EnumDeclaration,
    AnnotationDeclaration, EnumVariant, TypeBody, PrimaryConstructor, ConstructorDeclaration,
    ConstructorDelegation, InitDeclaration, BaseList, BaseType, Delegation,
    PropertyDeclaration, AccessorList, AccessorDeclaration, IndexerDeclaration, ReturnType,
    TypeParameterList, TypeParameter, TypeArgumentList, WhereClause, Constraint, AntiConstraint,
    ThrowsClause, ExternDeclaration, GenericExpression,
}

/// <summary>Snapshot-relative concrete structure; original tokens and trivia remain in SyntaxTree.</summary>
public sealed record SyntaxNode(NodeKind Kind, TextSpan Span, ImmutableArray<SyntaxNode> Children);
