// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ConstructionFlowProofTests
{
    [Theory]
    [InlineData("open class B { constructor() {} constructor(value: int = 1) {} }\nclass C: B {}")]
    [InlineData("open class B(value: int = 1) {}\nclass C(): B() {}")]
    [InlineData("open class B<T>(val value: T) {}\nclass C<T>(value: T): B<T>(value) {}\nfunc use(): C<int> = C<int>(1)")]
    [InlineData("open class B<T>(value: T) { val item: T = value }\nclass C(value: int): B<int>(value) { val copy: int = this.item }")]
    [InlineData("abstract class B { abstract val item: int }")]
    [InlineData("class C { val item: int { get = field } = 1 }")]
    [InlineData("class C<T> { var item: T; constructor(item: T) { this.initialize(item) }; private func initialize(item: T) { this.item = item } }")]
    [InlineData("class C<T> { val item: T; constructor(item: T) { this.item = this.copy<T>(item) }; private func <U> copy(value: U): U = value }")]
    [InlineData("open class B() {}\nclass C(val item: int): B() { init { val copy = this.item } }")]
    [InlineData("class C { var item: int; constructor() { defer { this.item = 1 } } }")]
    [InlineData("class C { var item: int; constructor() { try { this.item = 1 } finally { this.item = 2 } } }")]
    [InlineData("class C { var item: int; constructor(flag: bool) { if flag { this.item = 1; return }; this.item = 2 } }")]
    [InlineData("class C { val item: int; constructor() { this.item = 1 }; constructor(value: int): this() {} }")]
    [InlineData("class C { constructor(first: int, second: bool) {} constructor(): this(second = true, first = 1) {} }")]
    [InlineData("class C { constructor(out result: int) { result = 1 }; constructor(out result: int, marker: bool): this(out result) {} }")]
    [InlineData("class C { constructor(ref result: int) { result = 1 }; constructor(ref result: int, marker: bool): this(ref result) {} }")]
    [InlineData("class C { constructor(vararg items: int) {} constructor(flag: bool): this(1, 2) {} }")]
    [InlineData("interface I { func read(): int }\nclass C(delegate: I): I by delegate {}")]
    [InlineData("open class B { protected constructor() {} }\nclass C: B {}")]
    [InlineData("class C { private constructor() {}; companion object { func create(): C = C() } }")]
    public void ChainsAndStructuredFlowPreserveRequiredStorage(string source)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.DoesNotContain(analysis.ConstructionProofs.Values, proof => proof.Status != BodyBindingStatus.Bound);
    }

    [Theory]
    [InlineData("open class B(value: int = 1) {}\nclass C: B {}", "SOL2274")]
    [InlineData("class C(val item: int): B() {}\nopen class B { constructor() { this.observe() }; open func observe() {} }", "SOL2272")]
    [InlineData("class C { var item: int; constructor() { this.initialize() }; private func initialize() { val copy = this.item; this.item = 1 } }", "SOL2271")]
    [InlineData("open class B(value: int) {}\nclass C(val item: int): B(this.item) {}", "SOL2271")]
    [InlineData("class C { val item: int; constructor() { this.initialize() }; private func initialize() { this.item = 1 } }", "SOL2241")]
    [InlineData("class C { val item: int { get = field } }", "SOL2270")]
    [InlineData("class C { protected constructor() {} }\nfunc use(): C = C()", "SOL2214")]
    [InlineData("class C { constructor() throws int {} }", "SOL2251")]
    [InlineData("class C { constructor(@escaping item: int) {} }", "SOL2263")]
    [InlineData("class E: Exception {}\nopen class B { constructor() throws E { throw E() } }\nclass C(): B {}", "SOL2250")]
    public void InvalidDependenciesCannotProduceSuccessfulProofs(string source, string code)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Contains(analysis.ConstructionProofs.Values, proof => proof.Status == BodyBindingStatus.Invalid);
    }

    [Fact]
    public void MultipleInitializationPartsKeepTheDeclarationDiagnosticAndInvalidateConstruction()
    {
        var session = AstTestSources.Session("module app\npartial class C { constructor() {} }\n"
            + "partial class C { val item: int = 1 }", GenericMemberProofTestSource.Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.Contains(sources.Diagnostics, diagnostic => diagnostic.Code == "SOL2030");
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2276");
        Assert.Contains(analysis.ConstructionProofs.Values, proof => proof.Status == BodyBindingStatus.Invalid);
    }

    [Fact]
    public void FailedArgumentSkipsLaterArgumentsAndConstructorEffects()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class E1: Exception {}\nclass E2: Exception {}\n"
            + "func first(): ! throws E1 { throw E1() }\nfunc later(): int throws E2 { throw E2() }\n"
            + "class C(left: int, right: int) {}\nfunc use(): C throws E1 = C(first(), later())");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.DoesNotContain(analysis.Bodies.Single(body => body.Declaration.Name == "use").EscapingEffects,
            effect => effect.Declaration?.Name == "E2");
        Assert.DoesNotContain(analysis.ConstructionProofs.Values, proof => proof.Selection.Site is AstCall { Origin.Text: "C(first(), later())" });
    }

    [Fact]
    public void AFailedInterfaceTargetSkipsLaterHeaderTargets()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class E1: Exception {}\nclass E2: Exception {}\n"
            + "interface I1 {}\ninterface I2 {}\nclass D: I2 {}\n"
            + "func first(): ! throws E1 { throw E1() }\nfunc later(): I2 throws E2 = D()\n"
            + "class C: I1 by first(), I2 by later() {}\nfunc use(): C throws E1 = C()");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.DoesNotContain(analysis.Bodies.Single(body => body.Declaration.Name == "use").EscapingEffects,
            effect => effect.Declaration?.Name == "E2");
    }

    [Fact]
    public void AFailedCallbackArgumentSkipsLaterArgumentsAndInvocationEffects()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class E1: Exception {}\nclass E2: Exception {}\nclass E3: Exception {}\n"
            + "class C {}\nfunc first(): ! throws E1 { throw E1() }\nfunc later(): int throws E2 = 1\n"
            + "func use(callback: (int, int)->C throws E3): C throws E1 = callback(first(), later())");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.DoesNotContain(analysis.Bodies.Single(body => body.Declaration.Name == "use").EscapingEffects,
            effect => effect.Declaration?.Name is "E2" or "E3");
    }

    [Fact]
    public void PhaseOriginsDoNotMixInitializerAndBodyEffects()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class E: Exception {}\nfunc read(): int throws E = 1\n"
            + "class C { val item: int = read(); constructor() throws E {} }\nfunc use(): C throws E = C()");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var proof = analysis.ConstructionProofs.Values.Single(proof => proof.Selection.Site is AstCall { Origin.Text: "C()" });
        Assert.Contains(proof.Origins, origin => origin.Phase == "field initializer" && origin.Effects.Any(effect => effect.Declaration?.Name == "E"));
        Assert.DoesNotContain(proof.Origins.Single(origin => origin.Phase == "constructor body").Effects,
            effect => effect.Declaration?.Name == "E");
    }

    [Theory]
    [InlineData("C()", true)]
    [InlineData("C(1)", false)]
    public void OnlySelectedMissingDefaultsHaveDefaultOrigins(string call, bool usesDefault)
    {
        var analysis = GenericMemberProofTestSource.Analyze("class E: Exception {}\nfunc read(): int throws E = 1\n"
            + "class C { constructor(item: int = read()) throws E {} }\nfunc use(): C throws E = " + call);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var proof = analysis.ConstructionProofs.Values.Single(proof => proof.Selection.Site is AstCall site && site.Origin.Text == call);
        Assert.Equal(usesDefault, proof.Origins.Any(origin => origin.Phase == "default argument"));
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
    }
}
