// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative generic-call argument mapping matrix; native admission is a narrower subset.
module source.examples.semantics.generic_fir_argument_mapping

import std.stdio

// Argument mapping and generic specialization
// ===========================================
//
// This file specifies how one selected generic call maps source arguments to declaration
// parameters before an ordered type substitution becomes a FIR target. `// expect:` annotations
// are normative oracles for a conformance harness. They do not claim that every case currently
// lowers to an executable. A source mapping may be valid while its default, vararg, lambda, spread,
// or type-dependent body remains outside the verified native subset.
//
// Oracle fields use zero-based source argument indexes. `map` lists formal parameter -> source
// argument indexes; `eval` lists the once-only evaluation order; `defaults` lists omitted parameters
// filled later in declaration order; `type-arguments` follows generic parameter ordinals.
// `instance` is declaration identity plus ordered type arguments. `fir=absent` means rejection or
// deferral happens before an executable call node is certified.

// Shared generic declarations and trace helpers
// ---------------------------------------------

func <T, U> firstOf(first: T, second: U): T = first
func <T, U> secondOf(first: T, second: U): U = second
func <T> identity(value: T): T = value
func <T, U> choose(value: T, fallback: U): T = value
func <T> replace(ref value: T, replacement: T): T {
    value = replacement
    value
}
func <T> assign(out value: T, source: T) {
    value = source
}
func <T> borrowBoth(left: T, right: T): T = left
func <T> earlierDefault(value: T, fallback: T = value): T = fallback
func <T> withUnrelatedDefault(value: T, count: int = 1): T = value
func defaultedTrace(a: int = mark(1), b: int = mark(2), c: int = mark(3)): int = a + b + c
func <T> collect(vararg values: T): []T = values
func <T> gather(head: int = 0, vararg values: T, done: bool): []T = values
func <T> transform(value: T, action: (T) -> T): T = action(value)
func <T> delayed(value: T, action: () -> T): T = action()
func mark(value: int): int {
    println(value)
    value
}

// 1. Positional mapping and declaration order
// --------------------------------------------
//
// Positional arguments fill the next unoccupied formal. A mapping is fixed before generic
// inference: the solver receives the formal type paired with each actual type, not the textual
// order of the formal declarations after named arguments have been reordered.

func positionalPair(left: int, right: String): String = secondOf(left, right)

func inferredPositionOrder(number: int, text: String): String {
    // expect: positional.left-right | Specialized | call=firstOf(number,text) map=[first<-0,second<-1] eval=[0,1] type-arguments=[int,String] parameters=[int,String] result=int instance=firstOf<int,String>
    val left = firstOf(number, text)
    // expect: positional.right-left | Specialized | call=firstOf(text,number) map=[first<-0,second<-1] eval=[0,1] type-arguments=[String,int] parameters=[String,int] result=String instance=firstOf<String,int>
    val right = firstOf(text, number)
    text
}

func positionalEvidenceFromEveryActual(small: byte, wide: int): byte {
    // expect: positional.lower-bounds | Specialized | call=choose(small,wide) map=[value<-0,fallback<-1] eval=[0,1] type-arguments=[byte,int] result=byte instance=choose<byte,int>
    val result = choose(small, wide)
    result
}

func positionalExplicitArguments(number: int, text: String): int {
    // expect: positional.explicit | Specialized | call=firstOf<int,String>(number,text) map=[first<-0,second<-1] eval=[0,1] type-arguments=[int,String] result=int instance=firstOf<int,String>
    val result = firstOf<int, String>(number, text)
    result
}

func positionalMissingRequired(number: int): () {
    // expect: positional.missing | Rejected | call=firstOf(number) diagnostic=MissingRequiredParameter fir=absent
    val invalid = firstOf(number)
}

func positionalTooMany(number: int, text: String): () {
    // expect: positional.excess | Rejected | call=identity(number,text) diagnostic=NoApplicableCallable fir=absent
    val invalid = identity(number, text)
}

func positionalRepeatedFormal(number: int): () {
    // expect: positional.duplicate-fill | Rejected | call=firstOf(number,second=number) diagnostic=ArgumentAlreadyFilled fir=absent
    val invalid = firstOf(number, second = number)
}

