#pragma once
#include <cstddef>
#include <string_view>

namespace weva {

// Blink's caret/forward-delete segmentation omits GB9c (Indic conjuncts).
// Password masking follows the default Unicode rules, including GB9c.
enum class GraphemeProfile { Unicode, BrowserCaret };

// Unicode 17.0 default extended grapheme clusters (UAX #29). Positions are
// UTF-8 byte offsets. The iterator allocates no storage and returns each
// cluster's end. Invalid UTF-8 bytes advance as individual replacements.
class Graphemes {
public:
    explicit Graphemes(std::string_view text, GraphemeProfile profile = GraphemeProfile::Unicode)
        : text_(text), profile_(profile) {}
    bool next(size_t* end);
private:
    std::string_view text_;
    GraphemeProfile profile_;
    size_t at_ = 0;
};

size_t previous_grapheme(std::string_view text, size_t offset, GraphemeProfile profile = GraphemeProfile::Unicode);
size_t next_grapheme(std::string_view text, size_t offset, GraphemeProfile profile = GraphemeProfile::Unicode);

// Backspace removes one code point for accents and Hangul jamo, but keeps
// emoji sequences together. This is distinct from caret navigation (Chrome).
size_t backward_delete_boundary(std::string_view text, size_t offset);

} // namespace weva
