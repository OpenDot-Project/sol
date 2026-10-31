// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: object/module initialization has an explicit lazy state contract.
module spec.construction.initializationstate

class InitializationFailure: Exception {}
class RecursiveInitialization: RuntimeException {}
class ValueFailure: Exception {}

func record(value: int): () {}

func loadSetting(): int throws InitializationFailure {
    record(1)
    2
}

// Object initialization begins Uninitialized and becomes Ready after successful evaluation.
object Settings {
    val value: int = loadSetting()
}

func accessSettings(): int throws InitializationFailure {
    Settings.value
}

// The source effect remains visible even if another call already changed state to Ready.
func accessReadySettingsStillChecked(): int throws InitializationFailure {
    Settings.value
}

// A failed initializer changes state to Failed and retains the source failure.
object FailedSettings {
    val value: int = failSetting()
}

func failSetting(): int throws InitializationFailure {
    record(10)
    throw InitializationFailure()
}

func firstFailedAccess(): int throws InitializationFailure {
    FailedSettings.value
}

// Repeated access follows the cached-failure contract rather than returning a default value.
func laterFailedAccess(): int throws InitializationFailure {
    FailedSettings.value
}

// The runtime does not publish a partially initialized object on failure.
object PartialSettings {
    val first: int = 20
    val failed: int = failSetting()
    val later: int = 21
}

func noPartialSettingsValue(): int throws InitializationFailure {
    PartialSettings.first
}

// A later initializer is skipped after the first failing initializer.
func laterFieldDoesNotRun(): int throws InitializationFailure {
    PartialSettings.later
}

// Module-level values use the same explicit state model as object initialization.
val moduleSetting: int = loadSetting()

func readModuleSetting(): int throws InitializationFailure {
    moduleSetting
}

// A companion object has its own logical initialization state.
class HasCompanion {
    companion object {
        val value: int = loadSetting()
    }
}

func readCompanion(): int throws InitializationFailure {
    HasCompanion.value
}

// Separate objects have separate state machines even when initializer source is shared.
object FirstSettings {
    val value: int = loadSetting()
}

object SecondSettings {
    val value: int = loadSetting()
}

func readFirst(): int throws InitializationFailure = FirstSettings.value
func readSecond(): int throws InitializationFailure = SecondSettings.value

// An already initialized dependency can be read while another object initializes.
object BaseSettings {
    val value: int = 30
}

object DependentSettings {
    val value: int = BaseSettings.value + 1
}

func readDependent(): int = DependentSettings.value

// Dependency order is established by resolved initializer accesses, not file order.
func dependencyGraphExample(): int = DependentSettings.value

// Same-thread recursive initialization produces the declared cycle failure policy.
object RecursiveA {
    val value: int = RecursiveB.value
}

object RecursiveB {
    val value: int = RecursiveA.value
}

func readRecursiveCycle(): int throws RecursiveInitialization {
    RecursiveA.value
}

// A cycle is not repaired by returning a zero-filled field from an Initializing object.
func noPartialRecursiveRead(): int throws RecursiveInitialization {
    RecursiveB.value
}

// Concurrent readers wait for the winning initializer to publish Ready or Failed.
object ConcurrentSettings {
    val value: int = loadSetting()
}

func concurrentReaderContract(): int throws InitializationFailure {
    ConcurrentSettings.value
}

// A waiting thread resumes with the exact cached failure if the winning initializer failed.
object ConcurrentFailureSettings {
    val value: int = failSetting()
}

func waitingReaderGetsFailure(): int throws InitializationFailure {
    ConcurrentFailureSettings.value
}

// Wait edges are safepoint-aware; a blocked thread does not publish an unrooted partial value.
func safepointAwareWaitContract(): int throws InitializationFailure {
    ConcurrentSettings.value
}

// An object initializer's checked effects are part of its public access contract.
object NetworkSettings {
    val value: int = loadNetworkValue()
}

func loadNetworkValue(): int throws ValueFailure { 1 }

func accessNetworkSettings(): int throws ValueFailure {
    NetworkSettings.value
}

