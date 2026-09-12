#pragma once

// The Unicode Bidirectional Algorithm (UAX #9) over a paragraph's inline
// items, as CSS Writing Modes 3 §2 applies it: `direction` sets the paragraph
// level, `unicode-bidi` on an inline box becomes the matching embedding,
// override or isolate controls around its content, and every text item is
// split where the resolved embedding level changes so that each run the host
// shapes is one direction. A line then places its fragments in visual order
// (UAX #9 L2) before alignment. ICU's ubidi does the resolving; the core's ICU
// build carries it.
//
// What this does not do: reorder glyphs inside a run (the host shaper's job:
// TextServer shapes an Arabic or Hebrew run right-to-left on its own, TextCore
// does not), mirror brackets, or move the caret visually.

#include "weva/inline_layout.h"

#include <cstdint>
#include <vector>

namespace weva {

// Resolves the items' levels for the paragraph `container_style` describes.
// Returns false, touching nothing, for a left-to-right paragraph with no
// right-to-left text and no unicode-bidi anywhere -- the common case, which
// costs one pass over the text and no ICU call.
bool resolve_bidi_levels(std::vector<InlineItem>* items, const ComputedStyle* container_style);

// out[visual] = logical index, for fragments in logical order with these
// levels. All-zero levels give the identity.
void bidi_visual_order(const uint8_t* levels, size_t count, std::vector<int32_t>* out);

} // namespace weva
