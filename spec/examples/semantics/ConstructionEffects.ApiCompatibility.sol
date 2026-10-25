// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: public construction contracts survive calls and distribution metadata.
module spec.construction.apicompatibility

class StorageFailure: Exception {}
class NetworkFailure: Exception {}
class ValidationFailure: Exception {}

// Public constructor parameters and their names participate in named-call source compatibility.
public class PublicRecord(val id: int, val label: String)

func useNamedPublicRecord(): PublicRecord = PublicRecord(label = "record", id = 1)

// Renaming `label` is a source API change even when native layout is unchanged.
func callSiteDependsOnParameterName(): PublicRecord = PublicRecord(id = 2, label = "named")

// A public default is evaluated at each call that omits its parameter.
public class DefaultedRecord(val id: int, val label: String = defaultLabel())

func defaultLabel(): String = "generated"

func usePublicDefault(): DefaultedRecord = DefaultedRecord(3)

// Changing a default can change call values and effects without changing native constructor arity.
public class EffectfulDefaultRecord(val id: int, val label: String = loadLabel())

func loadLabel(): String throws StorageFailure { "loaded" }

func useEffectfulDefault(): EffectfulDefaultRecord throws StorageFailure {
    EffectfulDefaultRecord(4)
}

// Supplying the argument suppresses only that default's effect.
func supplyEffectfulDefault(): EffectfulDefaultRecord {
    EffectfulDefaultRecord(5, "explicit")
}

// Checked constructor effects are part of the public call contract.
public class RemoteRecord(val id: int) throws NetworkFailure

func createRemoteRecord(): RemoteRecord throws NetworkFailure {
    RemoteRecord(6)
}

// A source-compatible constructor effect change can still break callers.
func remoteCallerNeedsBudget(): RemoteRecord throws NetworkFailure {
    RemoteRecord(7)
}

// A broader public budget can cover a narrower implementation effect by subtype relation.
public class BroadBudgetRecord(val id: int) throws Exception

func broadBudgetCaller(): BroadBudgetRecord throws Exception {
    BroadBudgetRecord(8)
}

// The effect list is not an overload key.
public class ConstructorIdentity {
    constructor(id: int) {}
    // Negative: same signature with different throws is not a second overload.
    // constructor(id: int) throws NetworkFailure {}
}

// Generic constructor operations keep their instantiated checked effect identity.
func <E: Throwable> failWith(error: E): ! throws E { throw error }

func genericConstructionFailure(): ! throws StorageFailure {
    failWith(StorageFailure())
}

// An incompatible caller budget is rejected after generic substitution.
func genericBudgetMismatch(): ! throws ValidationFailure {
    failWith(StorageFailure())
}

// A generic type argument does not make a non-Throwable type a valid checked effect.
func genericConstraintBoundary(): () {}

// Constructor metadata retains the selected target for a named/default call.
public class MetadataRecord(val id: int, val label: String = "default")

func selectedMetadataTarget(): MetadataRecord = MetadataRecord(label = "custom", id = 9)

// Rejected constructor candidates must not contribute defaults or effects to the call.
public class CandidateRecord {
    val id: int
    constructor(id: int) { this.id = id }
    constructor(label: String) throws NetworkFailure { this.id = loadId(label) }
}

func loadId(label: String): int throws NetworkFailure = 10

func candidateSelectionDiscardsRejectedEffects(): CandidateRecord = CandidateRecord(11)

// Selected constructor effect is recorded in the public type metadata.
func candidateSelectionKeepsSelectedEffects(): CandidateRecord throws NetworkFailure {
    CandidateRecord("remote")
}

// A derived constructor cannot conceal a base constructor effect in its distribution signature.
open class PublicBase() throws NetworkFailure

public class PublicDerived: PublicBase {
    constructor(): super() throws NetworkFailure {}
}

func downstreamDerivedCall(): PublicDerived throws NetworkFailure {
    PublicDerived()
}

// A change from a pure base constructor to a checked one is a dependent API change.
func dependentBaseContract(): PublicDerived throws NetworkFailure {
    PublicDerived()
}

// A base effect not covered by the derived contract invalidates the derived constructor.
class DerivedEffectDrop: PublicBase {
    constructor(): super() {}
}

// Base constructor symbols are selected explicitly and are not inherited as ordinary methods.
func constructorNotAnInheritedMember(): () {}

// A wrapper that handles an exception must perform a real catch and produce a documented result.
class TryCreateResult {
    val value: RemoteRecord?
    val succeeded: bool
    constructor() {
        try {
            value = RemoteRecord(12)
            succeeded = true
        } catch (error: NetworkFailure) {
            value = null
            succeeded = false
        }
    }
}

func createWithoutEscapingNetworkFailure(): TryCreateResult = TryCreateResult()

