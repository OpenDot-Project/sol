// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
@file:Allow("examples")
module fixtures.models

import std.reflect

annotation class Label(val text: String)

interface Named {
    func name(): String
}

open class Entity(val id: ulong)

@Label("user")
partial class User(id: ulong, var caption: String): Entity(id), Named {
    override func name(): String = this.caption

    val displayName: String {
        get = this.caption
    }

    constructor(): this(0, "anonymous") {}

    companion object {
        func create(name: String): User = User(0, name)
    }
}

partial class User {
    func description(): String = this.displayName
}

readonly struct Point {
    val x: int
    val y: int

    constructor(x, y: int) {
        this.x = x
        this.y = y
    }

    companion object {
        operator func +(left, right: Point): Point = Point(left.x + right.x, left.y + right.y)
    }
}

ref struct Window<T> where T: allows ref struct {
    private var item: T

    ref readonly this[index: int]: T {
        get { return ref this.item }
    }
}

enum Result<T> {
    Empty,
    Value(value: T),
}

class NameDelegate(private var stored: String) {
    operator func getValue(owner: Object?, property: SolProperty<String>): String = this.stored
    operator func setValue(owner: Object?, property: SolProperty<String>, value: String) {
        this.stored = value
    }
}

class Settings {
    var name: String by NameDelegate("Sol")
    var count: int { get; private set; } = 0
}

func <T> identity(value: T): T = value

val nested: Result<Result<int>> = identity<Result<Result<int>>>(Result.Value(Result.Value(42)))

object Main {
    func main(args: []String) {}
}
