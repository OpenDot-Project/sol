// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: throws/try syntax and effect type identities; coverage/unwinding remain planned.
module demonstrations.configuration

class ConfigurationException(message: String): Exception(message)

func requireNonempty(value: String): String throws ConfigurationException {
    if value.byteLength == 0 { throw ConfigurationException("A value is required") }
    value
}

func configurationOrDefault(value: String): String {
    val result = try {
        requireNonempty(value)
    } catch (error: ConfigurationException) {
        "default"
    }
    result
}

func propagate(value: String): String throws ConfigurationException {
    requireNonempty(value)
}

// configurationOrDefault handles the checked failure locally; propagate publishes it.
// A RuntimeException subtype would be unchecked. Throwable as a static type is checked.