// 2. Named mapping and legal ordered mixing
// -----------------------------------------
//
// Named values attach to formal names. Ordered mixing may continue positionally when no formal has
// been skipped or revisited. Once reordering skips or fills an earlier position, remaining ordinary
// values cannot guess which formal the caller intended.

func namedFullyReordered(number: int, text: String): String {
    // expect: named.reversed | Specialized | call=firstOf(second=text,first=number) map=[first<-1,second<-0] eval=[0,1] type-arguments=[int,String] result=int instance=firstOf<int,String>
    val result = firstOf(second = text, first = number)
    text
}

func namedOrderedMix(number: int, text: String): String {
    // expect: named.mix-no-skip | Specialized | call=firstOf(first=number,text) map=[first<-0,second<-1] eval=[0,1] type-arguments=[int,String] result=int instance=firstOf<int,String>
    val result = firstOf(first = number, text)
    text
}

func namedMixAfterHole(number: int, text: String): () {
    // expect: named.mix-after-hole | Rejected | call=firstOf(second=text,number) diagnostic=PositionalAfterReorder fir=absent
    val invalid = firstOf(second = text, number)
}

func namedUnknown(text: String): () {
    // expect: named.unknown | Rejected | call=identity(missing=text) diagnostic=UnknownArgumentName fir=absent
    val invalid = identity(missing = text)
}

func namedDuplicate(number: int): () {
    // expect: named.duplicate | Rejected | call=firstOf(first=number,first=number) diagnostic=ArgumentAlreadyFilled fir=absent
    val invalid = firstOf(first = number, first = number)
}

func namedOrderDoesNotChangeGenericIdentity(number: int, text: String): () {
    // expect: named.same-key.one | Specialized | call=firstOf(first=number,second=text) type-arguments=[int,String] instance=firstOf<int,String> site=one
    val one = firstOf(first = number, second = text)
    // expect: named.same-key.two | Specialized | call=firstOf(second=text,first=number) type-arguments=[int,String] instance=firstOf<int,String> site=two canonical-instance=same-as-one
    val two = firstOf(second = text, first = number)
}

// 3. Mapping is separate from source evaluation order
// ---------------------------------------------------
//
// Every explicit expression runs once in source order, even when its formal parameter index differs.
// FIR stores the parameter mapping but must preserve the original argument origin and evaluation
// sequence. Generic specialization cannot sort actuals by formal index to make its signature fit.

func namedReorderKeepsEvaluationOrder(): int {
    // expect: evaluation.named-reorder | Specialized | call=firstOf(second=mark(2),first=mark(1)) map=[first<-1,second<-0] eval=[mark(2),mark(1)] count=2 type-arguments=[int,int] instance=firstOf<int,int>
    val result = firstOf(second = mark(2), first = mark(1))
    result
}

func positionalThenNamedKeepsEvaluationOrder(): int {
    // expect: evaluation.positional-named | Specialized | call=firstOf(mark(1),second=mark(2)) map=[first<-0,second<-1] eval=[mark(1),mark(2)] count=2 type-arguments=[int,int]
    val result = firstOf(mark(1), second = mark(2))
    result
}

func sideEffectfulNestedArgumentRunsOnce(): int {
    // expect: evaluation.nested | Specialized | call=firstOf(identity(mark(7)),identity(mark(8))) eval=[identity(mark(7)),identity(mark(8))] nested-count=2 distinct-call-origins=true
    val result = firstOf(identity(mark(7)), identity(mark(8)))
    result
}

func expectedTypeDoesNotReorderArguments(): String {
    // expect: evaluation.contextual-result | Specialized | call=identity(firstOf(second=mark(1),first="s")) eval=[mark(1),"s"] source-order-preserved=true type-arguments=[String,int] result=String
    val result: String = identity(firstOf(second = mark(1), first = "s"))
    result
}

func explicitArgumentHasOneSourceOrigin(value: int): int {
    // expect: evaluation.explicit | Specialized | call=identity<int>(mark(value)) eval=[mark(value)] count=1 call-origin=source-call argument-origin=mark-call
    val result = identity<int>(mark(value))
    result
}

