<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# PSI: typed source views

The development branch includes `OpenDot.Sol.Psi`, a read-only program structure interface over
the lossless CST. It exposes source declarations for tooling without loading LLVM or a C++/CLI
bridge. This project was not included in the initial Sol 0.0.1 component. The 0.1.0 compiler component carries the managed assemblies, but no stable NuGet SDK
distribution is promised yet. Reference the project from a source checkout to use it.

PSI views retain the existing `SyntaxNode` and `SourceText`; they do not copy the compiler into a
second parser. They do not resolve names or types. In particular, a `PsiTypeReference` is a type's
written syntax, not a resolved type identity or a nullability proof.

## Create and inspect a snapshot

```csharp
using System;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;

var source = SourceText.FromString(
    "module app.model\npartial class Box<T> { var value: T\nfunc get(): T = this.value }",
    "inputs/example.sol");
var tree = SyntaxTree.Parse(source);
if (tree.HasErrors) { throw new ArgumentException("The sample must parse successfully."); }

var snapshot = PsiSnapshot.Create(tree);
var box = snapshot.Root.Declarations.OfType<PsiTypeDeclaration>().Single();
var method = box.Members.OfType<PsiFunction>().Single();

Console.WriteLine(snapshot.Root.ModuleName); // app.model
Console.WriteLine(box.Name);                // Box
Console.WriteLine(box.Arity);               // 1
Console.WriteLine(method.ReturnType!.Text); // T

var name = snapshot.FindElement(method.NameElement!.Span.Start);
if (!ReferenceEquals(name, method.NameElement)) { throw new InvalidOperationException("Wrapper identity changed."); }
```

`Root` is a `PsiFile`. Its declarations are direct source declarations, not a global symbol table.
The class's `Members` come from its own body; inherited or generated members do not appear there.
`IsPartial` reports a written modifier, without merging parts or establishing compatibility.

## Typed views

| View | Source information |
|---|---|
| `PsiFile` | Logical module name, direct declarations, imports, and file annotations. |
| `PsiTypeDeclaration` | Name, written modifiers/annotations, type parameters/arity, bases, primary constructor, members, ref-struct kind. |
| `PsiFunction` | Name/operator spelling, parameter groups, return syntax, throws syntax, and body. |
| `PsiConstructor` | Primary/secondary kind, parameter groups, throws syntax, explicit delegation, and body. |
| `PsiProperty` | Binding/property name, mutability, written type, initializer, delegation expression, and accessors. |
| `PsiIndexer` | Parameter groups, value type, and accessors. |
| `PsiAccessor` | Written get/set/init name, modifiers, throws syntax, and body. |
| `PsiEnumVariant` | Variant name and payload parameter groups. |
| `PsiInitializer`, `PsiExtern` | Written type init blocks and extern function signatures. |
| `PsiParameterGroup` | Original grouped names, shared type syntax, passing mode, constructor-property marker, and default expression. |
| `PsiTypeReference`, `PsiTypeParameter` | Written type arguments and generic parameter constraints. |
| `PsiAnnotation`, `PsiImport` | Annotation target/name/arguments and imported module/alias. |
| `PsiLambda` | Written named parameter nodes and body statements. |

Every view is also a `PsiElement`, exposing `Snapshot`, `Syntax`, `Kind`, `Span`, `Text`, `Parent`,
`Children`, `Tokens`, `Ancestors()`, and `Descendants()`. Syntax kinds without a specialized public
view remain navigable as ordinary elements. Binding statements in local scopes use `PsiProperty`
as a source binding view; this does not turn locals into runtime class properties.

Grouped parameters remain grouped: `ref a, b: int` has two `PsiName` children in one
`PsiParameterGroup`, whose `PassingMode` is `Ref`. Expansion into separate parameter symbols
belongs to the later AST/binding stages. An absent return annotation yields a null `ReturnType`
view; PSI does not infer or synthesize one.

## Identity, positions, and tokens

`snapshot.GetElement(node)` returns the same wrapper object for a concrete node owned by that
snapshot. A structurally equal node from a separately parsed source is rejected with
`ArgumentException`. Existing views remain valid for their original immutable source after a new
snapshot is created; do not attach their offsets or nodes to another snapshot.

`Span` uses zero-based, half-open UTF-8 byte offsets. `FindElement(offset)` finds the deepest
structural node covering an offset; whitespace without a child node maps to its containing ancestor.
EOF maps to the file. Negative or beyond-EOF offsets are rejected. Use `SourceText.GetPosition`
for line/scalar/UTF-16 conversion when integrating with an editor.

The parser can consume a `>>`, `>=`, or `>>=` token in pieces while closing generic types. The
physical token stream is unchanged. Therefore `Tokens` returns physical lexer tokens intersecting
a view's range, and a combined closer may extend beyond that range. Use `Text` for the exact spelling
of the view and the original token/trivia stream for full-source reconstruction. Token range lookup
uses a binary search into the immutable stream.

## Several files and modules

`PsiWorkspace.Create(trees)` accepts an explicit sequence of `SyntaxTree` objects. `Files` preserves
their order; `Modules` groups file roots by ordinal, case-sensitive logical module name. A source
file's filesystem path does not choose its module or partial type identity. No directory scanning
or import loading occurs.

Two `partial class C` declarations in files naming the same module remain two source declarations.
PSI records them for subsequent analysis; it does not merge members, validate initialization order,
choose a base class, or resolve duplicate signatures. Source input order is not an initialization policy.

## Errors, cancellation, and concurrency

Recovery trees with diagnostics can be inspected. Missing syntax may produce empty name/type ranges,
so tooling must check diagnostics instead of assuming a complete declaration. Malformed original
bytes remain in `SourceText.Bytes`; decoded `Text` is a display view and can contain replacement characters.

`PsiSnapshot.Create` and `PsiWorkspace.Create` accept cancellation tokens. Snapshot construction
and traversal use explicit stacks, including for externally constructed deep trees. Construction
rejects out-of-parent spans, a root that does not cover its source, and nodes shared by multiple
parents. Once constructed, public views are immutable and support concurrent readers.

There is no mutable semantic session, symbol resolver, edit transaction, or stale-session recovery
API in this project yet. Create new source/CST/PSI snapshots for changed input. AST normalization,
symbol/type/effect analysis, and verified FIR remain separate implementation work.
