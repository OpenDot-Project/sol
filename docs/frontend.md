<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
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

The 0.0.1 frontend recorded imports as syntax only. Sol 0.1.0 adds compiler-owned bootstrap declarations for the documented std.core, std.stdio, and std.math subset; general module loading and standard-library coverage remain incomplete.
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

## Control flow beyond the 0.0.1 frontend

The following additions require a current source build; the published 0.0.1 component does not
contain them. They extend syntax inspection without adding semantic validation or execution.

```sol
module examples.control

func inspect(items: []int): int {
    var total = 0
    for var i: int = 0; i < items.length; i += 1 {
        if items[i] < 0 {
            continue
        } else {
            total += items[i]
        }
    }
    for val item in items {
        total += item
    }
    for total < 100 {
        total += 1
    }
    for {
        break
    }
    switch total {
        case 0:
            return 0
        default:
            return total
    }
}
```

Each loop has a `ForStatement` node. Header children distinguish an absent header, `ForCondition`,
`ForInitializer`/`ForCondition`/`ForUpdate`, and `ForEachBinding` plus collection expression.
Local and iteration bindings may use flat destructuring, such as `val (key, _)`; this does not
introduce a destructuring property declaration at module scope. Three-clause updates use assignment
statements. `break` and `continue` have dedicated nodes; legal targets require later flow analysis.

`if` is a statement requiring braces, not an expression. Its `else` must share a logical line with
the preceding `}`. Control body braces share their header's logical line. To use a trailing lambda
inside a condition, parenthesize that call so the following control body remains unambiguous.

```sol
module examples.handlers

func readValue(): int {
    defer { close() }
    val value = try {
        read()
    } catch (error: IOException) {
        fallback()
    } finally {
        release()
    }
    unsafe { nativeCall() }
    value
}

func dispatch(command: Command) {
    switch command {
        case Command.Move(x, _):
            move(x)
        case is Failure:
            report()
        default:
            return
    }
}
```

A `TryExpression` is distinct from a standalone `TryStatement`; both preserve body, individual
`CatchClause` nodes, and an optional `FinallyClause`. At least one catch or finally is required.
No exception type hierarchy, cleanup effect restriction, or return-result type is resolved yet.
Switch cases keep independent statement lists, flat payload bindings, and type-pattern nodes.
Nested payload patterns are not implemented. Exhaustiveness and duplicate/unreachable cases require FIR.
The parser can recover a missing try-body brace at `catch` without discarding that handler.

```sol
module examples.callbacks

val increment = { x: int -> x + 1 }
val write = done@ { ref x: int, out y: int ->
    y = x
    return@done
}
val anonymous = func(x: int): int { return x + 1 }
val result = combine(20, 22) { x, y -> x + y }

func parse(text: String) {
    tryRead(text = text, result = out var value)
    swap(left = ref first, right = ref second)
    consume(*items)
    val cast = value as? Item
    val matches = value !is Item
    val required = cast ?: throw Failure("missing")
}
```

Lambda headers have `LambdaParameters`/`LambdaParameter` children with source-preserved modes/types;
labels have `LabelledExpression` nodes. Each call argument has an `Argument` node containing its
optional name and value, with dedicated `RefArgument`, `OutArgument`, `OutVariable`, or `SpreadArgument`
structure where applicable. This preserves storage/declaration intent for later binding. Such markers
are not allowed in array or tuple literals. Semantic constraints such as valid writable storage,
label scope, ref escape, and callback type compatibility remain unimplemented.

## Declarations and members beyond the 0.0.1 frontend

Current source builds also parse class, struct/ref struct, object/companion, interface, and enum
declarations; primary/secondary constructors; init blocks; properties/accessors/indexers;
annotations; base/property delegation; generic parameters, applications, constraints, and effects.
These capabilities are not in the published 0.0.1 component.

```sol
module examples.models

annotation class Label(val text: String)

interface Named {
    func name(): String
}

open class Entity(val id: ulong)

@Label("user")
partial class User(id: ulong, var caption: String): Entity(id), Named {
    override func name(): String = this.caption

    val displayName: String {
        get = this.caption
    }

    constructor(): this(0, "anonymous") {}

    companion object {
        func create(name: String): User = User(0, name)
    }
}

partial class User {
    func description(): String = this.displayName
}

enum Result<T> {
    Empty,
    Value(value: T),
}

func <T> identity(value: T): T = value
val nested: Result<Result<int>> = identity<Result<Result<int>>>(Result.Value(Result.Value(42)))
```

Each nominal type has a `TypeBody` child containing its written members. Generic type parameters,
type argument lists, where constraints, throws clauses, and constructor delegation have dedicated
nodes. `>>`, `>=`, and `>>=` are consumed contextually when closing generic types without changing
the original physical token stream or the meaning of shift expressions elsewhere.

Array prefixes bind to the complete element type: `[]String?` and `[](String?)` both spell
a non-null array of nullable strings. `([]String)?` spells a nullable array of non-null strings;
`([]String?)?` makes both nullable. The same grouping rule applies to nested and fixed-size arrays.
Repeated nullable suffixes are preserved in CST spelling for later type normalization.

Annotations retain an optional target and argument list. File-target annotations may precede the
required module declaration. Annotation target validity and constant evaluation still require binding.

```sol
module examples.properties

readonly struct Point {
    val x: int
    val y: int
    constructor(x, y: int) {
        this.x = x
        this.y = y
    }
    companion object {
        operator func +(left, right: Point): Point = Point(left.x + right.x, left.y + right.y)
    }
}

ref struct Window<T> where T: allows ref struct {
    private var item: T
    ref readonly this[index: int]: T {
        get { return ref this.item }
    }
}

class Counter {
    var count: int { get; private set; } = 0
    val doubled: int { get = this.count * 2 }
}
```

Short getters contain expressions; short setters/init accessors can contain an assignment statement.
A short getter assignment is rejected. An accessor list remains distinct from a binding's initializer
lambda. Named operators, token operators, and conversion operator names retain their source spelling;
legal owner/arity/paired-operator rules still require semantic verification.

```sol
module examples.delegation

interface Logger {
    func log(text: String)
}

class WrappedLogger(private val target: Logger): Logger by target {}
class Settings {
    var name: String by NameDelegate("Sol")
}

extern "c" {
    func close(handle: ulong): int;
}
```

Delegation expressions are recorded as `Delegation` nodes for later protocol/initialization analysis.
The parser rejects a property combining a custom accessor list with `by`. Extern blocks accept the
quoted `c` ABI name and annotated function signatures without bodies; native signature layout and
linking are not yet validated or performed. The examples establish syntax, not valid resolved APIs.

The [PSI interface](psi.md) projects these concrete structures into typed immutable source views.
It does not merge partial types, resolve imports, infer types, validate checked effects or ref escape,
or produce executable code. Those later stages remain separate implementation work.
