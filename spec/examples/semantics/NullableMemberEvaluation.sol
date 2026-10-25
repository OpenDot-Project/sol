// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; safe access flow and runtime branching are pending.
module examples.semantics.nullable_member_evaluation

class Socket {
    func send(value: String): int = value.byteLength
}

func createSocket(): Socket? = null
func nextPayload(): String = "payload"

func sendIfPresent(): int? {
    return createSocket()?.send(nextPayload())
}

func narrowed(socket: Socket?): int? {
    if socket != null {
        return socket.send(nextPayload())
    }
    return null
}

// `createSocket` runs once. If the result is null, nextPayload is skipped and send is not called.
// If the result is non-null, nextPayload executes once and the int result is lifted to int?. Flow
// narrowing has a separate branch proof and keeps the ordinary call non-null on that path.
//
// Safe access is not a lookup retry or a member-name suppression. A missing member on the
// non-null Socket type still reports a missing-member diagnostic. Only nullability is conditional.