// A wrapper cannot silently erase the effect by calling a factory outside a handler.
func invalidWrapperShape(): RemoteRecord throws NetworkFailure {
    RemoteRecord(13)
}

// Explicit wrapping preserves the source cause under a new declared checked type.
class RecordCreationFailure(cause: Throwable): Exception(cause = cause) {}

func createWrapped(): RemoteRecord throws RecordCreationFailure {
    try {
        RemoteRecord(14)
    } catch (error: NetworkFailure) {
        throw RecordCreationFailure(error)
    }
}

// The wrapper signature is a different public contract from RemoteRecord's constructor.
func wrapperCallSite(): RemoteRecord throws RecordCreationFailure {
    createWrapped()
}

// Checked effects remain attached to the constructor even if it is called through a factory.
func remoteFactory(): RemoteRecord throws NetworkFailure {
    RemoteRecord(15)
}

func factoryCallSite(): RemoteRecord throws NetworkFailure {
    remoteFactory()
}

// A factory catches only the type it explicitly handles.
func factoryLeavesOtherFailuresVisible(): RemoteRecord throws Throwable {
    try {
        RemoteRecord(16)
    } catch (error: NetworkFailure) {
        throw error
    }
}

// An initializer's effect is represented even if no explicit constructor body calls it.
public class ImplicitInitializerEffect {
    val value: String = loadLabel()
}

func implicitInitializerCallSite(): ImplicitInitializerEffect throws StorageFailure {
    ImplicitInitializerEffect()
}

// Adding an effectful field initializer is a source-level API change for every constructor caller.
func initializerEffectChangesCallers(): ImplicitInitializerEffect throws StorageFailure {
    ImplicitInitializerEffect()
}

// Public objects expose initialization effects at each access, not only first access.
public object PublicSettings {
    val value: int = loadIdentifier()
}

func loadIdentifier(): int throws StorageFailure = 1

func firstPublicSettingsRead(): int throws StorageFailure = PublicSettings.value
func cachedPublicSettingsRead(): int throws StorageFailure = PublicSettings.value

// A Ready runtime cache does not rewrite the public effect metadata.
func cacheDoesNotChangeSignature(): int throws StorageFailure = PublicSettings.value

// Public partial types expose one merged constructor and one initialization part contract.
public partial class PartialPublicRecord {
    val first: int = 1
    constructor(second: int) { this.second = second }
}

public partial class PartialPublicRecord {
    val second: int
    func sum(): int = first + second
}

func publicPartialConstruction(): PartialPublicRecord = PartialPublicRecord(2)

// Adding an initializer to another part is a rejected change, not an ordering update.
public partial class PartialPublicConflict {
    val first: int = 3
    constructor() {}
}

public partial class PartialPublicConflict {
    val second: int = 4
}

// Source distribution metadata records resolved partial-part identity and initializer origin.
func partialPublicMetadata(): () {}

// Metadata without effect records cannot be consumed as a pure constructor contract.
func incompleteBinaryMetadata(value: RemoteRecord): RemoteRecord throws NetworkFailure {
    RemoteRecord(value.id)
}

// A schema mismatch or unresolved exception hierarchy is an error or Deferred proof.
func unresolvedEffectHierarchy(): RemoteRecord throws NetworkFailure {
    RemoteRecord(17)
}

// API compatibility compares nominal exception identity rather than simple printed names.
class SameNameFailure: Exception {}

func differentNominalFailure(): RemoteRecord throws SameNameFailure {
    RemoteRecord(18)
}

// Changing a base class can change which declared supertype covers the constructor effect.
class SpecializedNetworkFailure: NetworkFailure {}

public class SpecializedRecord(val id: int) throws SpecializedNetworkFailure

func callerUsesNetworkSupertype(): SpecializedRecord throws NetworkFailure {
    SpecializedRecord(19)
}

// Parameter names and defaults are source ABI even when native calling convention is unchanged.
func sourceSignatureMetadata(): DefaultedRecord {
    DefaultedRecord(label = "api", id = 20)
}

// Access control remains part of the selected construction contract.
public class InternalConstruction {
    internal constructor() {}
}

// A public factory does not make a private constructor directly callable.
public class HiddenConstructor {
    private constructor() {}
    public companion object {
        public func create(): HiddenConstructor = HiddenConstructor()
    }
}

func publicFactoryCall(): HiddenConstructor = HiddenConstructor.create()

// Constructor metadata records visibility, parameter modes, defaults, generic substitutions,
// effects, base/this target identities, required fields, and initialization-part identity.
func metadataCompletenessVector(): () {}

// Specification note: source callers observe changes to names, defaults, effects, and visibility.
// Specification note: distribution metadata cannot infer purity from the absence of a body.
// Specification note: a wrapper removes only the effects it actually catches or explicitly translates.
// Specification note: partial identity and initialization origin are versioned API facts.
// Specification note: effects from rejected candidates never leak into the selected constructor.
