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
    public void ComparisonOperatorsRequireMatchingPairsAndDispatchBeforeBuiltinEquality()
    {
        const string paired = "class Key { companion object { operator func ==(left: Key, right: Key): bool throws Exception = true\n"
            + "operator func !=(left: Key, right: Key): bool = false } }\n";
        Error(Analyze(paired + "func equal(left, right: Key): bool = left == right"), "SOL2250");
        Error(Analyze("class Key { companion object { operator func ==(left: Key, right: Key): bool = true } }"), "SOL2207");
    }

    [Fact]
    public void OperatorContractsRejectForeignOperandsAndInvalidResults()
    {
        var foreignOperand = Analyze("class Key { companion object { operator func +(left, right: int): int = left + right } }");
        Error(foreignOperand, "SOL2207");

        var invalidComparison = Analyze("class Key { companion object { operator func ==(left: Key, right: Key): int = 1\n"
            + "operator func !=(left: Key, right: Key): int = 0 } }");
        Error(invalidComparison, "SOL2207");
    }

    [Fact]
    public void CheckedOperatorVariantHasDistinctDeclarationIdentity()
    {
        Valid(Analyze("struct Number { companion object { operator func +(left, right: Number): Number = left\n"
            + "operator func checked +(left, right: Number): Number = right } }"));
    }

    [Fact]
    public void CheckedOperatorVariantIsPreferredAndCompoundAssignmentUsesOperator()
    {
        Error(Analyze("struct Number { companion object { operator func +(left, right: Number): Number = left\n"
            + "operator func checked +(left, right: Number): Number throws Exception = right } }\n"
            + "func add(left, right: Number): Number = left + right"), "SOL2250");
        Valid(Analyze("struct Counter { companion object { operator func +(left: Counter, right: int): Counter = left } }\n"
            + "func add(initial: Counter): Counter { var value: Counter = initial; value += 1; value }"));
    }

    [Fact]
    public void UserOperatorOverloadsUseConversionRanks()
    {
        Error(Analyze("struct Number { companion object { operator func +(left: Number, right: int): Number throws Exception = left\n"
            + "operator func +(left: Number, right: Object): Number = left } }\n"
            + "func add(value: Number): Number = value + 1"), "SOL2250");
    }

    [Fact]
    public void ShiftOperatorRequiresIntRightOperand()
    {
        Error(Analyze("struct Bits { companion object { operator func <<(left: Bits, right: short): Bits = left } }"), "SOL2207");
    }

    [Fact]
    public void ConditionalLogicalOperatorsRequireAndBindTruthAndBitwiseContracts()
    {
        const string contracts = "struct Flag { companion object { operator func true(value: Flag): bool = true\n"
            + "operator func false(value: Flag): bool = false\n"
            + "operator func &(left, right: Flag): Flag = left\n"
            + "operator func |(left, right: Flag): Flag = right } }\n";
        Valid(Analyze(contracts + "func both(left, right: Flag): Flag = left && right\n"
            + "func either(left, right: Flag): Flag = left || right"));

        Error(Analyze(contracts.Replace("operator func &(left, right: Flag): Flag = left",
            "operator func &(left, right: Flag): bool = true")
            + "func both(left, right: Flag): Flag = left && right"), "SOL2207");
    }

    [Fact]
    public void NullableValueOperatorsLiftUserAndBuiltinOperations()
    {
        const string source = "struct Scalar { companion object { operator func +(left, right: Scalar): Scalar throws Exception = left\n"
            + "operator func ==(left, right: Scalar): bool throws Exception = true\n"
            + "operator func !=(left, right: Scalar): bool = false } }\n"
            + "func add(left, right: Scalar?): Scalar? throws Exception = left + right\n"
            + "func equal(left, right: Scalar?): bool throws Exception = left == right\n"
            + "func isNull(value: Scalar?): bool = value == null\n"
            + "func numeric(left, right: int?): int? = left + right\n"
            + "func negate(value: int?): int? = -value\n"
            + "func ordered(left, right: int?): bool = left < right\n"
            + "func nullableNot(value: bool?): bool? = !value\n"
            + "func nullableAnd(left, right: bool?): bool? = left & right\n"
            + "func increment(initial: int?): int? { var value: int? = initial; value++; value }";
        Valid(Analyze(source));

        Error(Analyze(source.Replace("func add(left, right: Scalar?): Scalar? throws Exception",
            "func add(left, right: Scalar?): Scalar?")), "SOL2250");
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