// 4. Generic inference evidence follows the completed mapping
// ----------------------------------------------------------
//
// Value arguments contribute evidence only after mapping identifies their formals. Named order has
// no inference priority. Exact ref/out evidence is not a conversion. Explicit type arguments fix
// their own parameters; defaults and empty varargs are not hidden inference inputs.

func evidenceUsesNamedFormalOrder(number: int, text: String): String {
    // expect: evidence.named | Specialized | call=firstOf(second=text,first=number) map=[first<-1,second<-0] type-arguments=[int,String] evidence=[argument1->T=int,argument0->U=String]
    val result = firstOf(second = text, first = number)
    text
}

func evidenceUsesExpectedResult(): String {
    // expect: evidence.expected | Specialized | call=identity("payload") type-arguments=[String] evidence=[input=String,expected=String] result=String
    val result: String = identity("payload")
    result
}

func explicitTypeArgumentsStillCheckValues(number: long): long {
    // expect: evidence.explicit-checked | Specialized | call=identity<long>(number) type-arguments=[long] result=long conversion=identity
    val result = identity<long>(number)
    result
}

func explicitTypeArgumentMismatch(number: int, text: String): () {
    // expect: evidence.explicit-mismatch | Rejected | call=identity<String>(number) diagnostic=ArgumentConversionFailure fir=absent
    val invalid = identity<String>(number)
}

func defaultDoesNotInferUnusedParameter(number: int): () {
    // expect: evidence.default-is-not-input | Rejected | call=withUnrelatedDefault() default=count=1 evidence-from-default=false diagnostic=MissingRequiredParameter,value fir=absent
    val invalid = withUnrelatedDefault()
}

func expectedResultCannotOverrideExplicitArgument(): String {
    // expect: evidence.expected-vs-explicit | Rejected | call=identity<int>(1) expected=String diagnostic=ResultConversionFailure type-arguments=[int] fir=absent
    val invalid: String = identity<int>(1)
}

func twoGenericArgumentsKeepTheirOrdinals(number: int, text: String): () {
    // expect: evidence.ordinal-order | Specialized | call=firstOf(second=text,first=number) type-arguments=[int,String] ordinal0=T ordinal1=U
    val result = firstOf(second = text, first = number)
}

// 5. Exact ref/out mapping
// ------------------------
//
// Ref/out markers belong to the actual argument. Their formal parameter must declare the matching
// mode. A ref/out generic argument is exact evidence after mapping and must denote eligible mutable
// storage. A conversion cannot turn a different location into the required type.

func inferFromNamedRef(value: int): int {
    var current = value
    // expect: ref.named-exact | Specialized | call=replace(value=ref current,replacement=8) map=[value<-0,replacement<-1] type-arguments=[int] evidence=[ref=current:int,replacement=int] exact=true
    val result = replace(value = ref current, replacement = 8)
    result
}

func inferFromPositionalRef(value: String): String {
    var current = value
    // expect: ref.positional-exact | Specialized | call=replace(ref current,"next") type-arguments=[String] exact=true result=String
    replace(ref current, "next")
}

func refAndValueConflict(value: int): () {
    var current = value
    // expect: ref.value-conflict | Rejected | call=replace(ref current,"text") exact=[int] lower=[String] diagnostic=ConflictingGenericEvidence fir=absent
    val invalid = replace(ref current, "text")
}

func refRequiresMutableStorage(value: int): () {
    val immutable = value
    // expect: ref.immutable | Rejected | call=replace(ref immutable,1) diagnostic=RefRequiresWritableStorage fir=absent
    val invalid = replace(ref immutable, 1)
}

func refRequiresExactType(value: int): () {
    var narrow: byte = 1
    // expect: ref.no-widen | Rejected | call=replace(ref narrow,1) diagnostic=RefTypeMismatch actual=byte inferred=int fir=absent
    val invalid = replace(ref narrow, 1)
}

func outReceivesTypeFromOtherArgument(value: int): int {
    var result: int
    // expect: out.inferred-from-source | Specialized | call=assign(out result,source=value) type-arguments=[int] evidence=[out=no-prior-type,source=int] assigned-on-normal-return=true
    assign(out result, value)
    result
}

