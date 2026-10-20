// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: declaration/argument syntax; schemas/targets/constant evaluation remain planned.
module source.annotations.rules

annotation class Route(val path: String)
annotation class Retry(val attempts: int = 3)

@Route("/health")
@Retry(attempts = 2)
func health(): String = "ok"

@deprecated("Use health instead")
func oldHealth(): String = health()

@test
func healthContract(): () { assert(health() == "ok", "Unexpected health response") }

// User annotations have typed constant arguments and declared target/repeatability rules.
// Primitive/bool/String/Rune/enum/type metadata and fixed const arrays are permitted.
// Arbitrary objects, closures, ref/out values and filesystem/network execution are not.
// An annotation schema does not run an arbitrary constructor at compile time.
// Compiler-known names remain snake_case: test, repr, link, allow, warn, deny, forbid,
// must_use and deprecated. File-target annotations precede the module declaration.
// Attribute spelling alone is not proof that schema/effect/constant validation exists.
