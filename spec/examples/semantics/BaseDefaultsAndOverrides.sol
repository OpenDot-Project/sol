// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete inherited default binding/virtual execution is pending.
module examples.semantics.base_defaults_and_overrides

open class Renderer<T: std.core.Object> {
    open func render(value: T, width: int = 80): String = value.toString()
}

class TextRenderer: Renderer<String> {
    override func render(value: String, width: int): String = value
}

func useRenderer(renderer: TextRenderer): String {
    return renderer.render(value = "content")
}

func throughBase(renderer: Renderer<String>): String {
    return renderer.render("content")
}

class NarrowRenderer: Renderer<String> {
    override func render(value: String, width: int): String throws std.stdio.IOException = value
}

// The omitted width comes from Renderer<String>, even when the runtime implementation is
// TextRenderer. The default expression is selected from the static declaration contract and is
// evaluated once after explicit arguments. An override cannot redeclare a different default.
//
// Checked effects on an override cannot widen the base budget. The NarrowRenderer declaration is
// intentionally invalid if IOException is not admitted by the base contract. The call does not
// silently widen or discard that effect during member lookup.