func outNamedMapping(value: String): String {
    var result: String
    // expect: out.named | Specialized | call=assign(source=value,value=out result) map=[value<-0,source<-1] type-arguments=[String] exact-out=true
    assign(source = value, value = out result)
    result
}

func outVarGetsTypeAfterSelection(value: int): int {
    // expect: out-var.selected-type | SourceBound | call=assign(out var result,source=value) type-arguments=[int] local=result:int created-after-winner=true fir=not-qualified
    assign(out var result, source = value)
    result
}

func outVarAloneHasNoEvidence(): () {
    // expect: out-var.no-evidence | Rejected | call=assign(out var result) diagnostic=MissingRequiredParameter fir=absent
    assign(out var result)
}

func outVarNamedMapping(value: int): () {
    // expect: out-var.named | SourceBound | call=assign(source=value,value=out var result) map=[value<-1,source<-0] type-arguments=[int] local=result:int fir=not-qualified
    assign(source = value, value = out var result)
    println(result)
}

// 6. Defaults are mapped after overload selection
// -----------------------------------------------
//
// Default expressions are omitted inputs, not source arguments and not generic inference evidence.
// After one candidate wins, defaults are evaluated in declaration order. The currently verified
// native subset does not lower user defaults, so source binding success is not native admission.

func selectedDefaultAfterExplicit(number: int): int {
    // expect: default.selected | SourceBoundNativeRejected | call=earlierDefault(number) type-arguments=[int] map=[value<-0] defaults=[fallback<-earlierDefault.value] eval-explicit=[0] eval-defaults=[fallback] diagnostic=SOL2400
    val result = earlierDefault(number)
    result
}

func defaultNotEvaluatedWhenSupplied(number: int): int {
    // expect: default.suppressed | SourceBoundNativeRejected | call=earlierDefault(value=number,fallback=number) defaults=[] eval=[0,1] type-arguments=[int] diagnostic=SOL2400
    val result = earlierDefault(value = number, fallback = number)
    result
}

func defaultMayUseEarlierParameter(number: int): int {
    // expect: default.earlier-read | SourceBoundNativeRejected | call=earlierDefault(number) default=fallback expression=value substituted-type=int
    earlierDefault(number)
}

func explicitArgumentEvaluatesBeforeDefault(): int {
    // expect: default.evaluation-order | SourceBoundNativeRejected | call=earlierDefault(value=mark(2)) eval-explicit=[mark(2)] eval-defaults=[fallback] default-after-explicit=true
    val result = earlierDefault(value = mark(2))
    result
}

func defaultDoesNotRunAtDeclaration(): int {
    // expect: default.no-definition-evaluation | SourceBoundNativeRejected | declaration=earlierDefault evaluation=only-at-omitting-call
    earlierDefault(3)
}

func explicitBeforeDefaults(): int {
    // expect: default.explicit-then-defaults | SourceBoundNativeRejected | call=defaultedTrace(c=mark(30)) eval-explicit=[mark(30)] eval-defaults=[a=mark(1),b=mark(2)] final-parameter-order=[a,b,c]
    val result = defaultedTrace(c = mark(30))
    result
}

func defaultsRunOncePerOmission(): int {
    // expect: default.once | SourceBoundNativeRejected | call=defaultedTrace(b=mark(20)) eval-explicit=[mark(20)] eval-defaults=[a=mark(1),c=mark(3)] count-per-default=1
    val result = defaultedTrace(b = mark(20))
    result
}

func suppliedValuesSuppressDefaults(): int {
    // expect: default.suppresses | SourceBoundNativeRejected | call=defaultedTrace(a=mark(10),b=mark(20),c=mark(30)) eval-explicit=[0,1,2] eval-defaults=[]
    val result = defaultedTrace(a = mark(10), b = mark(20), c = mark(30))
    result
}

func defaultsDoNotRunAtDefinition(): int {
    // expect: default.definition-time | SourceBoundNativeRejected | declaration=defaultedTrace evaluation=only-at-omitting-call
    val result = defaultedTrace()
    result
}

func defaultSelectionPrecedesEvaluation(): int {
    // expect: default.selection-order | SourceBoundNativeRejected | call=defaultedTrace(b=mark(9)) winner-selected-before-defaults=true eval-explicit=[0] eval-defaults=[a,c]
    val result = defaultedTrace(b = mark(9))
    result
}

