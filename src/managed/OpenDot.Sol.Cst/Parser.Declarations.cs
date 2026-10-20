// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using static OpenDot.Sol.Cst.SyntaxKind;

namespace OpenDot.Sol.Cst;

internal sealed partial class Parser
{
    private SyntaxNode ParseDeclaration(bool member = false)
    {
        int start = Start;
        if (!Enter()) { return Recover(start); }
        try
        {
            var prefix = ParsePrefix();
            SyntaxNode declaration;
            if (Kind == RefKeyword && Peek(1).Kind == StructKeyword)
            {
                Advance();
                declaration = ParseNominal(start, NodeKind.RefStructDeclaration);
            }
            else if (Kind == RefKeyword)
            {
                prefix.Add(ConsumeMarker(NodeKind.PassingMode));
                if (IsText("readonly")) { prefix.Add(ConsumeMarker(NodeKind.Modifier)); }
                declaration = Kind == ThisKeyword && member ? ParseIndexer(start) : ParseProperty(start);
            }
            else if (IsText("extern")) { declaration = ParseExtern(start); }
            else
            {
                declaration = Kind switch
                {
                    ImportKeyword => ParseImport(start),
                    ObjectKeyword or CompanionKeyword => ParseObject(start),
                    ClassKeyword => ParseNominal(start, NodeKind.ClassDeclaration),
                    StructKeyword => ParseNominal(start, NodeKind.StructDeclaration),
                    InterfaceKeyword => ParseNominal(start, NodeKind.InterfaceDeclaration),
                    EnumKeyword => ParseEnum(start),
                    AnnotationKeyword => ParseAnnotationDeclaration(start),
                    FuncKeyword => ParseFunction(start),
                    ValKeyword or VarKeyword or ConstKeyword => ParseProperty(start),
                    ConstructorKeyword when member => ParseConstructor(start),
                    InitKeyword when member => ParseInit(start),
                    ThisKeyword when member => ParseIndexer(start),
                    _ => Unsupported(start),
                };
            }
            return prefix.Count == 0 ? declaration : Node(declaration.Kind, start, prefix.Concat(declaration.Children));
        }
        finally { _depth--; }
    }

    private List<SyntaxNode> ParsePrefix()
    {
        var nodes = new List<SyntaxNode>();
        while (Kind == AtToken || IsModifier())
        {
            nodes.Add(Kind == AtToken ? ParseAnnotation() : ConsumeMarker(NodeKind.Modifier));
        }
        return nodes;
    }

    private bool IsModifier() => Kind is PublicKeyword or PrivateKeyword or InternalKeyword or ProtectedKeyword
        or PartialKeyword or FinalKeyword or OpenKeyword or AbstractKeyword or OverrideKeyword or DataKeyword
        or SealedKeyword or OperatorKeyword or UnsafeKeyword || IsText("readonly")
        && Peek(1).Kind is not (ColonToken or CommaToken or EqualsToken or CloseParenToken or CloseBracketToken);

    private SyntaxNode ConsumeMarker(NodeKind kind)
    {
        int start = Start;
        Advance();
        return Node(kind, start, []);
    }

    private SyntaxNode ParseAnnotation(bool file = false)
    {
        int start = Start;
        Expect(AtToken);
        var children = new List<SyntaxNode>();
        if ((Kind == IdentifierToken || (int)Kind >= (int)AbstractKeyword) && Peek(1).Kind == ColonToken)
        {
            int targetStart = Start;
            if (file && !IsText("file")) { Error("SOL1101", "Only file-target annotations can precede module."); }
            Advance();
            children.Add(Node(NodeKind.AnnotationTarget, targetStart, []));
            Advance();
        }
        else if (file) { Error("SOL1101", "A pre-module annotation requires the file target."); }
        children.Add(ParseName());
        if (Kind == OpenParenToken) { children.Add(ParseArguments()); }
        return Node(file ? NodeKind.FileAnnotation : NodeKind.Annotation, start, children);
    }

    private SyntaxNode ParseNominal(int start, NodeKind kind)
    {
        Advance();
        var children = new List<SyntaxNode> { ConsumeName() };
        if (Kind == LessToken) { children.Add(ParseTypeParameters()); }
        if (kind == NodeKind.ClassDeclaration && (Kind == OpenParenToken || IsVisibility() || Kind == ConstructorKeyword))
        {
            children.Add(ParsePrimaryConstructor());
        }
        if (Kind == ColonToken) { children.Add(ParseBases()); }
        if (Kind == WhereKeyword) { children.Add(ParseWhere()); }
        if (Kind == OpenBraceToken) { children.Add(ParseTypeBody()); }
        else if (kind != NodeKind.ClassDeclaration) { Error("SOL1101", "This type declaration requires a body."); }
        else { Terminate(); }
        Take(SemicolonToken);
        return Node(kind, start, children);
    }

