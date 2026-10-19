# Frontend guide

The frontend libraries inspect source text without loading LLVM or a C++/CLI bridge.
Sol 0.0.1 exposes immutable source snapshots, tokens, syntax trees, diagnostics, module grouping,
and CST JSON serialization. These APIs are early and may change as the full grammar is implemented.

## Source and coordinates

`OpenDot.Sol.Text.SourceText.FromBytes` copies its input, preserving the original bytes even when
they are malformed UTF-8. Lexing reports invalid input; source construction does not silently rewrite it.
`FromString` encodes strictly and rejects unpaired UTF-16 surrogates. `Bytes` and `Slice` expose
read-only views. `GetText` decodes a span for display; malformed bytes may appear as replacement
characters there, so use the byte APIs for exact input identity.

`TextSpan` is a zero-based, half-open byte range: `Start` is included and `End` is excluded.
`SourceText.GetPosition` returns zero-based line, scalar column, and UTF-16 column. It recognizes
LF, CRLF, and CR line endings, and rejects offsets inside a valid UTF-8 scalar. A leading UTF-8 BOM
is retained in the source while visible positions after it start at column zero. CLI human positions
are converted to one-based line/scalar coordinates.

## Parse in a C# application

Reference the frontend projects from a source checkout; published stable SDK packages are not yet
part of this release. The following code is for a .NET 10 C# application:

```csharp
using System;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;

var source = SourceText.FromString(
    "module arbitrary.name\nfunc add(a, b: int) = a + b\n",
    "inputs/example.sol");
var tree = SyntaxTree.Parse(source);

Console.WriteLine(tree.ModuleName); // arbitrary.name
Console.WriteLine(tree.Root.Kind);  // CompilationUnit
foreach (var diagnostic in tree.Diagnostics)
{
    Console.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
}
```

Check `HasErrors` or inspect `Diagnostics` before consuming a tree as successfully parsed input.
Recovery trees remain useful for inspecting invalid files, but recovery is not semantic validation.
The default diagnostic limit is 100. `SyntaxTree.Parse` defaults to a maximum depth of 128 and
clamps a requested depth to 256. The budget also covers structural depth from long expression chains.
Cancellation tokens are supported by lexer, parser, and multi-source driver operations.

For several sources:

```csharp
using OpenDot.Sol.Driver;

var compilation = FrontendCompilation.Parse(new[] { source });
var parts = compilation.Modules["arbitrary.name"];
byte[] json = CstWriter.Serialize(parts[0]);
```

Module grouping is ordinal and based on explicit declarations. Multiple sources may name the same
module. The driver does not discover files, resolve imports, merge type symbols, or perform partial-type
binding. Callers provide the complete source set themselves.

## Tokens, trivia, and JSON

Tokens preserve their source span and leading trivia. The end-of-file token owns any trailing
comments/whitespace. Reading each token's leading trivia and token bytes in order reconstructs the
original bytes, including erroneous input. Syntax nodes expose kind, span, and children; the flat token
stream retains concrete spelling and punctuation alongside the structured node tree.

`CstWriter.Serialize` produces UTF-8 JSON with `schema_version: 1`, `source_path`, `module`,
`root`, and `tokens`. Token/trivia records include `kind`, `span`, and `text`; nodes include `kind`,
`span`, and `children`. Span fields are `start`, `length`, and `end`. Property names use snake_case;
enum values retain their declared spelling, such as `CompilationUnit`. There are no resolved type,
symbol, or effect fields. JSON text is a decoded representation; for malformed source fidelity,
retain the original `SourceText.Bytes` rather than reconstructing invalid UTF-8 from JSON strings.

## Supported syntax in 0.0.1

This section is an implementation guide, not the full Sol language specification. A production
appearing here means it has parser support; name lookup, type inference, overload resolution,
checked exceptions, and execution are not yet implemented.

Each file begins with a logical module name, independent of its path:

```sol
module examples.frontend

import std.stdio
import output = std.stdio
```

Imports are recorded as syntax. No standard library is loaded or linked in this release.
Selective imports and import aliases are parsed. Object declarations can contain supported member
declarations; object base lists and most other type declarations still report unsupported syntax.

Functions use `func`. Grouped parameter spelling is preserved:

```sol
module examples.functions

func add(a, b: int) = a + b

func adjust(ref value: int, out previous: int): () {
    previous = value
    value = value + 1
}

func fail(): () throws Exception {
    throw Exception("example")
}
```

The parser recognizes expression bodies, blocks, return annotations, parameter groups, `ref`, `out`,
`vararg`, defaults, and a `throws` list. It does not yet enforce ref/out storage rules, checked effects,
or required return types. A declaration's opening body brace must remain on the same logical line
as its header. Newlines separate statements where permitted; delimiters and continued expressions
affect that decision.

Bindings and expressions include:

```sol
module examples.expressions

val values = [1, 2, 3]
var count: int = 0
const val LIMIT = 8
val empty = ()
val named = { x, y -> x + y }
val positional = { $0 + $1 }

func calculate(value: int): int {
    val result = value * 2
    result + 1
}
```

The syntax tree retains array, tuple, named/positional lambda, member/index access, call, unary,
binary, assignment, return, and throw structure. `$0`, `$1`, and subsequent positional names use
canonical decimal notation. There is no implicit lambda `it`. Tail expressions are preserved for
the future semantic stage to apply function/block result rules.

Identifiers are currently ASCII. Comments are `//` and non-nesting `/* ... */` forms. String and
rune literals must close on the same physical line; rune contents must contain one Unicode scalar.
Malformed UTF-8 is diagnosed even inside comments and quoted literals. Numeric base digits and
underscore placement are checked; numeric suffixes are not implemented.

## Important unsupported areas

The full class/struct/ref-struct model, accessors, annotations, delegation with `by`, generic
declarations, the complete statement grammar, and later semantic stages still require work.
Nested generic closers and other incomplete productions must not be assumed to work merely because
their tokens are recognized. See [status and roadmap](status.md) for implementation order.
When extending a production, add tests for both valid spelling and malformed recovery.
