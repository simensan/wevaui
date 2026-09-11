#include "weva/bidi.h"
#include "weva/style_resolver.h"

#include "unicode/ubidi.h"
#include "unicode/uchar.h"
#include "unicode/utf8.h"

#include <map>
#include <string>

namespace weva {

namespace {

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

std::string_view trim(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
    return s;
}

void append_utf16(std::u16string* out, UChar32 c) {
    if (c < 0) c = 0xFFFD;
    if (c <= 0xFFFF) {
        out->push_back(static_cast<char16_t>(c));
    } else {
        c -= 0x10000;
        out->push_back(static_cast<char16_t>(0xD800 + (c >> 10)));
        out->push_back(static_cast<char16_t>(0xDC00 + (c & 0x3FF)));
    }
}

// A character that can change the outcome: anything right-to-left, an
// Arabic number, or an explicit formatting character in the source.
bool directional(UChar32 c) {
    switch (u_charDirection(c)) {
        case U_RIGHT_TO_LEFT:
        case U_RIGHT_TO_LEFT_ARABIC:
        case U_ARABIC_NUMBER:
        case U_LEFT_TO_RIGHT_EMBEDDING:
        case U_LEFT_TO_RIGHT_OVERRIDE:
        case U_RIGHT_TO_LEFT_EMBEDDING:
        case U_RIGHT_TO_LEFT_OVERRIDE:
        case U_POP_DIRECTIONAL_FORMAT:
        case U_FIRST_STRONG_ISOLATE:
        case U_LEFT_TO_RIGHT_ISOLATE:
        case U_RIGHT_TO_LEFT_ISOLATE:
        case U_POP_DIRECTIONAL_ISOLATE:
            return true;
        default:
            return false;
    }
}

// The controls an inline box's `unicode-bidi` opens (CSS Writing Modes 3
// §2.2), and what closes them.
struct Controls {
    std::u16string open, close;
};

Controls controls_for(const ComputedStyle* style) {
    Controls c;
    if (!style) return c;
    const std::string_view ub = trim(style->get("unicode-bidi"));
    if (ub.empty() || iequals(ub, "normal")) return c;
    const bool rtl = is_rtl(style);
    if (iequals(ub, "embed")) {
        c.open = rtl ? u"\u202B" : u"\u202A";   // RLE / LRE
        c.close = u"\u202C";                     // PDF
    } else if (iequals(ub, "bidi-override")) {
        c.open = rtl ? u"\u202E" : u"\u202D";   // RLO / LRO
        c.close = u"\u202C";
    } else if (iequals(ub, "isolate")) {
        c.open = rtl ? u"\u2067" : u"\u2066";   // RLI / LRI
        c.close = u"\u2069";                     // PDI
    } else if (iequals(ub, "isolate-override")) {
        c.open = rtl ? u"\u2067\u202E" : u"\u2066\u202D";
        c.close = u"\u202C\u2069";
    } else if (iequals(ub, "plaintext")) {
        c.open = u"\u2068";                      // FSI
        c.close = u"\u2069";
    }
    return c;
}

} // namespace

bool resolve_bidi_levels(std::vector<InlineItem>* items, const ComputedStyle* container_style) {
    if (!items || items->empty()) return false;
    const bool paragraph_rtl = is_rtl(container_style);
    const Controls paragraph = controls_for(container_style);
    const bool plaintext = container_style && iequals(trim(container_style->get("unicode-bidi")), "plaintext");

    // The paragraph text in UTF-16, with each text item's code points mapped
    // to their unit index so a level change can be turned back into a byte
    // offset of the item's text.
    std::u16string text;
    struct Piece { size_t byte_offset; size_t unit; };   // a code point's start
    std::vector<std::vector<Piece>> pieces(items->size());
    std::vector<size_t> unit_of(items->size(), 0);        // a non-text item's place
    std::map<BoxId, std::u16string> closers;
    bool needs = paragraph_rtl || !paragraph.open.empty();
    text += paragraph.open;
    for (size_t n = 0; n < items->size(); ++n) {
        const InlineItem& item = (*items)[n];
        unit_of[n] = text.size();
        if (item.is_inline_start()) {
            const Controls c = controls_for(item.style);
            if (!c.open.empty()) {
                needs = true;
                text += c.open;
                closers[item.inline_box_start] = c.close;
            }
        } else if (item.is_inline_end()) {
            const auto it = closers.find(item.inline_box_end);
            if (it != closers.end()) {
                text += it->second;
                closers.erase(it);
            }
        } else if (item.is_break()) {
            text += u'\n';
        } else if (item.is_atom()) {
            text += u'\uFFFC';
        } else if (!item.text.empty()) {
            const auto* s = reinterpret_cast<const uint8_t*>(item.text.data());
            const int32_t length = static_cast<int32_t>(item.text.size());
            int32_t i = 0;
            while (i < length) {
                const int32_t at = i;
                UChar32 c = 0;
                U8_NEXT(s, i, length, c);
                if (c < 0) c = 0xFFFD;
                pieces[n].push_back({static_cast<size_t>(at), text.size()});
                if (!needs && directional(c)) needs = true;
                append_utf16(&text, c);
            }
        }
    }
    if (!needs) return false;

    UErrorCode error = U_ZERO_ERROR;
    UBiDi* bidi = ubidi_open();
    if (!bidi) return false;
    const UBiDiLevel base = plaintext ? UBIDI_DEFAULT_LTR : (paragraph_rtl ? 1 : 0);
    ubidi_setPara(bidi, reinterpret_cast<const UChar*>(text.data()), static_cast<int32_t>(text.size()),
                  base, nullptr, &error);
    const UBiDiLevel* levels = U_FAILURE(error) ? nullptr : ubidi_getLevels(bidi, &error);
    if (!levels || U_FAILURE(error)) {
        ubidi_close(bidi);
        return false;
    }
    const auto level_at = [&](size_t unit) -> uint8_t {
        if (text.empty()) return paragraph_rtl ? 1 : 0;
        if (unit >= text.size()) unit = text.size() - 1;
        return static_cast<uint8_t>(levels[unit]);
    };

    std::vector<InlineItem> out;
    out.reserve(items->size() + 4);
    uint8_t current = static_cast<uint8_t>(ubidi_getParaLevel(bidi));
    for (size_t n = 0; n < items->size(); ++n) {
        const InlineItem& item = (*items)[n];
        if (pieces[n].empty()) {
            // Edges, breaks, atoms and empty runs sit at the level of the text
            // around them; an atom has a place of its own in the paragraph.
            InlineItem copy = item;
            copy.bidi_level = item.is_atom() ? level_at(unit_of[n]) : current;
            out.push_back(copy);
            continue;
        }
        // Split the run where the level changes, on code point boundaries.
        size_t start = 0;
        uint8_t level = level_at(pieces[n][0].unit);
        for (size_t k = 1; k <= pieces[n].size(); ++k) {
            const uint8_t next = k < pieces[n].size() ? level_at(pieces[n][k].unit) : level;
            if (k < pieces[n].size() && next == level) continue;
            const size_t end = k < pieces[n].size() ? pieces[n][k].byte_offset : item.text.size();
            InlineItem copy = item;
            copy.text = item.text.substr(start, end - start);
            copy.bidi_level = level;
            out.push_back(copy);
            start = end;
            level = next;
        }
        current = level;
    }
    ubidi_close(bidi);
    *items = std::move(out);
    return true;
}

void bidi_visual_order(const uint8_t* levels, size_t count, std::vector<int32_t>* out) {
    out->resize(count);
    if (count == 0) return;
    static_assert(sizeof(UBiDiLevel) == sizeof(uint8_t), "UBiDiLevel is a byte");
    ubidi_reorderVisual(reinterpret_cast<const UBiDiLevel*>(levels), static_cast<int32_t>(count), out->data());
}

} // namespace weva
