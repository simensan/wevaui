#include "weva/html.h"

#include <cctype>

namespace weva {

namespace {

bool is_tag_name_start(char c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
bool is_tag_name_char(char c) {
    return is_tag_name_start(c) || (c >= '0' && c <= '9') || c == '-' || c == '_';
}
char ascii_to_lower(char c) {
    return (c >= 'A' && c <= 'Z') ? static_cast<char>(c + 32) : c;
}
bool is_ascii_letter_or_digit(char c) {
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}

} // namespace

HtmlTokenizer::HtmlTokenizer(std::string_view source, SymbolTable* symbols)
    : src_(source), symbols_(symbols) {}

uint32_t HtmlTokenizer::peek_codepoint(int* len) const {
    *len = 1;
    if (pos_ >= src_.size()) return 0;
    auto b0 = static_cast<unsigned char>(src_[pos_]);
    if (b0 < 0x80) return b0;

    auto cont = [&](std::size_t i) -> bool {
        return pos_ + i < src_.size() &&
               (static_cast<unsigned char>(src_[pos_ + i]) & 0xC0) == 0x80;
    };
    auto at = [&](std::size_t i) -> uint32_t {
        return static_cast<unsigned char>(src_[pos_ + i]) & 0x3F;
    };

    if ((b0 & 0xE0) == 0xC0 && cont(1)) { *len = 2; return ((b0 & 0x1Fu) << 6) | at(1); }
    if ((b0 & 0xF0) == 0xE0 && cont(1) && cont(2)) {
        *len = 3; return ((b0 & 0x0Fu) << 12) | (at(1) << 6) | at(2);
    }
    if ((b0 & 0xF8) == 0xF0 && cont(1) && cont(2) && cont(3)) {
        *len = 4; return ((b0 & 0x07u) << 18) | (at(1) << 12) | (at(2) << 6) | at(3);
    }
    return b0;  // invalid sequence: treat the byte literally
}

void HtmlTokenizer::advance() {
    if (pos_ >= src_.size()) return;
    if (src_[pos_] == '\n') { ++line_; column_ = 1; } else { ++column_; }
    ++pos_;
}

void HtmlTokenizer::skip_whitespace() {
    while (!at_end()) {
        int len = 0;
        uint32_t cp = peek_codepoint(&len);
        if (!is_unicode_whitespace(cp)) break;
        for (int i = 0; i < len; ++i) advance();
    }
}

bool HtmlTokenizer::starts_with(std::string_view s) const {
    return src_.compare(pos_, s.size(), s) == 0;
}

bool HtmlTokenizer::starts_with_ignore_case(std::string_view s) const {
    if (pos_ + s.size() > src_.size()) return false;
    for (std::size_t i = 0; i < s.size(); ++i) {
        if (ascii_to_lower(src_[pos_ + i]) != ascii_to_lower(s[i])) return false;
    }
    return true;
}

bool HtmlTokenizer::fail(std::string_view message) {
    if (error_) *error_ = HtmlParseError{std::string(message), line_, column_};
    return false;
}

bool HtmlTokenizer::tokenize(std::vector<HtmlToken>* out, HtmlParseError* error) {
    error_ = error;
    out->clear();
    buf_.clear();
    pos_ = 0; line_ = 1; column_ = 1;

    while (!at_end()) {
        if (peek() == '<') {
            flush_text(out);
            if (!consume_tag(out)) return false;
        } else if (!consume_text()) {
            return false;
        }
    }
    flush_text(out);
    HtmlToken eof;
    eof.kind = HtmlTokenKind::Eof;
    eof.line = line_;
    eof.column = column_;
    out->push_back(std::move(eof));
    return true;
}

bool HtmlTokenizer::consume_text() {
    if (buf_.empty()) mark_token_start();
    if (peek() == '&') {
        std::string resolved;
        if (try_consume_entity(&resolved)) {
            buf_ += resolved;
            return true;
        }
    }
    buf_.push_back(peek());
    advance();
    return true;
}

void HtmlTokenizer::flush_text(std::vector<HtmlToken>* out) {
    if (buf_.empty()) return;
    HtmlToken t;
    t.kind = HtmlTokenKind::Text;
    t.text = buf_;
    t.line = token_line_;
    t.column = token_column_;
    out->push_back(std::move(t));
    buf_.clear();
}

bool HtmlTokenizer::consume_tag(std::vector<HtmlToken>* out) {
    mark_token_start();
    advance();  // '<'
    if (peek() == '!') {
        advance();
        return consume_markup_declaration(out);
    }
    if (peek() == '/') {
        advance();
        return consume_end_tag(out);
    }
    return consume_start_tag(out);
}

Symbol HtmlTokenizer::read_tag_name() {
    std::string name;
    while (!at_end() && is_tag_name_char(peek())) {
        name.push_back(ascii_to_lower(peek()));
        advance();
    }
    return symbols_->intern(name);
}

bool HtmlTokenizer::consume_start_tag(std::vector<HtmlToken>* out) {
    if (!is_tag_name_start(peek())) return fail("Expected tag name after '<'");

    HtmlToken t;
    t.kind = HtmlTokenKind::StartTag;
    t.name = read_tag_name();
    t.line = token_line_;
    t.column = token_column_;

    while (!at_end()) {
        skip_whitespace();
        if (at_end()) break;
        char c = peek();
        if (c == '>') break;
        if (c == '/') {
            advance();
            skip_whitespace();
            if (peek() != '>') return fail("Expected '>' after '/'");
            t.self_closing = true;
            break;
        }
        HtmlAttribute attr;
        if (!read_attribute(&attr)) return false;
        t.attributes.push_back(std::move(attr));
    }
    if (at_end() || peek() != '>') return fail("Unterminated start tag");
    advance();
    out->push_back(std::move(t));
    return true;
}

bool HtmlTokenizer::consume_end_tag(std::vector<HtmlToken>* out) {
    if (!is_tag_name_start(peek())) return fail("Expected tag name after '</'");
    HtmlToken t;
    t.kind = HtmlTokenKind::EndTag;
    t.name = read_tag_name();
    t.line = token_line_;
    t.column = token_column_;
    skip_whitespace();
    if (at_end() || peek() != '>') return fail("Unterminated end tag");
    advance();
    out->push_back(std::move(t));
    return true;
}

bool HtmlTokenizer::read_attribute(HtmlAttribute* out) {
    std::string name;
    while (!at_end()) {
        int len = 0;
        uint32_t cp = peek_codepoint(&len);
        char c = peek();
        if (is_unicode_whitespace(cp) || c == '/' || c == '>' || c == '=' ||
            c == '"' || c == '\'') {
            break;
        }
        for (int i = 0; i < len; ++i) { name.push_back(ascii_to_lower(peek())); advance(); }
    }
    if (name.empty()) return fail("Expected attribute name");
    out->name = symbols_->intern(name);

    skip_whitespace();
    if (peek() != '=') { out->value.clear(); return true; }
    advance();
    skip_whitespace();
    return read_attribute_value(&out->value);
}

bool HtmlTokenizer::read_attribute_value(std::string* out) {
    out->clear();
    char c = peek();
    if (c == '"' || c == '\'') {
        char quote = c;
        advance();
        while (!at_end() && peek() != quote) {
            std::string resolved;
            if (peek() == '&' && try_consume_entity(&resolved)) {
                *out += resolved;
                continue;
            }
            out->push_back(peek());
            advance();
        }
        if (at_end()) return fail("Unterminated attribute value");
        advance();
        return true;
    }
    while (!at_end()) {
        int len = 0;
        uint32_t cp = peek_codepoint(&len);
        char ch = peek();
        if (is_unicode_whitespace(cp) || ch == '>' || ch == '/') break;
        std::string resolved;
        if (ch == '&' && try_consume_entity(&resolved)) {
            *out += resolved;
            continue;
        }
        for (int i = 0; i < len; ++i) { out->push_back(peek()); advance(); }
    }
    return true;
}

bool HtmlTokenizer::consume_markup_declaration(std::vector<HtmlToken>* out) {
    if (starts_with("--")) {
        advance(); advance();
        return consume_comment(out);
    }
    if (starts_with_ignore_case("DOCTYPE")) {
        for (int i = 0; i < 7; ++i) advance();
        return consume_doctype(out);
    }
    return fail("Unknown markup declaration");
}

bool HtmlTokenizer::consume_comment(std::vector<HtmlToken>* out) {
    std::string body;
    while (!at_end()) {
        // Matches C#'s bound exactly: pos+2 < length, so a comment ending at
        // the very last byte of the buffer is treated as unterminated.
        if (peek() == '-' && pos_ + 2 < src_.size() &&
            src_[pos_ + 1] == '-' && src_[pos_ + 2] == '>') {
            advance(); advance(); advance();
            HtmlToken t;
            t.kind = HtmlTokenKind::Comment;
            t.text = std::move(body);
            t.line = token_line_;
            t.column = token_column_;
            out->push_back(std::move(t));
            return true;
        }
        body.push_back(peek());
        advance();
    }
    return fail("Unterminated comment");
}

bool HtmlTokenizer::consume_doctype(std::vector<HtmlToken>* out) {
    while (!at_end() && peek() != '>') advance();
    if (at_end()) return fail("Unterminated DOCTYPE");
    advance();
    HtmlToken t;
    t.kind = HtmlTokenKind::DocType;
    t.line = token_line_;
    t.column = token_column_;
    out->push_back(std::move(t));
    return true;
}

bool HtmlTokenizer::try_consume_entity(std::string* out) {
    out->clear();
    std::size_t saved_pos = pos_;
    int saved_line = line_, saved_col = column_;
    auto restore = [&] { pos_ = saved_pos; line_ = saved_line; column_ = saved_col; };

    if (peek() != '&') return false;
    advance();
    if (at_end()) { restore(); return false; }

    if (peek() == '#') {
        advance();
        bool hex = false;
        if (!at_end() && (peek() == 'x' || peek() == 'X')) { hex = true; advance(); }
        uint64_t code = 0;
        int digits = 0;
        while (!at_end()) {
            char ch = peek();
            int v = -1;
            if (ch >= '0' && ch <= '9') v = ch - '0';
            else if (hex && ch >= 'a' && ch <= 'f') v = 10 + (ch - 'a');
            else if (hex && ch >= 'A' && ch <= 'F') v = 10 + (ch - 'A');
            if (v < 0) break;
            code = code * (hex ? 16 : 10) + static_cast<uint64_t>(v);
            if (code > 0x110000) code = 0x110000;   // saturate; C# would overflow
            advance();
            ++digits;
        }
        if (digits == 0 || at_end() || peek() != ';') { restore(); return false; }
        advance();
        if (!append_utf8(static_cast<uint32_t>(code), out)) { restore(); return false; }
        return true;
    }

    // C# uses char.IsLetterOrDigit here, which accepts Unicode letters. No
    // named entity in the table is non-ASCII, so the only reachable difference
    // is how far a doomed lookup scans before failing — and it restores either
    // way. ASCII keeps this branch allocation-light.
    std::string name;
    while (!at_end() && is_ascii_letter_or_digit(peek())) {
        name.push_back(peek());
        advance();
    }
    if (name.empty() || at_end() || peek() != ';') { restore(); return false; }
    advance();
    std::string_view resolved;
    if (html_entities::lookup(name, &resolved)) {
        *out = std::string(resolved);
        return true;
    }
    restore();
    return false;
}

} // namespace weva