    private SyntaxNode ParseObject(int start)
    {
        bool companion = Take(CompanionKeyword);
        Expect(ObjectKeyword);
        var children = new List<SyntaxNode>();
        if (!companion || Kind == IdentifierToken) { children.Add(ConsumeName()); }
        if (Kind == ColonToken) { children.Add(ParseBases()); }
        if (Kind == OpenBraceToken) { children.Add(ParseTypeBody()); }
        else { Error("SOL1101", "An object requires a body."); }
        Take(SemicolonToken);
        return Node(NodeKind.ObjectDeclaration, start, children);
    }

    private SyntaxNode ParseTypeBody()
    {
        int start = Start;
        RequireSameLineBrace();
        Expect(OpenBraceToken);
        var children = new List<SyntaxNode>();
        while (!AtEnd && Kind != CloseBraceToken)
        {
            if (Take(SemicolonToken)) { continue; }
            int before = _index;
            children.Add(ParseDeclaration(member: true));
            EnsureProgress(before);
        }
        Expect(CloseBraceToken);
        return Node(NodeKind.TypeBody, start, children);
    }

    private SyntaxNode ParseBases()
    {
        int start = Start;
        Expect(ColonToken);
        var bases = new List<SyntaxNode>();
        do
        {
            int baseStart = Start;
            var children = new List<SyntaxNode> { ParseType() };
            if (Kind == OpenParenToken) { children.Add(ParseArguments()); }
            else if (IsText("by"))
            {
                int delegateStart = Start;
                Advance();
                children.Add(Node(NodeKind.Delegation, delegateStart, [ParseExpression(allowTrailingLambda: false)]));
            }
            bases.Add(Node(NodeKind.BaseType, baseStart, children));
        } while (Take(CommaToken));
        return Node(NodeKind.BaseList, start, bases);
    }

    private SyntaxNode ParseEnum(int start)
    {
        Advance();
        var children = new List<SyntaxNode> { ConsumeName() };
        if (Kind == LessToken) { children.Add(ParseTypeParameters()); }
        RequireBodyBrace();
        if (!Take(OpenBraceToken)) { return Node(NodeKind.EnumDeclaration, start, children); }
        var variants = new List<SyntaxNode>();
        if (Kind == CloseBraceToken) { Error("SOL1101", "An enum requires at least one variant."); }
        while (!AtEnd && Kind != CloseBraceToken)
        {
            int before = _index;
            int variantStart = Start;
            var variant = new List<SyntaxNode> { ConsumeName() };
            if (Kind == OpenParenToken) { variant.Add(ParseParameters()); }
            variants.Add(Node(NodeKind.EnumVariant, variantStart, variant));
            EnsureProgress(before);
            if (!Take(CommaToken)) { break; }
        }
        Expect(CloseBraceToken);
        children.Add(Node(NodeKind.TypeBody, variants.Count == 0 ? start : variants[0].Span.Start, variants));
        Take(SemicolonToken);
        return Node(NodeKind.EnumDeclaration, start, children);
    }

    private SyntaxNode ParseAnnotationDeclaration(int start)
    {
        Advance();
        Expect(ClassKeyword);
        var children = new List<SyntaxNode> { ConsumeName() };
        if (Kind == OpenParenToken) { children.Add(ParsePrimaryConstructor()); }
        Terminate();
        return Node(NodeKind.AnnotationDeclaration, start, children);
    }

    private SyntaxNode ParseExtern(int start)
    {
        Advance();
        if (Kind != StringLiteralToken) { Error("SOL1101", "Extern requires the quoted c ABI name."); }
        else
        {
            if (lex.Source.GetText(Current.Span) != "\"c\"") { Error("SOL1101", "Only the c extern ABI is supported."); }
            Advance();
        }
        RequireBodyBrace();
        if (Kind != OpenBraceToken) { return Node(NodeKind.ExternDeclaration, start, []); }
        var body = ParseTypeBody();
        if (body.Children.Any(member => member.Kind != NodeKind.FunctionDeclaration || member.Children.Any(part =>
            part.Kind is not (NodeKind.Annotation or NodeKind.Name or NodeKind.ParameterList or NodeKind.ReturnType))))
        {
            Error("SOL1101", "An extern block contains only annotated function signatures without bodies.");
        }
        return Node(NodeKind.ExternDeclaration, start, [body]);
    }
    private SyntaxNode ParseImport(int start)
    {
        Advance();
        var children = new List<SyntaxNode>();
        if (Kind == IdentifierToken && Peek(1).Kind == EqualsToken)
        {
            children.Add(ConsumeName());
            Advance();
        }
        children.Add(ParseName());
        if (Take(ColonToken))
        {
            do
            {
                children.Add(ConsumeName());
                if (Take(EqualsToken)) { children.Add(ConsumeName()); }
            } while (Take(CommaToken));
        }
        Terminate();
        return Node(NodeKind.ImportDeclaration, start, children);
    }

}
