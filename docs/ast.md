<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Structural AST

`OpenDot.Sol.Ast` converts a PSI snapshot into an immutable structural AST.
It preserves source origins while making parameter groups, type shapes, function bodies,
call arguments, and control-flow structure available through dedicated APIs.
The library runs without LLVM or a native bridge.

This is a structural normalization boundary. Expressions do not have resolved types,
calls do not have selected overloads, and variables do not have proven storage lifetimes.
The AST is not executable FIR.

## Create and inspect a snapshot

```csharp
using System;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;

var source = SourceText.FromString("module app\nfunc add(a, b: int) = a + b");
var syntax = SyntaxTree.Parse(source);
var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax));
AstVerifier.Verify(ast);
foreach (var declaration in ast.Root.Declarations)
{
    Console.WriteLine(declaration.Name);
}
```

`FrontendCompilation.CreateAst()` applies the same conversion to the explicit source set.
No directory discovery or path-to-module conversion occurs.
The caller can supply a cancellation token to construction, verification, and inspection.

## Source identity

Every node has an `AstOrigin`, which refers to its original PSI element and UTF-8 byte span.
The origin retains the original immutable source snapshot, including malformed bytes.
Original comments, whitespace, token spelling, and generic closing tokens remain in CST/PSI.
The AST does not rewrite them.

An AST node ID is local to a snapshot. It is not a cross-edit, serialized ABI, or cache identity.
`GetNode(psiElement)` returns the unique AST node for an owned PSI element.
`ValidateOwnership` and `GetParent` reject nodes from another AST snapshot,
even if their source text and byte ranges are equal.
`FindNode(byteOffset)` follows PSI range navigation; EOF resolves to the file.

Construction and traversal use explicit stacks. Read-only navigation can run concurrently
after the snapshot has been fully constructed.

## Parameters and function bodies

```sol
module examples.parameters

func add(a, b: int): int {
    a + b
}

func update(ref count: int, out text: String) {
    count += 1
    text = "updated"
}
```

The first function has two `AstParameter` entries. Each has its own name origin,
while both retain the same group origin and shared `AstType`.
Default expressions remain AST nodes; normalization never evaluates them.
Passing modes are `Value`, `Ref`, `Out`, and `Vararg`.
Primary constructor parameters additionally expose property and mutability flags.

`AstBody.Kind` distinguishes absent signatures, expression bodies, and block bodies.
`TailExpression` exposes a syntactic candidate, not a proof of return coverage or inferred type.
A final assignment, declaration, loop, or explicit return is not changed into an expression.
Source semicolons do not suppress a block's tail candidate.

Accessors retain their own bodies and effects. A short getter is an accessor expression,
not a stored-property initializer. Delegated properties retain their delegate expression
separately from both the initializer and custom accessors.

## Normalized type spelling

`AstType.Shape` is an unresolved `TypeShape` with a canonical inspection spelling.
It preserves nominal names and generic arguments without deciding what those names denote.
Grouping around a single type is removed; tuple grouping and function passing modes remain.
Repeated nullable suffixes normalize to one nullable layer.

| Source | Meaning retained by the shape |
|---|---|
| `[]String?` | Non-null array of nullable elements |
| `[](String?)` | Same element nullability as the preceding form |
| `([]String)?` | Nullable array of non-null elements |
| `([]String?)?` | Nullable array of nullable elements |
| `[][]String?` | Two non-null array layers and nullable leaf elements |
| `[4]String?` | Fixed-size array with nullable elements |
| `(int,)` | Singleton tuple, distinct from grouped `int` |
| `(ref int, out String)->()` | Function type with explicit passing modes |
| `!` | Bottom type spelling |

Canonical names are for structural comparison and inspection.
They do not collapse imported aliases, qualify unqualified nominal names,
prove generic constraints, or establish layout/defaultability.
Those operations require a resolved type system.

## Expressions and statements

`AstOperation` retains operator spelling, prefix/postfix position, operands,
and type-test/cast target syntax. Logical and Elvis operators are marked as short-circuiting;
no transformation evaluates both sides during AST construction.

`AstCall.Arguments` keeps source evaluation order, local argument names,
ref/out/spread modes, out-variable declarations, and trailing lambdas.
`AstMember` records safe navigation separately from ordinary member access.
`AstSequence` distinguishes grouping, tuples, and array literals.

`AstLambda` distinguishes named headers, positional references, and headerless forms.
Its positional index set excludes nested lambdas and anonymous functions.
An index too large for a supported function arity is retained as an unrepresentable-position flag;
it never changes a positional lambda into a headerless one.
Minimum positional arity is a syntactic lower bound; expected function types still need binding.
No implicit parameter is invented. Labelled lambdas and labelled returns retain their labels.

Control flow has dedicated views for if, the four for-loop forms, switch cases,
catch clauses, and try statement/result forms. Finally and defer bodies remain explicit.
The AST does not yet contain a control-flow graph, exhaustiveness proof, cleanup plan,
exception subtype classification, or definite-assignment results.

## Inspection and verification

`solc input.sol --emit=ast -o input.ast.json` emits the normalized source model.
The output is produced only after syntax succeeds for the entire explicit input set.
Output collision, source-overwrite, changed-source, and transactional-write protections
apply to AST output as they do to CST output.

The JSON document uses schema version 1 and a flat node array.
Children refer to snapshot-local IDs, avoiding deeply nested inspection JSON.
It contains source byte ranges and normalized fields, without embedding source paths.
`AstJson.Write` leaves its destination stream open.
This is an inspection schema; there is no executable AST deserializer or stable binary contract.

`AstVerifier` checks snapshot ownership, unique IDs, parent mapping, source ranges,
parameter-origin ownership, and reachability from the root.
Verification does not certify a program as semantically valid.
An AST created from a recovery tree remains inspectable through the library and reports
`HasSyntaxErrors`; the CLI refuses to publish output for that input.

Declaration lookup and partial composition are described in [Declaration analysis](analysis.md).
