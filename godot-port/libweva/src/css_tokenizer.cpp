#include "weva/css_token.h"
#include "weva/html.h"   // append_utf8

#include <charconv>
#include <cstring>

namespace weva {

namespace {

// CSS whitespace: ASCII only. NOT the HTML tokenizer's Unicode set.
bool is_ws(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
}
bool is_digit(char c) { return c >= '0' && c <= '9'; }

// CSS Syntax L3 §4.2: a name-start code point is a letter, a non-ASCII code
// point, or U+005F. The `>= 0x80` branch is why `.café`, `--日本語` and emoji
// class names tokenize as single identifiers instead of a truncated ASCII
// prefix plus a Delim per byte. Because every UTF-8 continuation byte is also
// >= 0x80, the byte-wise test is equivalent to the code-point-wise one.
bool is_letter(char c) {
    auto u = static_cast<unsigned char>(c);
    return (u >= 'a' && u <= 'z') || (u >= 'A' && u <= 'Z') || u >= 0x80;
}
bool is_name_char(char c) {
    return is_letter(c) || is_digit(c) || c == '-' || c == '_';
}
bool is_ident_start(char a, char b, char /*c*/) {
    if (is_letter(a) || a == '_') return true;
    if (a == '-') return is_letter(b) || b == '_' || b == '-';
    return false;
}
bool is_hex(char c) {
    return is_digit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
int hex_value(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    return 10 + (c - 'A');
}
char map_escape(char c) {
    switch (c) {
        case 'n': return '\n';
        case 'r': return '\r';
        case 't': return '\t';
        case 'f': return '\f';
        default: return c;
    }
}

} // namespace

bool css_parse_double(std::string_view text, double* out) {
    *out = 0;
    if (text.empty()) return false;

    // std::from_chars deliberately does NOT accept a leading '+' (it is only
    // recognised in an exponent), where C#'s NumberStyles.Float does. The CSS
    // tokenizer emits raw text like "+5" and "+.5" straight from the source,
    // so without this every explicitly-signed positive number would parse as 0
    // — silently turning `margin: +5px` into `margin: 0`.
    if (text.front() == '+') text.remove_prefix(1);
    if (text.empty()) return false;

    double v = 0;
    auto* first = text.data();
    auto* last = text.data() + text.size();
    auto res = std::from_chars(first, last, v);
    if (res.ec != std::errc()) return false;   // e.g. a lone "." or "-"
    // C#'s double.TryParse requires the WHOLE string to be the number, while
    // std::from_chars is happy to stop at the first character it cannot use.
    // Without this, "10m" parses as 10 — which turned "10min" into a valid
    // <length> in the @property syntax validator.
    if (res.ptr != last) return false;
    *out = v;
    return true;
}

CssTokenizer::CssTokenizer(std::string_view source, bool strict)
    : src_(source), strict_(strict) {}

void CssTokenizer::advance() {
    if (pos_ >= src_.size()) return;
    if (src_[pos_] == '\n') { ++line_; column_ = 1; } else { ++column_; }
    ++pos_;
}

void CssTokenizer::emit(CssTokenKind kind, std::string text) {
    CssToken t;
    t.kind = kind;
    t.text = std::move(text);
    t.line = token_line_;
    t.column = token_column_;
    out_->push_back(std::move(t));
}

bool CssTokenizer::fail(std::string_view message) {
    if (error_) *error_ = CssParseError{std::string(message), line_, column_};
    return false;
}

bool CssTokenizer::tokenize(std::vector<CssToken>* out, CssParseError* error) {
    out_ = out;
    error_ = error;
    out->clear();
    pos_ = 0; line_ = 1; column_ = 1;

    while (!at_end()) {
        mark_token_start();
        char c = peek();

        if (c == '/' && peek_at(1) == '*') {
            if (!skip_comment()) return false;
            continue;
        }
        // CDO / CDC: HTML comment delimiters are skipped at top level.
        if (c == '<' && peek_at(1) == '!' && peek_at(2) == '-' && peek_at(3) == '-') {
            advance(); advance(); advance(); advance();
            continue;
        }
        if (c == '-' && peek_at(1) == '-' && peek_at(2) == '>') {
            advance(); advance(); advance();
            continue;
        }
        if (is_ws(c)) {
            while (!at_end() && is_ws(peek())) advance();
            emit(CssTokenKind::Whitespace, " ");   // runs collapse to one space
            continue;
        }
        if (c == '"' || c == '\'') {
            if (!consume_string(c)) return false;
            continue;
        }
        if (c == '#') {
            advance();
            if (!at_end() && is_name_char(peek())) {
                std::string name;
                while (!at_end() && is_name_char(peek())) { name.push_back(peek()); advance(); }
                emit(CssTokenKind::Hash, std::move(name));
            } else {
                emit(CssTokenKind::Delim, "#");
            }
            continue;
        }
        if (c == '@') {
            advance();
            if (!at_end() && is_ident_start(peek(), peek_at(1), peek_at(2))) {
                emit(CssTokenKind::AtKeyword, read_ident());
            } else {
                emit(CssTokenKind::Delim, "@");
            }
            continue;
        }
        if (is_digit(c) || ((c == '.' || c == '+' || c == '-') && is_number_start())) {
            consume_numeric();
            continue;
        }
        if (is_ident_start(c, peek_at(1), peek_at(2))) {
            consume_ident_like();
            if (error_ && !error_->message.empty() && strict_) return false;
            continue;
        }

        switch (c) {
            case ',': advance(); emit(CssTokenKind::Comma, ","); continue;
            case ':': advance(); emit(CssTokenKind::Colon, ":"); continue;
            case ';': advance(); emit(CssTokenKind::Semicolon, ";"); continue;
            case '{': advance(); emit(CssTokenKind::LBrace, "{"); continue;
            case '}': advance(); emit(CssTokenKind::RBrace, "}"); continue;
            case '(': advance(); emit(CssTokenKind::LParen, "("); continue;
            case ')': advance(); emit(CssTokenKind::RParen, ")"); continue;
            case '[': advance(); emit(CssTokenKind::LBracket, "["); continue;
            case ']': advance(); emit(CssTokenKind::RBracket, "]"); continue;
            default: break;
        }
        advance();
        emit(CssTokenKind::Delim, std::string(1, c));
    }

    CssToken eof;
    eof.kind = CssTokenKind::Eof;
    eof.line = line_;
    eof.column = column_;
    out->push_back(std::move(eof));
    return true;
}

bool CssTokenizer::skip_comment() {
    advance(); advance();
    while (!at_end()) {
        if (peek() == '*' && peek_at(1) == '/') { advance(); advance(); return true; }
        advance();
    }
    if (strict_) return fail("Unterminated comment");
    return true;
}

bool CssTokenizer::consume_string(char quote) {
    advance();
    std::string sb;
    while (!at_end()) {
        char c = peek();
        if (c == quote) {
            advance();
            emit(CssTokenKind::String, std::move(sb));
            return true;
        }
        if (c == '\n') {
            if (strict_) return fail("Unterminated string");
            // §4.3.5: an unescaped newline is a parse error producing a
            // <bad-string-token>; the newline is NOT consumed, so it still
            // terminates the declaration on the next pass.
            emit(CssTokenKind::BadString, std::move(sb));
            return true;
        }
        if (c == '\\') {
            advance();
            if (at_end()) break;
            char e = peek();
            if (e == '\n') { advance(); continue; }   // line continuation
            if (is_hex(e)) {
                uint32_t code = 0;
                int digits = 0;
                while (!at_end() && digits < 6 && is_hex(peek())) {
                    code = code * 16 + static_cast<uint32_t>(hex_value(peek()));
                    advance();
                    ++digits;
                }
                if (!at_end() && is_ws(peek())) advance();
                // §4.3.7: zero, surrogates and >U+10FFFF are all invalid escape
                // results and must produce U+FFFD. Without the surrogate guard
                // the C# char.ConvertFromUtf32 threw and aborted the whole
                // stylesheet.
                if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) {
                    sb += "\xEF\xBF\xBD";   // U+FFFD
                } else {
                    append_utf8(code, &sb);
                }
                continue;
            }
            sb.push_back(map_escape(e));
            advance();
            continue;
        }
        sb.push_back(c);
        advance();
    }
    if (strict_) return fail("Unterminated string");
    // §4.3.5: EOF in a string is a parse error but still yields a
    // <string-token> carrying what was consumed.
    emit(CssTokenKind::String, std::move(sb));
    return true;
}

void CssTokenizer::consume_numeric() {
    std::string raw;
    double value = read_number(&raw);

    if (!at_end() && peek() == '%') {
        advance();
        CssToken t;
        t.kind = CssTokenKind::Percentage;
        t.text = raw + "%";
        t.number = value;
        t.unit = "%";
        t.line = token_line_;
        t.column = token_column_;
        out_->push_back(std::move(t));
        return;
    }
    if (!at_end() && is_ident_start(peek(), peek_at(1), peek_at(2))) {
        std::string unit = read_ident();
        CssToken t;
        t.kind = CssTokenKind::Dimension;
        t.text = raw + unit;
        t.number = value;
        t.unit = std::move(unit);
        t.line = token_line_;
        t.column = token_column_;
        out_->push_back(std::move(t));
        return;
    }
    CssToken t;
    t.kind = CssTokenKind::Number;
    t.text = raw;
    t.number = value;
    t.line = token_line_;
    t.column = token_column_;
    out_->push_back(std::move(t));
}

double CssTokenizer::read_number(std::string* raw) {
    std::string sb;
    if (!at_end() && (peek() == '+' || peek() == '-')) { sb.push_back(peek()); advance(); }
    while (!at_end() && is_digit(peek())) { sb.push_back(peek()); advance(); }

    if (!at_end() && peek() == '.' && is_digit(peek_at(1))) {
        sb.push_back(peek());
        advance();
        while (!at_end() && is_digit(peek())) { sb.push_back(peek()); advance(); }
    } else if (!at_end() && peek() == '.') {
        // A trailing dot is consumed into the raw text ("5." stays "5.").
        sb.push_back(peek());
        advance();
    }

    if (!at_end() && (peek() == 'e' || peek() == 'E')) {
        std::size_t saved_pos = pos_;
        int saved_line = line_, saved_col = column_;
        std::string ebuf;
        ebuf.push_back(peek());
        advance();
        if (!at_end() && (peek() == '+' || peek() == '-')) { ebuf.push_back(peek()); advance(); }
        if (!at_end() && is_digit(peek())) {
            while (!at_end() && is_digit(peek())) { ebuf.push_back(peek()); advance(); }
            sb += ebuf;
        } else {
            // Not an exponent after all ("1em"): rewind so `em` reads as a unit.
            pos_ = saved_pos; line_ = saved_line; column_ = saved_col;
        }
    }

    *raw = sb;
    double v = 0;
    css_parse_double(sb, &v);   // matches TryParse: failure leaves 0
    return v;
}

void CssTokenizer::consume_ident_like() {
    std::string name = read_ident();
    if (!at_end() && peek() == '(') {
        advance();
        bool is_url = name.size() == 3 &&
                      (name[0] | 0x20) == 'u' && (name[1] | 0x20) == 'r' && (name[2] | 0x20) == 'l';
        if (is_url) { consume_url(name); return; }
        emit(CssTokenKind::Function, std::move(name));
        return;
    }
    emit(CssTokenKind::Ident, std::move(name));
}

bool CssTokenizer::consume_url(const std::string& fn_name) {
    std::size_t saved_pos = pos_;
    int saved_line = line_, saved_col = column_;

    while (!at_end() && is_ws(peek())) advance();
    if (!at_end() && (peek() == '"' || peek() == '\'')) {
        // url("...") is a normal function with a string argument; rewind and
        // let the generic path handle it.
        pos_ = saved_pos; line_ = saved_line; column_ = saved_col;
        emit(CssTokenKind::Function, fn_name);
        return true;
    }

    std::string sb;
    while (!at_end() && peek() != ')') {
        char c = peek();
        if (is_ws(c)) {
            while (!at_end() && is_ws(peek())) advance();
            if (at_end() || peek() != ')') {
                if (strict_) return fail("Invalid url(): whitespace before bad chars");
                consume_bad_url_remnants();
                emit(CssTokenKind::BadUrl, std::move(sb));
                return true;
            }
            break;
        }
        if (c == '"' || c == '\'' || c == '(') {
            if (strict_) return fail("Invalid url() body");
            consume_bad_url_remnants();
            emit(CssTokenKind::BadUrl, std::move(sb));
            return true;
        }
        if (c == '\\') {
            advance();
            if (at_end()) {
                if (strict_) return fail("Bad escape in url()");
                emit(CssTokenKind::BadUrl, std::move(sb));
                return true;
            }
            sb.push_back(peek());
            advance();
            continue;
        }
        sb.push_back(c);
        advance();
    }

    if (at_end() || peek() != ')') {
        if (strict_) return fail("Unterminated url()");
        // §4.3.6: EOF in url() is a parse error but still yields a <url-token>.
        emit(CssTokenKind::Url, std::move(sb));
        return true;
    }
    advance();
    emit(CssTokenKind::Url, std::move(sb));
    return true;
}

// §4.3.14: after a url() goes bad, consume through the closing ')' (honouring
// escapes) so tokenizing resumes at a sane boundary.
void CssTokenizer::consume_bad_url_remnants() {
    while (!at_end()) {
        char c = peek();
        if (c == ')') { advance(); return; }
        if (c == '\\') {
            advance();
            if (!at_end()) advance();
            continue;
        }
        advance();
    }
}

std::string CssTokenizer::read_ident() {
    std::string sb;
    if (!at_end() && peek() == '-') {
        sb.push_back('-');
        advance();
        if (!at_end() && peek() == '-') { sb.push_back('-'); advance(); }
    }
    while (!at_end() && is_name_char(peek())) { sb.push_back(peek()); advance(); }
    return sb;
}

bool CssTokenizer::is_number_start() const {
    char c = peek();
    if (is_digit(c)) return true;
    if (c == '.' && is_digit(peek_at(1))) return true;
    if (c == '+' || c == '-') {
        if (is_digit(peek_at(1))) return true;
        if (peek_at(1) == '.' && is_digit(peek_at(2))) return true;
    }
    return false;
}

} // namespace weva