func missingNonDefaultStillFails(number: int): () {
    // expect: default.required | Rejected | call=firstOf(first=number) missing=[second] diagnostic=MissingRequiredParameter fir=absent
    val invalid = firstOf(first = number)
}

func defaultCannotHideTypeConflict(number: int): () {
    // expect: default.not-evidence | SourceBoundNativeRejected | call=earlierDefault(value=number) type-arguments=[int] default-evidence=[] fir=absent diagnostic=SOL2400
    val value = earlierDefault(value = number)
}

// 7. Varargs and array-valued named arguments
// -------------------------------------------
//
// A positional vararg expands each actual into an element mapping. A named vararg denotes the
// complete array slot. The source contract distinguishes an omitted empty vararg from a provided
// empty array. The verified native slice currently rejects expanded/user vararg lowering.

func collectExpanded(number: int, another: int): []int {
    // expect: vararg.expanded | SourceBoundNativeRejected | call=collect(number,another) map=[values<-[0,1]] type-arguments=[int] packed=[]int order=[0,1] diagnostic=SOL2400
    collect(number, another)
}

func gatherPositionalAndNamedDone(): []int {
    // expect: vararg.before-named-tail | SourceBoundNativeRejected | call=gather(1,2,3,done=true) map=[head<-0,values<-[1,2],done<-3] type-arguments=[int] native-vararg=reject
    gather(1, 2, 3, done = true)
}

func gatherOmittedHeadAndEmptyVararg(): () {
    // No expected result reaches this local initializer. An empty vararg and the defaulted head do
    // not infer T; the boolean tail only maps to done and contributes no T evidence.
    // expect: vararg.empty-without-evidence | RejectedOrDeferred | call=gather(done=true) map=[head<-default,values<-empty,done<-0] type-arguments=unresolved evidence-from-empty=false diagnostic=MissingGenericEvidence
    val unresolved = gather(done = true)
    ()
}

func gatherExplicitTypeWithEmptyVararg(): []String {
    // expect: vararg.empty-explicit-type | SourceBoundNativeRejected | call=gather<String>(done=true) map=[head<-default,values<-empty,done<-0] type-arguments=[String]
    gather<String>(done = true)
}

func gatherNamedArray(values: []String): []String {
    // expect: vararg.named-whole-array | SourceBoundNativeRejected | call=gather(values=values,done=true) map=[values<-0:whole-array,done<-1] head<-default type-arguments=[String]
    gather(values = values, done = true)
}

func collectNamedArray(values: []String): []String {
    // expect: vararg.named-array | SourceBoundNativeRejected | call=collect(values=values) map=[values<-0:whole-array] type-arguments=[String] parameters=[[]String] diagnostic=SOL2400
    collect(values = values)
}

func collectSpread(values: []int): []int {
    // expect: vararg.spread | SourceBoundNativeRejected | call=collect(*values) map=[values<-0:spread] type-arguments=[int] array-identity=[]int diagnostic=SOL2400
    collect(*values)
}

func collectNamedSpread(values: []int): []int {
    // expect: vararg.named-spread | SourceBoundNativeRejected | call=collect(values=*values) map=[values<-0:whole-array] type-arguments=[int] diagnostic=SOL2400
    collect(values = *values)
}

func collectEmpty(): () {
    // The local has no declared target type, so this call has neither an element nor result bound.
    // expect: vararg.empty | RejectedOrDeferred | call=collect() map=[values<-empty] type-arguments=unresolved diagnostic=MissingGenericEvidence
    val unresolved = collect()
    ()
}

func collectEmptyWithExplicitType(): []int {
    // expect: vararg.empty-explicit | SourceBoundNativeRejected | call=collect<int>() type-arguments=[int] map=[values<-empty] array-result=[]int diagnostic=SOL2400
    collect<int>()
}

func namedArrayCannotMixExpanded(values: []int): () {
    // expect: vararg.array-plus-elements | Rejected | call=collect(values=values,values=values) diagnostic=ArgumentAlreadyFilled fir=absent
    collect(values = values, values = values)
}