// An access site must retain the declared effect budget after Ready is reached.
func accessNetworkSettingsAgain(): int throws ValueFailure {
    NetworkSettings.value
}

// Negative: a cache hit is not evidence that no checked initialization effect exists.
func accessNetworkSettingsWithoutBudget(): int {
    NetworkSettings.value
}

// The checked initialization effect belongs to the selected getter edge.
class HasEffectfulObjectAccess {
    val value: int { get throws ValueFailure = NetworkSettings.value }
}

func readThroughAccessor(owner: HasEffectfulObjectAccess): int throws ValueFailure {
    owner.value
}

// Object initialization is lazy; constructing an unrelated instance need not access it.
class UnrelatedValue(val value: int)

func createUnrelatedValue(): UnrelatedValue = UnrelatedValue(40)

// A property default can defer module/object access until construction if its initializer runs.
class DependsOnSettings {
    val value: int = NetworkSettings.value
    constructor() throws ValueFailure {}
}

func createDependentInstance(): DependsOnSettings throws ValueFailure {
    DependsOnSettings()
}

// An object initialization failure does not make later fields appear assigned.
object FailingInitializationSequence {
    val first: String = "first"
    val second: String = failString()
    val third: String = "third"
}

func failString(): String throws InitializationFailure { throw InitializationFailure() }

func readLaterInitializationSequence(): String throws InitializationFailure {
    FailingInitializationSequence.third
}

// A module initializer can use lexical cleanup for resources it acquires.
func acquireModuleResource(): ModuleResource { ModuleResource() }

class ModuleResource {
    constructor() {}
    func close(): () {}
}

object ResourceBackedSettings {
    val value: int = initializeWithResource()
}

func initializeWithResource(): int throws InitializationFailure {
    val resource = acquireModuleResource()
    defer { resource.close() }
    failSetting()
}

func readResourceBackedSetting(): int throws InitializationFailure {
    ResourceBackedSettings.value
}

// If initialization fails, local cleanup completes before the state becomes Failed.
func cleanupBeforeFailureCaching(): int throws InitializationFailure {
    ResourceBackedSettings.value
}

// If resource cleanup also fails, the initialization failure remains primary.
func cleanupFailureSuppressedDuringInit(): int throws InitializationFailure {
    ResourceBackedSettings.value
}

class CleanupFailure: Exception {}

func acquireFailingResource(): FailingModuleResource { FailingModuleResource() }

class FailingModuleResource {
    constructor() {}
    func close(): () throws CleanupFailure { throw CleanupFailure() }
}

object DoubleFailureSettings {
    val value: int = initializeWithFailingResource()
}

func initializeWithFailingResource(): int throws InitializationFailure, CleanupFailure {
    val resource = acquireFailingResource()
    defer { resource.close() }
    failSetting()
}

// A later access observes the recorded initialization failure, not a new cleanup retry.
func noRepeatedCleanupRetry(): int throws InitializationFailure {
    DoubleFailureSettings.value
}

// A global reference is published only after its entire initializer succeeds.
object PublishedValue {
    val value: String = "ready"
}

func objectValueAfterReady(): String = PublishedValue.value

// Failed object references are not visible through another global slot.
object GlobalReferenceSlot {
    var value: Object? = null
}

object FailingObjectReference {
    val value: Object = failObject()
}

func failObject(): Object throws InitializationFailure { throw InitializationFailure() }

func noGlobalHalfObject(): Object? = GlobalReferenceSlot.value

// Recursive initialization and ordinary RuntimeException failure remain distinct states.
object RuntimeFailingSettings {
    val value: int = throw RecursiveInitialization()
}

func runtimeFailureAccess(): int = RuntimeFailingSettings.value

// A Failed state retains a root to its cause until metadata/runtime policy releases it.
func failureCauseRooted(): int throws InitializationFailure {
    FailedSettings.value
}

// Specification note: Ready and Failed are stable terminal states for one initialization attempt.
// Specification note: the declared effect is checked on every access, including a cache hit.
// Specification note: no read observes fields while the owning initializer is still in progress.
// Specification note: initialization cycles are explicit failures, not implicit zero values.
// Specification note: cleanup completes before an initializer's failure is cached and published.
