#include "weva/grapheme.h"
#include <algorithm>
#include <cstdint>
#include <iterator>

namespace weva {
namespace {
enum Property : uint8_t { Other, CR, LF, Control, Extend, ZWJ, RI, Prepend, SpacingMark, L, V, T, LV, LVT };
struct PropertyRange { uint32_t first, last; uint16_t value; };
#include "grapheme_data.inc"

uint16_t properties(uint32_t cp) {
    if (cp >= 0xAC00 && cp <= 0xD7A3) return (cp - 0xAC00) % 28 == 0 ? LV : LVT;
    const auto* end = std::end(kGraphemeProperties);
    const auto* range = std::lower_bound(std::begin(kGraphemeProperties), end, cp,
        [](const PropertyRange& r, uint32_t value) { return r.last < value; });
    return range != end && cp >= range->first ? range->value : 0;
}

uint32_t decode(std::string_view text, size_t* offset) {
    const size_t start = *offset;
    const uint8_t first = static_cast<uint8_t>(text[(*offset)++]);
    if (first < 0x80) return first;
    const size_t count = first >= 0xC2 && first <= 0xDF ? 2 :
                         first >= 0xE0 && first <= 0xEF ? 3 : first >= 0xF0 && first <= 0xF4 ? 4 : 0;
    if (!count || count > text.size() - start) return 0xFFFD;
    uint32_t cp = first & (0x7F >> count);
    for (size_t i = 1; i < count; ++i) {
        const uint8_t byte = static_cast<uint8_t>(text[start + i]);
        if ((byte & 0xC0) != 0x80) return 0xFFFD;
        cp = (cp << 6) | (byte & 0x3F);
    }
    if ((count == 3 && cp < 0x800) || (count == 4 && cp < 0x10000) ||
        (cp >= 0xD800 && cp <= 0xDFFF) || cp > 0x10FFFF) return 0xFFFD;
    *offset = start + count;
    return cp;
}

bool control(Property p) { return p == CR || p == LF || p == Control; }
}

bool Graphemes::next(size_t* end) {
    if (at_ >= text_.size()) return false;
    uint16_t flags = properties(decode(text_, &at_));
    auto previous = static_cast<Property>(flags & 15);
    bool pictographic = (flags & 16) != 0;
    bool pictographic_zwj = false;
    int indic = (flags & 96) == 32 ? 1 : 0; // consonant, then at least one linker
    int regional = previous == RI ? 1 : 0;
    while (at_ < text_.size()) {
        size_t next = at_;
        flags = properties(decode(text_, &next));
        const auto current = static_cast<Property>(flags & 15);
        const int incb = flags & 96;
        bool join = false;
        if (previous == CR && current == LF) join = true; // GB3
        else if (control(previous) || control(current)) join = false; // GB4/5
        else if (previous == L && (current == L || current == V || current == LV || current == LVT)) join = true; // GB6
        else if ((previous == LV || previous == V) && (current == V || current == T)) join = true; // GB7
        else if ((previous == LVT || previous == T) && current == T) join = true; // GB8
        else if (current == Extend || current == ZWJ || current == SpacingMark || previous == Prepend) join = true; // GB9/9a/9b
        else if (incb == 32 && indic == 2 && profile_ == GraphemeProfile::Unicode) join = true; // GB9c
        else if ((flags & 16) && previous == ZWJ && pictographic_zwj) join = true; // GB11
        else if (previous == RI && current == RI && regional % 2 == 1) join = true; // GB12/13
        if (!join) break; // GB999
        pictographic_zwj = current == ZWJ && pictographic;
        if (flags & 16) pictographic = true;
        else if (current != Extend) pictographic = false;
        if (incb == 32) indic = 1;
        else if (incb == 64 && indic) indic = 2;
        else if (incb != 96 && incb != 64) indic = 0;
        regional = current == RI ? regional + 1 : 0;
        previous = current;
        at_ = next;
    }
    if (end) *end = at_;
    return true;
}

size_t previous_grapheme(std::string_view text, size_t offset, GraphemeProfile profile) {
    offset = std::min(offset, text.size());
    Graphemes clusters(text, profile);
    size_t previous = 0, end = 0;
    while (clusters.next(&end) && end < offset) previous = end;
    return previous;
}

size_t next_grapheme(std::string_view text, size_t offset, GraphemeProfile profile) {
    Graphemes clusters(text, profile);
    size_t end = 0;
    while (clusters.next(&end)) if (end > offset) return end;
    return text.size();
}

namespace {
struct Preceding { size_t start; uint32_t cp; };
Preceding preceding(std::string_view text, size_t end) {
    if (!end) return {0, 0};
    size_t start = end - 1;
    while (start && end - start < 4 && (static_cast<uint8_t>(text[start]) & 0xC0) == 0x80) --start;
    size_t decoded_end = start;
    const uint32_t cp = decode(text.substr(0, end), &decoded_end);
    return decoded_end == end ? Preceding{start, cp} : Preceding{end - 1, 0xFFFD};
}
bool variation(uint32_t cp) { return (properties(cp) & 512) != 0; }
bool modifier(uint32_t cp) { return cp >= 0x1F3FB && cp <= 0x1F3FF; }
bool pictograph(uint32_t cp) { return (properties(cp) & 16) != 0 || modifier(cp); }
bool tag(uint32_t cp) { return cp >= 0xE0020 && cp <= 0xE007E; }
bool regional(uint32_t cp) { return (properties(cp) & 15) == RI; }

// Consume the base of a presentation/modifier suffix, then any ZWJ chain.
// Failed lookahead leaves the unpaired selector/joiner in the field.
size_t emoji_suffix(std::string_view text, size_t start, uint32_t cp, bool join) {
    if (variation(cp) || modifier(cp)) {
        if (!start) return start;
        auto base = preceding(text, start);
        bool selector = false;
        if (modifier(cp) && variation(base.cp)) {
            if (!base.start) return start;
            base = preceding(text, base.start);
            selector = true;
        }
        if (modifier(cp)) {
            if (!(properties(base.cp) & 128)) return start;
            if (selector) join = false;
        } else if (!pictograph(base.cp)) {
            return !variation(base.cp) && !(properties(base.cp) & 256) ? base.start : start;
        }
        start = base.start;
    } else if (!pictograph(cp)) return start;
    while (join && start) {
        const auto zwj = preceding(text, start);
        if (zwj.cp != 0x200D || !zwj.start) break;
        auto base = preceding(text, zwj.start);
        if (variation(base.cp)) {
            if (!base.start) break;
            base = preceding(text, base.start);
            if (!pictograph(base.cp)) break;
            start = base.start;
        } else if (pictograph(base.cp)) {
            start = base.start;
            if (modifier(base.cp)) {
                if (!start) break;
                auto before = preceding(text, start);
                const bool selector = variation(before.cp);
                if (selector && before.start) before = preceding(text, before.start);
                if (!(properties(before.cp) & 128)) break;
                start = before.start;
                if (selector) break;
            }
        } else break;
    }
    return start;
}
}

size_t backward_delete_boundary(std::string_view text, size_t offset) {
    offset = std::min(offset, text.size());
    if (!offset) return 0;
    const auto last = preceding(text, offset);
    size_t start = last.start;
    if (last.cp == '\n') {
        return start && preceding(text, start).cp == '\r' ? start - 1 : start;
    }
    if (regional(last.cp)) {
        size_t at = start, count = 1;
        while (at) {
            auto before = preceding(text, at);
            if (!regional(before.cp)) break;
            at = before.start;
            ++count;
        }
        return count % 2 == 0 ? preceding(text, start).start : start;
    }
    if (last.cp == 0x20E3 && start) {
        auto base = preceding(text, start);
        if (variation(base.cp) && base.start) base = preceding(text, base.start);
        return base.cp == '#' || base.cp == '*' || (base.cp >= '0' && base.cp <= '9') ? base.start : start;
    }
    if (last.cp == 0xE007F && start && tag(preceding(text, start).cp)) {
        while (start && tag(preceding(text, start).cp)) start = preceding(text, start).start;
        if (!start) return start;
        const auto base = preceding(text, start);
        return pictograph(base.cp) || variation(base.cp) ? emoji_suffix(text, base.start, base.cp, false) : start;
    }
    return emoji_suffix(text, start, last.cp, true);
}
} // namespace weva
