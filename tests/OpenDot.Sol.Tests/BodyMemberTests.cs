// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class BodyMemberTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable {}\n"
        + "open class Exception: Throwable {}\nopen class RuntimeException: Exception {}\n";
    private static BodyAnalysis Analyze(string source, params string[] extra)
    {
        var session = AstTestSources.Session(new[] { "module app\n" + source, Core }.Concat(extra).ToArray());
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }
    private static void Valid(BodyAnalysis result) => Assert.False(result.HasErrors,
        string.Join('\n', result.Diagnostics.Select(d => d.Code + " " + d.Message + ": " + d.Source.GetText(d.Span))));
    private static void Error(BodyAnalysis result, string code) => Assert.Contains(result.Diagnostics, d => d.Code == code);

    [Fact]
    public void ConstructorAssignsStoredValPropertiesBeforePublication()
    {
        var result = Analyze("class Item { val value: int; constructor(value: int) { this.value = value } }\nfunc f(): int = Item(42).value");
        Valid(result);
        Assert.Equal("int", result.Expressions.Single(e => e.Expression.Origin.Text == "Item(42).value").Type!.Name);
    }

    [Fact]
    public void ConstructorInitializationRequiresEveryNormalPath()
    {
        Error(Analyze("class Item { val value: int; constructor(flag: bool) { if flag { this.value = 1 } } }"), "SOL2270");
        Error(Analyze("class Item { val value: int; constructor() { val before = this.value; this.value = 1 } }"), "SOL2271");
        Error(Analyze("class Item { val value: int; constructor() { this.value = 1; this.value = 2 } }"), "SOL2241");
        Valid(Analyze("class Item { val value: int; constructor(other: Item) { this.value = other.value } }"));
        Error(Analyze("class Item { val value: int; constructor(other: Item) { other.value = 1; this.value = 2 } }"), "SOL2241");
    }

    [Fact]
    public void PrimaryConstructorPropertiesAndGenericConstructionAreTyped()
    {
        Valid(Analyze("class Box<T>(val value: T) {}\nfunc f(): int = Box<int>(1).value"));
    }

    [Fact]
    public void ConstructorCannotPublishThisOrDispatchVirtually()
    {
        Error(Analyze("func publish(value: Object) {}\nclass Item { constructor() { publish(this) } }"), "SOL2273");
        Error(Analyze("open class Item { constructor() { this.f() }; open func f() {} }"), "SOL2272");
    }

    [Fact]
    public void EmptyNominalConstructionHasANominalResult()
    {
        var result = Analyze("class Item {}\nfunc f(): Item = Item()");
        Valid(result);
        Assert.Equal("app.Item", result.Expressions.Single(e => e.Expression.Origin.Text == "Item()").Type!.Name);
    }

    [Fact]
    public void UserImplicitConversionIsSingleStepAndEffectsArePropagated()
    {
        const string definitions = "class Source { companion object { operator func implicit(value: Source): int = 1 } }\n";
        Valid(Analyze(definitions + "func f(value: Source): int = value"));
        Valid(Analyze(definitions + "func consume(value: int): int = value\nfunc f(value: Source): int = consume(value)"));
        Error(Analyze("class Source { companion object { operator func implicit(value: Source): int throws Exception { throw Exception() } } }\n"
            + "func f(value: Source): int = value"), "SOL2250");
    }

    [Fact]
    public void ArithmeticOperatorUsesCompanionDeclaration()
    {
        Valid(Analyze("struct Pair { companion object { operator func +(a, b: Pair): int = 1 } }\n"
            + "func f(a, b: Pair): int = a + b"));
    }

    [Fact]
    public void RefFunctionValuesCheckAndAssignCallerStorage()
    {
        Valid(Analyze("func f(): int { val write: (out int) -> () = { out x: int -> x = 1 }; var value: int; write(out value); value }"));
        Error(Analyze("func f() { val write: (out int) -> () = { out x: int -> }; var value: int; write(out value) }"), "SOL2242");
    }

    [Fact]
    public void AFunctionValueCanBroadenItsCheckedBudget()
    {
        Valid(Analyze("func pure(): int = 1\nfunc f() { val callback: () -> int throws Exception = pure }"));
        Error(Analyze("func impure(): int throws Exception { for {} }\nfunc f() { val callback: () -> int = impure }"), "SOL2206");
    }

    [Fact]
    public void SimpleLocalFunctionsAreCallable()
    { Valid(Analyze("func f(): int { func add(a, b: int): int = a + b; add(1, 2) }")); }

    [Fact]
    public void LiteralGenericInferenceAndInferredReceiverResultsAreSpecialized()
    {
        Valid(Analyze("func <T> identity(value: T): T = value\nfunc f(): int = identity(1)"));
        Valid(Analyze("class Box<T> { func get(value: T) = value }\nfunc f(box: Box<int>): int = box.get(1)"));
    }

    [Fact]
    public void VarargFunctionReferencesUseAnArrayParameter()
    {
        Valid(Analyze("func count(vararg values: int): int = values.length\nfunc f(): int { val callback = count; callback([1, 2]) }"));
    }

    [Fact]
    public void GenericAntiConstraintDoesNotPermitHeapCaptureOrStorage()
    {
        Error(Analyze("func <T> f(value: T) where T: allows ref struct { val callback = { value } }"), "SOL2262");
        Error(Analyze("class Holder<T> where T: allows ref struct { var value: T }"), "SOL2260");
        Error(Analyze("func <T> f(values: []T) where T: allows ref struct {}"), "SOL2260");
    }

    [Fact]
    public void PropertyDelegationChecksOperatorProtocolAndGetterEffects()
    {
        const string descriptor = "module std.reflect\nclass SolProperty<T> {}";
        const string declarations = "import std.reflect\nclass Delegate { operator func getValue(owner: Object?, property: SolProperty<int>): int throws Exception { for {} } }\n"
            + "class Item { val value: int by Delegate() }\n";
        Valid(Analyze(declarations + "func f(item: Item): int throws Exception = item.value", descriptor));
        Error(Analyze(declarations + "func f(item: Item): int = item.value", descriptor), "SOL2250");
        Error(Analyze("import std.reflect\nclass Delegate { func getValue(owner: Object?, property: SolProperty<int>): int = 1 }\n"
            + "class Item { val value: int by Delegate() }", descriptor), "SOL2280");
    }
}
