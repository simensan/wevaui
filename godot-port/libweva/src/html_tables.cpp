#include "weva/html.h"

#include <unordered_map>
#include <unordered_set>

namespace weva {

bool is_unicode_whitespace(uint32_t cp) {
    // System.Char.IsWhiteSpace, BMP only (surrogate halves are never
    // whitespace, so a UTF-16 vs code-point reading agrees here).
    switch (cp) {
        case 0x0009: case 0x000A: case 0x000B: case 0x000C: case 0x000D:
        case 0x0020: case 0x0085: case 0x00A0: case 0x1680:
        case 0x2028: case 0x2029: case 0x202F: case 0x205F: case 0x3000:
            return true;
        default:
            return cp >= 0x2000 && cp <= 0x200A;
    }
}

bool append_utf8(uint32_t cp, std::string* out) {
    if (cp > 0x10FFFF) return false;
    if (cp >= 0xD800 && cp <= 0xDFFF) return false;   // see html.h on surrogates
    if (cp < 0x80) {
        out->push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out->push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        out->push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out->push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
        out->push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out->push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
        out->push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out->push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
    return true;
}

namespace html_elements {

static const std::unordered_set<std::string_view>& void_elements() {
    static const std::unordered_set<std::string_view> s = {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"};
    return s;
}

// Block-level start tags that implicitly close an open <p>, per the HTML
// Living Standard "Optional tags" section.
static const std::unordered_set<std::string_view>& closes_open_p() {
    static const std::unordered_set<std::string_view> s = {
        "p", "div", "section", "article", "header", "footer", "nav",
        "aside", "main", "ul", "ol", "table", "form", "h1", "h2", "h3",
        "h4", "h5", "h6", "pre", "address", "blockquote", "hr"};
    return s;
}

// Elements whose end tag is optional. Browsers auto-insert the implicit close,
// so the parser must not reject sequences that are actually well-formed —
// `<section><li>x</section>` is valid HTML.
static const std::unordered_set<std::string_view>& optional_close() {
    static const std::unordered_set<std::string_view> s = {
        "li", "p", "dd", "dt", "td", "th", "tr", "tbody", "thead",
        "tfoot", "option", "optgroup", "colgroup", "caption", "rt", "rp",
        "html", "head", "body"};
    return s;
}

bool is_void(std::string_view tag) { return void_elements().count(tag) != 0; }

bool is_optional_close(std::string_view tag) {
    return !tag.empty() && optional_close().count(tag) != 0;
}

bool should_implicitly_close(std::string_view current, std::string_view start) {
    if (current.empty() || start.empty()) return false;
    if (current == "p") return closes_open_p().count(start) != 0;
    if (current == "li") return start == "li";
    if (current == "dt" || current == "dd") return start == "dt" || start == "dd";
    if (current == "tr") return start == "tr";
    if (current == "td" || current == "th") {
        return start == "td" || start == "th" || start == "tr";
    }
    if (current == "option") return start == "option";
    return false;
}

} // namespace html_elements

namespace html_entities {

bool lookup(std::string_view name, std::string_view* out) {
    // Values are UTF-8 for the same code points the C# table names. nbsp is
    // U+00A0 and shy is U+00AD — both look like ordinary punctuation in a
    // source listing, so they are spelled as escapes here on purpose.
    static const std::unordered_map<std::string_view, std::string_view> map = {
        {"amp", "&"},    {"lt", "<"},        {"gt", ">"},
        {"shy", "­"}, {"quot", "\""},   {"apos", "'"},
        {"nbsp", " "},
        {"copy", "©"},  {"reg", "®"},   {"trade", "™"},
        {"hellip", "…"},{"mdash", "—"}, {"ndash", "–"},
        {"lsquo", "‘"}, {"rsquo", "’"},
        {"ldquo", "“"}, {"rdquo", "”"},
        {"bull", "•"},  {"middot", "·"},{"deg", "°"},
        {"times", "×"}, {"divide", "÷"},
        {"laquo", "«"}, {"raquo", "»"},
    };
    auto it = map.find(name);
    if (it == map.end()) return false;
    *out = it->second;
    return true;
}

} // namespace html_entities

} // namespace weva
