// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; executable safe-call control flow remains pending.
module examples.semantics.safe_member_calls

class Connector {
    func send(payload: String): String = payload
    func close(): () { }
}

func makeConnector(): Connector? = null
func makePayload(): String = "request"

func safeCall(connector: Connector?): String? {
    return connector?.send(payload = makePayload())
}

func safeFactoryCall(): String? {
    return makeConnector()?.send(payload = makePayload())
}

func safeVoid(connector: Connector?): () {
    connector?.close()
}

func assertThenCall(connector: Connector?): String {
    return connector!!.send(payload = makePayload())
}

func invalidNullableCall(connector: Connector?): String {
    // Expected diagnostic: ordinary member call needs a flow proof or explicit assertion.
    return connector.send(payload = makePayload())
}

func smartCastCall(connector: Connector?): String? {
    if connector != null {
        return connector.send(payload = makePayload())
    }
    return null
}

// In the safe call, receiver evaluation occurs once. If it returns null, makePayload is not
// evaluated and send is not invoked. If it returns Connector, the argument is evaluated once and
// the result String is lifted to String?. The factory is not called twice to test and then invoke.
//
// Safe access returns a value, not addressable storage. It cannot be passed as ref/out or assigned
// through a setter on a missing receiver. The non-null assertion has an explicit failure path and
// does not make a nullable result from send disappear.