func ordinaryArrayIsNotOneVarargElement(values: []int): () {
    // expect: vararg.array-element-mismatch | Rejected | call=collect(values) actual=[]int formal=T diagnostic=ArgumentConversionFailure add-spread=required
    collect(values)
}

// 8. Trailing lambdas and generic callback parameters
// ---------------------------------------------------
//
// A trailing lambda maps to the final function parameter according to the source contract. It can
// supply result evidence once a candidate's function shape is known. Closure conversion and lambda
// body specialization are outside this native slice, even when the source mapping is complete.

func transformTrailingInt(): int {
    // expect: lambda.trailing | SourceBoundNativeRejected | call=transform(4){x->x+1} map=[value<-0,action<-trailing] type-arguments=[int] result=int diagnostic=SOL2400
    transform(4) { x -> x + 1 }
}

func transformTrailingExplicit(): String {
    // expect: lambda.explicit | SourceBoundNativeRejected | call=transform<String>("a"){x->x} type-arguments=[String] callback=(String)->String diagnostic=SOL2400
    transform<String>("a") { x: String -> x }
}

func transformNamedCallback(): int {
    // expect: lambda.named | SourceBoundNativeRejected | call=transform(action={x->x},value=3) map=[value<-1,action<-0] type-arguments=[int] diagnostic=SOL2400
    transform(action = { x: int -> x }, value = 3)
}

func delayedTrailing(): String {
    // expect: lambda.zero-arg | SourceBoundNativeRejected | call=delayed("x"){"later"} map=[value<-0,action<-trailing] type-arguments=[String] diagnostic=SOL2400
    delayed("x") { "later" }
}

func trailingLambdaDoesNotFillEarlierParameter(): int {
    // expect: lambda.final-only | Rejected | call=transform{1} diagnostic=MissingRequiredParameter parameter=value fir=absent
    transform { 1 }
}

func parenthesizedLambdaKeepsItsPosition(): int {
    // expect: lambda.inside-parentheses | SourceBoundNativeRejected | call=transform(3,{x->x}) map=[value<-0,action<-1] type-arguments=[int] not-trailing=true diagnostic=SOL2400
    transform(3, { x: int -> x })
}

// 9. Generic overload candidates own independent maps
// ---------------------------------------------------
//
// Each overload candidate receives its own argument mapping and inference facts. A losing
// candidate's partial type arguments or unsupported body cannot leak into the selected target.

func <T> overloaded(value: T, copy: T): T = value
func overloaded(value: String, copy: String): String = value

func overloadedNamedString(value: String): String {
    // expect: overload.named-string | Specialized | call=overloaded(copy=value,value=value) winner=non-generic map=[value<-1,copy<-0] type-arguments=[] fir-target=source-nongeneric
    overloaded(copy = value, value = value)
}

func overloadedNamedInt(value: int): int {
    // expect: overload.named-int | Specialized | call=overloaded(copy=value,value=value) winner=generic type-arguments=[int] map=[value<-1,copy<-0] instance=overloaded<int>
    overloaded(copy = value, value = value)
}

func overloadMappingCannotSelectBySourceOrder(value: String): String {
    // expect: overload.order-independent | Specialized | call=overloaded(copy=value,value=value) selection=source-ranking-not-declaration-order
    overloaded(copy = value, value = value)
}

func overloadDuplicateArgumentRejectsAllCandidates(value: int): () {
    // expect: overload.duplicate | Rejected | call=overloaded(value,value=value) diagnostic=ArgumentAlreadyFilled no-specialization-created=true
    overloaded(value, value = value)
}

// 10. Nested generic calls retain both mappings and substitutions
// ---------------------------------------------------------------
//
// An inner call is mapped independently. Its selected substitution becomes an ordinary typed
// expression for the outer mapper. The outer call cannot mutate the inner source mapping or share
// an instance solely because both calls appear in one expression.

func nestedSameIdentity(value: int): int {
    // expect: nested.same-map | Specialized | call=identity(identity(value)) outer-map=[value<-0] inner-map=[value<-0] type-arguments=[int] outer-instance=identity<int> inner-instance=identity<int> shared-instance=true
    identity(identity(value))
}

