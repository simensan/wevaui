#pragma once
#include "weva/intern.h"
#include "weva/status.h"

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Parsing/{HtmlToken,HtmlTokenizer,HtmlElements,HtmlEntities}.cs.
//
// ---------------------------------------------------------------------------
// Encoding: C# tokenizes UTF-16 `char`s; this tokenizes UTF-8 bytes.
//
// That is not a free translation, and the differences are deliberate:
//
//  * Whitespace classification matches C#'s char.IsWhiteSpace exactly, over
//    decoded code points, not just ASCII. It decides where an unquoted
//    attribute value ends, so narrowing it to ASCII would change parse results
//    on real input.
//  * Line/column count code points, where C# counts UTF-16 units. An astral
//    character therefore reports one column less here. Columns only appear in
//    diagnostics, never in a layout dump, so the oracle is unaffected.
//  * A numeric entity naming a surrogate (&#xD800;) is rejected and left as
//    literal text. C# calls char.ConvertFromUtf32, which *throws* on
//    surrogates — an unhandled ArgumentOutOfRangeException escaping the
//    tokenizer. Treating it as an unresolvable entity is the browser
//    behaviour and almost certainly what the C# code meant.
// ---------------------------------------------------------------------------
//
// Tag and attribute names are interned rather than stored as strings (C# holds
// `string Name`). Selector matching and the void/optional-close tables compare
// them constantly; integer compares are the point of having a symbol table.

namespace weva {

enum class HtmlTokenKind { StartTag, EndTag, Text, Comment, DocType, Eof };

struct HtmlAttribute {
    Symbol name = kInvalidSymbol;   // ASCII-lowercased
    std::string value;
};

struct HtmlToken {
    HtmlTokenKind kind = HtmlTokenKind::Eof;
    Symbol name = kInvalidSymbol;   // ASCII-lowercased; start/end tags only
    std::string text;               // text and comment payloads
    std::vector<HtmlAttribute> attributes;
    bool self_closing = false;
    int line = 0;
    int column = 0;
};

struct HtmlParseError {
    std::string message;
    int line = 0;
    int column = 0;
};

class HtmlTokenizer {
public:
    // `source` must outlive the tokenizer — it is the document buffer
    // (docs/CONVENTIONS.md: one owner for parser slices).
    HtmlTokenizer(std::string_view source, SymbolTable* symbols);

    // Returns false and fills `error` on malformed input, mirroring C#'s
    // HtmlParseException rather than throwing (CONVENTIONS.md: no exceptions).
    bool tokenize(std::vector<HtmlToken>* out, HtmlParseError* error);

private:
    bool consume_text();
    void flush_text(std::vector<HtmlToken>* out);
    bool consume_tag(std::vector<HtmlToken>* out);
    bool consume_start_tag(std::vector<HtmlToken>* out);
    bool consume_end_tag(std::vector<HtmlToken>* out);
    bool read_attribute(HtmlAttribute* out);
    bool read_attribute_value(std::string* out);
    bool consume_markup_declaration(std::vector<HtmlToken>* out);
    bool consume_comment(std::vector<HtmlToken>* out);
    bool consume_doctype(std::vector<HtmlToken>* out);
    Symbol read_tag_name();
    bool try_consume_entity(std::string* out);

    bool at_end() const { return pos_ >= src_.size(); }
    char peek() const { return pos_ < src_.size() ? src_[pos_] : '\0'; }
    void advance();
    void skip_whitespace();
    bool starts_with(std::string_view s) const;
    bool starts_with_ignore_case(std::string_view s) const;
    void mark_token_start() { token_line_ = line_; token_column_ = column_; }
    bool fail(std::string_view message);

    // Decodes the code point at pos_ without consuming. Returns its byte
    // length in `len`; invalid sequences decode as the single raw byte.
    uint32_t peek_codepoint(int* len) const;

    std::string_view src_;
    SymbolTable* symbols_;
    std::size_t pos_ = 0;
    int line_ = 1, column_ = 1;
    int token_line_ = 1, token_column_ = 1;
    std::string buf_;
    HtmlParseError* error_ = nullptr;
};

// Matches System.Char.IsWhiteSpace over the BMP.
bool is_unicode_whitespace(uint32_t cp);
// Appends `cp` to `out` as UTF-8. False for surrogates and out-of-range.
bool append_utf8(uint32_t cp, std::string* out);

namespace html_elements {
bool is_void(std::string_view tag);
bool is_optional_close(std::string_view tag);
bool should_implicitly_close(std::string_view current_open, std::string_view new_start);
} // namespace html_elements

namespace html_entities {
// Resolves a named entity ("amp") to its replacement text.
bool lookup(std::string_view name, std::string_view* out);
} // namespace html_entities

} // namespace weva
