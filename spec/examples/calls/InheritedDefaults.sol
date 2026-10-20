// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: inherited default origins available; receiver dispatch and execution remain planned.
module demonstrations.defaults

open class Renderer {
    open func render(width: int = 80, height: int = 25): int = width * height
}

class CompactRenderer: Renderer {
    override func render(width: int, height: int): int = width + height
}

func use(base: Renderer, derived: CompactRenderer): () {
    val first = base.render()
    val second = derived.render(height = 10)
}

// Overrides repeat parameter types/names but omit defaults. The original default
// expressions remain owned by Renderer.render. A call uses its static contract for
// omitted arguments, then virtual dispatch selects the implementation body.
// Conflicting inherited default origins require a diagnostic, never source-order choice.