func nestedDifferentTypeArguments(number: int, text: String): int {
    // expect: nested.distinct-map | Specialized | call=firstOf(first=identity(number),second=identity(text)) outer-map=[first<-0,second<-1] outer-type-arguments=[int,String] inner-type-arguments=[int,String] result=int
    val result = firstOf(first = identity(number), second = identity(text))
    result
}

func nestedNamedArgument(value: String): String {
    // expect: nested.named | Specialized | call=firstOf(second=identity(value),first=identity(value)) map=[first<-1,second<-0] eval=[0,1] type-arguments=[String,String] result=String
    firstOf(second = identity(value), first = identity(value))
}

func nestedExpectedResult(value: int): int {
    // expect: nested.expected-map | Specialized | call=identity(firstOf(second=value,first=value)) outer-parameters=[int,int] inner-type-arguments=[int,int] outer-type-arguments=[int] expected-result=int
    val result: int = identity(firstOf(second = value, first = value))
    result
}

// 11. Native FIR boundary for mapped arguments
// --------------------------------------------
//
// The current closed native slice supports direct generic free-function calls whose closed
// arguments and substituted body types pass FIR verification. Named reordering and exact scalar
// ref/out are admitted where their source slots can be tied to FIR parameters. User defaults,
// expanded varargs, spread arguments, closures, unsupported generic members, and unresolved
// evidence are rejected or deferred before a verified program is created.

func nativeNamedScalar(value: int, text: String): int {
    // expect: native.named | Specialized | call=firstOf(second=text,first=value) type-arguments=[int,String] result=int fir-target=closed
    firstOf(second = text, first = value)
}

func nativeExplicitScalar(value: String): String {
    // expect: native.explicit | Specialized | call=identity<String>(value) type-arguments=[String] fir-target=closed
    identity<String>(value)
}

func nativeRefScalar(value: int): int {
    var slot = value
    // expect: native.ref | Specialized | call=replace(ref slot,8) type-arguments=[int] passing=[ref,value] exact=true fir-target=closed
    replace(ref slot, 8)
}

func nativeOutScalar(value: String): String {
    var slot: String
    // expect: native.out | Specialized | call=assign(out slot,source=value) type-arguments=[String] passing=[out,value] assigned-on-return=true fir-target=closed
    assign(out slot, source = value)
    slot
}

func nativeUnsupportedDefault(value: int): int {
    // expect: native.default | SourceBoundNativeRejected | call=earlierDefault(value) type-arguments=[int] source-mapping=complete fir=absent diagnostic=SOL2400
    earlierDefault(value)
}

func nativeUnsupportedVararg(value: int): []int {
    // expect: native.vararg | SourceBoundNativeRejected | call=collect(value) type-arguments=[int] source-mapping=complete fir=absent diagnostic=SOL2400
    collect(value)
}

func nativeUnsupportedClosure(value: int): int {
    // expect: native.closure | SourceBoundNativeRejected | call=transform(value){x->x} type-arguments=[int] source-mapping=complete fir=absent diagnostic=SOL2400
    transform(value) { x: int -> x }
}

func nativeUnresolvedGenericEvidence(): () {
    // expect: native.open | RejectedOrDeferred | call=identity(untyped-empty) no-closed-type-argument no-FIR=true
    val invalid = collect()
}

// 12. Required invariants for argument-to-FIR construction
// --------------------------------------------------------
//
// A compiler implementation conforms only if it preserves all of the following at the call site:
//
// - the selected source declaration identity, including overload identity;
// - the source argument list and evaluation order;
// - one mapping from each supplied argument to exactly one formal, except expanded vararg elements;
// - the source passing mode and the formal ref/out mode;
// - an immutable ordered tuple of explicit or inferred generic type arguments;
// - the final substituted formal parameter and result identities;
// - every omitted default and its original expression owner;
// - the source spans for the call and each mapped argument;
// - a rejection/defer outcome when any required mapping or type proof is unavailable.
//
// Reordering only changes parameter indexes. It never changes the AST argument array or the order
// in which its expressions are evaluated. Duplicate mapping is rejected before an instance key is
// reserved. A rejected overload cannot create a partially specialized FIR target.
