#include "check.h"
#include "weva/html.h"
#include <string>
#include <vector>

using namespace weva;

namespace {

struct Tok {
    SymbolTable symbols;
    std::vector<HtmlToken> tokens;
    HtmlParseError error;
    bool ok_ = false;

    bool run(std::string_view src) {
        HtmlTokenizer t(src, &symbols);
        ok_ = t.tokenize(&tokens, &error);
        return ok_;
    }
    std::string_view name(std::size_t i) { return symbols.text(tokens[i].name); }
    std::string_view attr_name(std::size_t i, std::size_t a) {
        return symbols.text(tokens[i].attributes[a].name);
    }
};

} // namespace

void test_html() {
    // ---- basic tags, names lowercased, attributes ordered
    {
        Tok t;
        CHECK(t.run("<DIV Class='a' data-X=\"1\">hi</DiV>"));
        CHECK(t.tokens.size() == 4);   // start, text, end, eof
        CHECK(t.tokens[0].kind == HtmlTokenKind::StartTag);
        CHECK(t.name(0) == "div");
        CHECK(t.tokens[0].attributes.size() == 2);
        CHECK(t.attr_name(0, 0) == "class");
        CHECK(t.tokens[0].attributes[0].value == "a");
        CHECK(t.attr_name(0, 1) == "data-x");
        CHECK(t.tokens[0].attributes[1].value == "1");
        CHECK(t.tokens[1].kind == HtmlTokenKind::Text && t.tokens[1].text == "hi");
        CHECK(t.tokens[2].kind == HtmlTokenKind::EndTag && t.name(2) == "div");
        CHECK(t.tokens[3].kind == HtmlTokenKind::Eof);
    }

    // ---- interning: the same tag name yields the same Symbol
    {
        Tok t;
        CHECK(t.run("<p></p><p></p>"));
        CHECK(t.tokens[0].name == t.tokens[2].name);
        CHECK(t.tokens[0].name != kInvalidSymbol);
    }

    // ---- valueless, unquoted, and self-closing
    {
        Tok t;
        CHECK(t.run("<input disabled type=text />"));
        CHECK(t.tokens[0].self_closing);
        CHECK(t.tokens[0].attributes[0].value == "");
        CHECK(t.tokens[0].attributes[1].value == "text");
    }

    // ---- entities in text and in attribute values
    {
        Tok t;
        CHECK(t.run("<a title=\"a&amp;b\">&lt;x&gt;&#65;&#x42;</a>"));
        CHECK(t.tokens[0].attributes[0].value == "a&b");
        CHECK(t.tokens[1].text == "<x>AB");
    }

    // ---- unresolvable entities stay literal and do not consume input
    {
        Tok t;
        CHECK(t.run("A & B &notreal; &#;"));
        CHECK(t.tokens[0].text == "A & B &notreal; &#;");
    }

    // ---- multi-byte entity payloads encode as UTF-8
    {
        Tok t;
        CHECK(t.run("&nbsp;&mdash;&#x1F600;"));
        CHECK(t.tokens[0].text == " —\U0001F600");
    }

    // ---- a surrogate code point is rejected, left as literal text.
    // C# would throw ArgumentOutOfRangeException out of char.ConvertFromUtf32.
    {
        Tok t;
        CHECK(t.run("&#xD800;"));
        CHECK(t.ok_);
        CHECK(t.tokens[0].text == "&#xD800;");
    }

    // ---- UTF-8 passes through text and quoted values untouched
    {
        Tok t;
        CHECK(t.run("<p title=\"café\">naïve 中文</p>"));
        CHECK(t.tokens[0].attributes[0].value == "café");
        CHECK(t.tokens[1].text == "naïve 中文");
    }

    // ---- U+00A0 terminates an unquoted value, matching char.IsWhiteSpace.
    // An ASCII-only classifier would swallow it into the value.
    {
        Tok t;
        CHECK(t.run("<p data-a=x data-b=y>"));
        CHECK(t.tokens[0].attributes.size() == 2);
        CHECK(t.tokens[0].attributes[0].value == "x");
        CHECK(t.attr_name(0, 1) == "data-b");
    }

    // ---- comments and doctype
    {
        Tok t;
        CHECK(t.run("<!DOCTYPE html><!-- note --><b>x</b>"));
        CHECK(t.tokens[0].kind == HtmlTokenKind::DocType);
        CHECK(t.tokens[1].kind == HtmlTokenKind::Comment);
        CHECK(t.tokens[1].text == " note ");
    }

    // ---- line/column tracking
    {
        Tok t;
        CHECK(t.run("<a>\n  <b>x</b>\n</a>"));
        CHECK(t.tokens[2].line == 2 && t.tokens[2].column == 3);   // <b>
    }

    // ---- malformed input returns an error rather than throwing
    {
        Tok t;
        CHECK(!t.run("<div"));
        CHECK(t.error.message == "Unterminated start tag");

        Tok t2;
        CHECK(!t2.run("<!-- unterminated"));
        CHECK(t2.error.message == "Unterminated comment");

        Tok t3;
        CHECK(!t3.run("<p title='unclosed>"));
        CHECK(t3.error.message == "Unterminated attribute value");

        Tok t4;
        CHECK(!t4.run("<1bad>"));
        CHECK(t4.error.line == 1 && t4.error.column == 2);
    }

    // ---- element tables
    CHECK(html_elements::is_void("br"));
    CHECK(!html_elements::is_void("div"));
    CHECK(html_elements::is_optional_close("li"));
    CHECK(!html_elements::is_optional_close("div"));
    CHECK(html_elements::should_implicitly_close("p", "div"));
    CHECK(html_elements::should_implicitly_close("li", "li"));
    CHECK(html_elements::should_implicitly_close("td", "tr"));
    CHECK(!html_elements::should_implicitly_close("div", "p"));
    CHECK(!html_elements::should_implicitly_close("", "p"));

    // ---- whitespace classifier matches char.IsWhiteSpace
    CHECK(is_unicode_whitespace(' ') && is_unicode_whitespace('\t'));
    CHECK(is_unicode_whitespace(0x00A0) && is_unicode_whitespace(0x3000));
    CHECK(is_unicode_whitespace(0x2000) && is_unicode_whitespace(0x200A));
    CHECK(!is_unicode_whitespace(0x200B));   // zero-width space is NOT whitespace
    CHECK(!is_unicode_whitespace('x'));
}
