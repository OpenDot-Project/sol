// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Psi;

public abstract class PsiDeclaration : PsiElement
{
    internal PsiDeclaration(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public PsiName? NameElement => Children.OfType<PsiName>().FirstOrDefault();
    public virtual string? Name => NameElement?.Text;
    public IEnumerable<string> Modifiers => Children.Where(c => c.Kind == NodeKind.Modifier).Select(c => c.Text);
    public IEnumerable<PsiAnnotation> Annotations => Children.OfType<PsiAnnotation>();
    public bool IsPartial => Modifiers.Contains("partial");
}

public sealed class PsiTypeDeclaration : PsiDeclaration
{
    internal PsiTypeDeclaration(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public IEnumerable<PsiDeclaration> Members => Child(NodeKind.TypeBody)?.Children.OfType<PsiDeclaration>() ?? [];
    public IEnumerable<PsiTypeParameter> TypeParameters => Child(NodeKind.TypeParameterList)?.Children.OfType<PsiTypeParameter>() ?? [];
    public int Arity => TypeParameters.Count();
    public IEnumerable<PsiElement> BaseTypes => Child(NodeKind.BaseList)?.Children ?? [];
    public PsiConstructor? PrimaryConstructor => Children.OfType<PsiConstructor>().FirstOrDefault(c => c.Kind == NodeKind.PrimaryConstructor);
    public bool IsRefLike => Kind == NodeKind.RefStructDeclaration;
    public IEnumerable<PsiInitializer> Initializers => Members.OfType<PsiInitializer>();
}

public abstract class PsiCallableDeclaration : PsiDeclaration
{
    internal PsiCallableDeclaration(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public IEnumerable<PsiParameterGroup> ParameterGroups => Child(NodeKind.ParameterList)?.Children.OfType<PsiParameterGroup>() ?? [];
    public IEnumerable<PsiTypeReference> ThrownTypes => Child(NodeKind.ThrowsClause)?.Children.OfType<PsiTypeReference>() ?? [];
}

public sealed class PsiFunction : PsiCallableDeclaration
{
    internal PsiFunction(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public bool IsCheckedOperator => NameElement?.Text.StartsWith("checked ", System.StringComparison.Ordinal) == true;
    public string? OperatorName => IsCheckedOperator ? NameElement?.Text["checked ".Length..] : NameElement?.Text;
    public override string? Name => OperatorName;
    public IEnumerable<PsiTypeParameter> TypeParameters => Child(NodeKind.TypeParameterList)?.Children.OfType<PsiTypeParameter>() ?? [];
    public PsiTypeReference? ReturnType => Child(NodeKind.ReturnType)?.Children.OfType<PsiTypeReference>().FirstOrDefault();
    public PsiElement? Body => Children.LastOrDefault(c => c.Kind is not (NodeKind.Annotation or NodeKind.Modifier
        or NodeKind.Name or NodeKind.OperatorName or NodeKind.ParameterList or NodeKind.TypeParameterList
        or NodeKind.ReturnType or NodeKind.WhereClause or NodeKind.ThrowsClause));
}

public sealed class PsiConstructor : PsiCallableDeclaration
{
    internal PsiConstructor(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public PsiElement? Body => Child(NodeKind.Block);
    public PsiElement? Delegation => Child(NodeKind.ConstructorDelegation);
}

public sealed class PsiEnumVariant : PsiCallableDeclaration
{
    internal PsiEnumVariant(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
}

public sealed class PsiInitializer : PsiDeclaration
{
    internal PsiInitializer(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public PsiElement? Body => Child(NodeKind.Block);
}

public sealed class PsiExtern : PsiDeclaration
{
    internal PsiExtern(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public IEnumerable<PsiFunction> Functions => Child(NodeKind.TypeBody)?.Children.OfType<PsiFunction>() ?? [];
}

public sealed class PsiProperty : PsiDeclaration
{
    internal PsiProperty(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public PsiTypeReference? ValueType => Children.OfType<PsiTypeReference>().FirstOrDefault();
    public IEnumerable<PsiAccessor> Accessors => Child(NodeKind.AccessorList)?.Children.OfType<PsiAccessor>() ?? [];
    public PsiElement? DelegateExpression => Child(NodeKind.Delegation)?.Children.FirstOrDefault();
    public PsiElement? Initializer => Children.LastOrDefault(c => c.Kind is not (NodeKind.Annotation or NodeKind.Modifier
        or NodeKind.PassingMode or NodeKind.Name or NodeKind.Type or NodeKind.AccessorList or NodeKind.Delegation));
    public bool IsMutable => Tokens.LastOrDefault(t => t.Span.Start < (NameElement?.Span.Start ?? 0)
        && t.Kind is SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)?.Kind == SyntaxKind.VarKeyword;
}

public sealed class PsiIndexer : PsiCallableDeclaration
{
    internal PsiIndexer(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public PsiTypeReference? ValueType => Children.OfType<PsiTypeReference>().FirstOrDefault();
    public IEnumerable<PsiAccessor> Accessors => Child(NodeKind.AccessorList)?.Children.OfType<PsiAccessor>() ?? [];
}

public sealed class PsiAccessor : PsiCallableDeclaration
{
    internal PsiAccessor(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public PsiElement? Body => Children.LastOrDefault(c => c.Kind is not (NodeKind.Annotation or NodeKind.Modifier or NodeKind.Name or NodeKind.ThrowsClause));
}

public enum ParameterPassingMode { Value, Ref, Out, Vararg }

public sealed class PsiParameterGroup : PsiElement
{
    internal PsiParameterGroup(PsiSnapshot snapshot, SyntaxNode syntax, PsiElement? parent) : base(snapshot, syntax, parent) { }
    public IEnumerable<PsiName> Names => Children.OfType<PsiName>();
    public PsiTypeReference? ParameterType => Children.OfType<PsiTypeReference>().FirstOrDefault();
    public ParameterPassingMode PassingMode => Child(NodeKind.PassingMode)?.Text switch
    {
        "ref" => ParameterPassingMode.Ref,
        "out" => ParameterPassingMode.Out,
        "vararg" => ParameterPassingMode.Vararg,
        _ => ParameterPassingMode.Value,
    };
    public bool DeclaresProperty => Children.Any(c => c.Kind == NodeKind.Modifier && c.Text is "val" or "var");
    public PsiElement? DefaultValue => Children.LastOrDefault(c => c.Kind is not (NodeKind.Annotation or NodeKind.Modifier
        or NodeKind.PassingMode or NodeKind.Name or NodeKind.Type));
}
