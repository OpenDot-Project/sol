// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
namespace OpenDot.Sol.Cst;

public enum SyntaxKind
{
    BadToken, EndOfFileToken, IdentifierToken, IntegerLiteralToken, FloatLiteralToken,
    StringLiteralToken, RuneLiteralToken, PositionalParameterToken,
    OpenParenToken, CloseParenToken, OpenBracketToken, CloseBracketToken,
    OpenBraceToken, CloseBraceToken, CommaToken, DotToken, ColonToken, SemicolonToken, AtToken,
    PlusToken, MinusToken, StarToken, SlashToken, PercentToken, BangToken, TildeToken,
    AmpersandToken, PipeToken, CaretToken, EqualsToken, LessToken, GreaterToken, QuestionToken,
    PlusPlusToken, MinusMinusToken, PlusEqualsToken, MinusEqualsToken, StarEqualsToken,
    SlashEqualsToken, PercentEqualsToken, AmpersandEqualsToken, PipeEqualsToken, CaretEqualsToken,
    EqualsEqualsToken, BangEqualsToken, EqualsEqualsEqualsToken, BangEqualsEqualsToken,
    LessEqualsToken, GreaterEqualsToken, LessLessToken, GreaterGreaterToken,
    LessLessEqualsToken, GreaterGreaterEqualsToken, AmpersandAmpersandToken, PipePipeToken,
    ArrowToken, QuestionDotToken, QuestionColonToken, BangBangToken,
    AbstractKeyword, AnnotationKeyword, AsKeyword, BreakKeyword, CaseKeyword, CatchKeyword,
    ClassKeyword, CompanionKeyword, ConstKeyword, ConstructorKeyword, ContinueKeyword,
    DataKeyword, DefaultKeyword, DeferKeyword, ElseKeyword, EnumKeyword, FalseKeyword,
    FinalKeyword, FinallyKeyword, ForKeyword, FuncKeyword, IfKeyword, ImportKeyword,
    InKeyword, InitKeyword, InterfaceKeyword, InternalKeyword, IsKeyword, ModuleKeyword,
    NullKeyword, ObjectKeyword, OpenKeyword, OperatorKeyword, OutKeyword, OverrideKeyword,
    PartialKeyword, PrivateKeyword, ProtectedKeyword, PublicKeyword, RefKeyword, ReturnKeyword,
    SealedKeyword, StructKeyword, SuperKeyword, SwitchKeyword, ThisKeyword, ThrowKeyword,
    TrueKeyword, TryKeyword, TypeKeyword, UnsafeKeyword, ValKeyword, VarKeyword, VarargKeyword,
    WhereKeyword,
}

public enum TriviaKind
{
    ByteOrderMark, Whitespace, Newline, LineComment, BlockComment, DocumentationComment,
}
