// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; generic receiver and method specialization are incomplete.
module examples.semantics.receiver_type_method_args

class Repository<T>(val item: T) {
    func <R> convert(transform: (T) -> R): R = transform(item)
    func current(): T = item
}

func useString(): int {
    val repository: Repository<String> = Repository("value")
    val result: int = repository.convert({ text: String -> text.byteLength })
    return result
}

func explicitMethodType(): String {
    val repository: Repository<int> = Repository(4)
    return repository.convert<String>({ value: int -> value.toString() })
}

func readOwnerType(): int {
    val repository: Repository<int> = Repository(9)
    return repository.current()
}

// Repository<T>'s T is fixed by the receiver. The method R is inferred from the lambda result or
// explicit method argument. No parameter-name collision or map replacement can turn the receiver
// into Repository<String> when its declared source type is Repository<int>.
