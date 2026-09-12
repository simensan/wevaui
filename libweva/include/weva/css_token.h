#pragma once
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Parsing/{CssTokenKind,CssToken,CssTokenizer}.cs.
//
// Encoding note, and it is a lucky one. CSS Syntax L3 §4.2 makes every
// non-ASCII code point a name-start code point, and the C# implements that as
// `c >= 0x80`. Every UTF-8 continuation byte is also >= 0x80, so processing
// bytes rather than code points yields *identical* identifiers here — unlike
// the HTML tokenizer, which needed real code-point decoding. No decoding in
// this file is a correctness decision, not an oversight.
//
// CSS whitespace is ASCII-only (space, tab, LF, CR, FF) — deliberately
// NARROWER than the HTML tokenizer's char.IsWhiteSpace set. Do not unify them.

namespace weva {

enum class CssTokenKind {
    Whitespace, Ident, Function, AtKeyword, Hash, String,
    Number, Percentage, Dimension, Url,
    // CSS Syntax §4.3.5/§4.3.6 lenient-recovery tokens. A string with an
    // unescaped newline, or a malformed url(), used to throw even in lenient
    // mode — the exception escaped to WevaDocument, which nulled its state, so
    // one bad token anywhere (including inside an @import) blanked the whole
    // document. These let the containing declaration fail alone.
    BadString, BadUrl,
    Delim, Comma, Colon, Semicolon,
    LBrace, RBrace, LParen, RParen, LBracket, RBracket,
    Eof,
};

struct CssToken {
    CssTokenKind kind = CssTokenKind::Eof;
    std::string text;
    double number = 0;
    std::string unit;
    int line = 0;
    int column = 0;
};

struct CssParseError {
    std::string message;
    int line = 0;
    int column = 0;
};

class CssTokenizer {
public:
    // `source` must outlive the tokenizer (the document owns the buffer).
    // strict mirrors C#'s throwOnError: nothing throws, but in strict mode a
    // malformed construct fails the tokenize instead of emitting a recovery
    // token.
    CssTokenizer(std::string_view source, bool strict = true);

    bool tokenize(std::vector<CssToken>* out, CssParseError* error);

private:
    bool skip_comment();
    bool consume_string(char quote);
    void consume_numeric();
    double read_number(std::string* raw);
    void consume_ident_like();
    bool consume_url(const std::string& fn_name);
    void consume_bad_url_remnants();
    std::string read_ident();
    bool is_number_start() const;

    bool at_end() const { return pos_ >= src_.size(); }
    char peek() const { return pos_ < src_.size() ? src_[pos_] : '\0'; }
    char peek_at(std::size_t off) const {
        return pos_ + off < src_.size() ? src_[pos_ + off] : '\0';
    }
    void advance();
    void mark_token_start() { token_line_ = line_; token_column_ = column_; }
    void emit(CssTokenKind kind, std::string text);
    bool fail(std::string_view message);

    std::string_view src_;
    bool strict_;
    std::size_t pos_ = 0;
    int line_ = 1, column_ = 1;
    int token_line_ = 1, token_column_ = 1;
    std::vector<CssToken>* out_ = nullptr;
    CssParseError* error_ = nullptr;
};

// Mirrors C#'s double.TryParse(raw, NumberStyles.Float, InvariantCulture):
// returns 0 and false when the text is not a number. Exposed for tests because
// its edge cases are the ones most likely to drift between the two engines.
bool css_parse_double(std::string_view text, double* out);

} // namespace weva
