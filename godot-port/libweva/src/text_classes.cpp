#include "weva/text_classes.h"

#include <cctype>

namespace weva {
namespace {

bool between(int cp, int lo, int hi) { return cp >= lo && cp <= hi; }

}   // namespace

// ---- UTF-8 walking -------------------------------------------------------

int utf8_at(std::string_view text, std::size_t at, std::size_t* length) {
    if (length) *length = 1;
    if (at >= text.size()) return 0;
    const auto byte = [&](std::size_t i) { return static_cast<unsigned char>(text[i]); };
    const unsigned char lead = byte(at);
    if (lead < 0x80) return lead;
    std::size_t n = 0;
    int cp = 0;
    if ((lead & 0xE0) == 0xC0) {
        n = 2;
        cp = lead & 0x1F;
    } else if ((lead & 0xF0) == 0xE0) {
        n = 3;
        cp = lead & 0x0F;
    } else if ((lead & 0xF8) == 0xF0) {
        n = 4;
        cp = lead & 0x07;
    } else {
        // A continuation byte or an invalid lead: not a position. One byte, so
        // a caller walking bytes still makes progress.
        return 0;
    }
    if (at + n > text.size()) return 0;
    for (std::size_t i = 1; i < n; ++i) {
        if ((byte(at + i) & 0xC0) != 0x80) return 0;
        cp = (cp << 6) | (byte(at + i) & 0x3F);
    }
    if (length) *length = n;
    return cp;
}

int utf8_before(std::string_view text, std::size_t at, std::size_t* length) {
    if (length) *length = 1;
    if (at == 0 || at > text.size()) return 0;
    std::size_t start = at - 1;
    // Back up over the continuation bytes to the lead that owns them.
    while (start > 0 && (static_cast<unsigned char>(text[start]) & 0xC0) == 0x80) --start;
    std::size_t n = 0;
    const int cp = utf8_at(text, start, &n);
    if (start + n != at) {
        // Malformed: the bytes before `at` are not one codepoint. Treat the
        // single byte as itself so the caller still moves.
        if (length) *length = 1;
        return static_cast<unsigned char>(text[at - 1]);
    }
    if (length) *length = n;
    return cp;
}

// ---- CJK -----------------------------------------------------------------

bool is_cjk_ideographic(int cp) {
    return between(cp, 0x3040, 0x309F) ||   // hiragana
           between(cp, 0x30A0, 0x30FF) ||   // katakana
           between(cp, 0x3400, 0x4DBF) ||   // extension A
           between(cp, 0x4E00, 0x9FFF) ||   // unified ideographs
           between(cp, 0xAC00, 0xD7AF) ||   // hangul syllables
           between(cp, 0xF900, 0xFAFF) ||   // compatibility ideographs
           between(cp, 0xFF01, 0xFF5E) ||   // fullwidth forms
           cp == 0x3005 || cp == 0x3006;    // iteration and closing marks
}

bool is_cjk_supplementary(int cp) { return between(cp, 0x20000, 0x2FA1F); }

bool is_kinsoku_close(int cp) {
    switch (cp) {
        case 0x3001:   // 、
        case 0x3002:   // 。
        case 0x3009:   // 〉
        case 0x300B:   // 》
        case 0x300D:   // 」
        case 0x300F:   // 』
        case 0x3011:   // 】
        case 0x3015:   // 〕
        case 0x3017:   // 〗
        case 0x3019:   // 〙
        case 0x301B:   // 〛
        case 0x309B:   // ゛
        case 0x309C:   // ゜
        case 0xFF09:   // ）
        case 0xFF0C:   // ，
        case 0xFF0E:   // ．
        case 0xFF3D:   // ］
        case 0xFF5D:   // ｝
        case 0xFF60:   // ｠
        case 0xFF61:   // ｡
        case 0xFF63:   // ｣
        case 0xFF64:   // ､
        case 0x2019:   // '
        case 0x201D:   // "
            return true;
        default: return false;
    }
}

bool is_loose_only_kinsoku_close(int cp) {
    // Small kana, which cling to the character before them.
    switch (cp) {
        case 0x3041: case 0x3043: case 0x3045: case 0x3047: case 0x3049:
        case 0x3063: case 0x3083: case 0x3085: case 0x3087: case 0x308E:
        case 0x3095: case 0x3096:
        case 0x30A1: case 0x30A3: case 0x30A5: case 0x30A7: case 0x30A9:
        case 0x30C3: case 0x30E3: case 0x30E5: case 0x30E7: case 0x30EE:
        case 0x30F5: case 0x30F6:
        case 0x30FC:   // ー prolonged sound mark
            return true;
        default: break;
    }
    if (between(cp, 0x31F0, 0x31FF)) return true;   // Ainu phonetic small kana
    switch (cp) {
        // Hyphen-likes.
        case 0x2010: case 0x2013: case 0x301C: case 0x30A0:
        // Iteration marks.
        case 0x3005: case 0x303B: case 0x309D: case 0x309E:
        case 0x30FD: case 0x30FE:
        // Centred punctuation and the fullwidth colons.
        case 0x30FB: case 0xFF1A: case 0xFF1B:
        case 0x203C: case 0x2047: case 0x2048: case 0x2049:
        case 0xFF01: case 0xFF1F:
            return true;
        default: return false;
    }
}

bool is_kinsoku_open(int cp) {
    switch (cp) {
        case 0x3008:   // 〈
        case 0x300A:   // 《
        case 0x300C:   // 「
        case 0x300E:   // 『
        case 0x3010:   // 【
        case 0x3014:   // 〔
        case 0x3016:   // 〖
        case 0x3018:   // 〘
        case 0x301A:   // 〚
        case 0xFF08:   // （
        case 0xFF3B:   // ［
        case 0xFF5B:   // ｛
        case 0xFF62:   // ｢
        case 0x2018:   // '
        case 0x201C:   // "
            return true;
        default: return false;
    }
}

bool is_cjk_flow_char(int cp) {
    return is_cjk_ideographic(cp) || is_cjk_supplementary(cp) || is_kinsoku_close(cp) ||
           is_kinsoku_open(cp) || is_loose_only_kinsoku_close(cp);
}

// ---- word boundaries -----------------------------------------------------

bool is_word_char(int cp) {
    if (is_cjk_flow_char(cp)) return false;
    if (cp < 0x80) {
        return std::isalnum(static_cast<unsigned char>(cp)) != 0 || cp == '_';
    }
    // Above ASCII and not CJK: accented Latin, Greek, Cyrillic, and the rest
    // of the alphabetic world, which a word is made of. The exceptions are the
    // punctuation and space blocks, which separate.
    if (between(cp, 0x2000, 0x206F)) return false;   // general punctuation
    if (between(cp, 0x3000, 0x303F)) return false;   // CJK punctuation
    if (cp == 0x00A0 || cp == 0x3000) return false;  // the two wide spaces
    if (between(cp, 0x00A1, 0x00BF)) return false;   // Latin-1 punctuation
    if (cp == 0x00D7 || cp == 0x00F7) return false;  // multiply and divide
    return true;
}

bool is_word_separator(int cp) {
    if (is_cjk_flow_char(cp)) return false;
    return !is_word_char(cp);
}

std::size_t previous_word_boundary(std::string_view text, std::size_t at) {
    if (at == 0 || text.empty()) return 0;
    std::size_t i = at < text.size() ? at : text.size();

    // Skip the separators immediately to the left.
    while (i > 0) {
        std::size_t n = 0;
        const int cp = utf8_before(text, i, &n);
        if (!is_word_separator(cp)) break;
        i -= n;
    }
    if (i == 0) return 0;

    // Then one word. A CJK character is a word on its own.
    std::size_t first = 0;
    const int cp = utf8_before(text, i, &first);
    if (is_cjk_flow_char(cp)) return i - first;
    while (i > 0) {
        std::size_t n = 0;
        const int c = utf8_before(text, i, &n);
        if (is_word_separator(c) || is_cjk_flow_char(c)) break;
        i -= n;
    }
    return i;
}

std::size_t next_word_boundary(std::string_view text, std::size_t at) {
    const std::size_t end = text.size();
    if (text.empty()) return 0;
    if (at >= end) return end;
    std::size_t i = at;

    while (i < end) {
        std::size_t n = 0;
        const int cp = utf8_at(text, i, &n);
        if (!is_word_separator(cp)) break;
        i += n;
    }
    if (i >= end) return end;

    std::size_t first = 0;
    const int cp = utf8_at(text, i, &first);
    if (is_cjk_flow_char(cp)) return i + first;
    while (i < end) {
        std::size_t n = 0;
        const int c = utf8_at(text, i, &n);
        if (is_word_separator(c) || is_cjk_flow_char(c)) break;
        i += n;
    }
    return i;
}

void word_range_at(std::string_view text, std::size_t at, std::size_t* start, std::size_t* end) {
    if (start) *start = 0;
    if (end) *end = 0;
    if (text.empty()) return;
    const std::size_t n = text.size();
    std::size_t pivot = at < n ? at : n;

    // Past the last character, or on a separator whose left neighbour is not
    // one: pivot left, so clicking the trailing edge of a word takes the word.
    if (pivot == n) {
        std::size_t back = 0;
        utf8_before(text, pivot, &back);
        pivot -= back;
    } else {
        std::size_t len = 0;
        const int here = utf8_at(text, pivot, &len);
        if (is_word_separator(here) && pivot > 0) {
            std::size_t back = 0;
            const int left = utf8_before(text, pivot, &back);
            if (!is_word_separator(left)) pivot -= back;
        }
    }
    // A byte in the middle of a codepoint is not a position.
    while (pivot > 0 && (static_cast<unsigned char>(text[pivot]) & 0xC0) == 0x80) --pivot;

    std::size_t len = 0;
    const int cp = utf8_at(text, pivot, &len);
    if (is_cjk_flow_char(cp)) {
        // One character, one unit -- selecting a whole Japanese sentence on a
        // double click is not what a browser does.
        if (start) *start = pivot;
        if (end) *end = pivot + len;
        return;
    }

    const bool separators = is_word_separator(cp);
    std::size_t s = pivot;
    std::size_t e = pivot + len;
    while (s > 0) {
        std::size_t back = 0;
        const int left = utf8_before(text, s, &back);
        if (is_cjk_flow_char(left) || is_word_separator(left) != separators) break;
        s -= back;
    }
    while (e < n) {
        std::size_t fwd = 0;
        const int right = utf8_at(text, e, &fwd);
        if (is_cjk_flow_char(right) || is_word_separator(right) != separators) break;
        e += fwd;
    }
    if (start) *start = s;
    if (end) *end = e;
}

}   // namespace weva
