#include "check.h"
#include "weva/css_token.h"
#include <cmath>
#include <string>

using namespace weva;

namespace {

struct T {
    std::vector<CssToken> toks;
    CssParseError err;
    bool run(std::string_view src, bool strict = true) {
        CssTokenizer t(src, strict);
        err = CssParseError{};
        return t.tokenize(&toks, &err);
    }
    CssTokenKind kind(std::size_t i) { return toks[i].kind; }
    const std::string& text(std::size_t i) { return toks[i].text; }
};

bool near(double a, double b) { return std::fabs(a - b) < 1e-12; }

} // namespace

void test_css_tokenizer() {
    // ---- number parsing is where C# and C++ most easily drift.
    //
    // std::from_chars refuses a leading '+' (it only recognises one in an
    // exponent) where C#'s NumberStyles.Float accepts it. Unhandled, every
    // `margin: +5px` would silently become 0.
    {
        double v = 0;
        CHECK(css_parse_double("+5", &v) && near(v, 5));
        CHECK(css_parse_double("-5", &v) && near(v, -5));
        CHECK(css_parse_double("+.5", &v) && near(v, 0.5));
        CHECK(css_parse_double("1e3", &v) && near(v, 1000));
        CHECK(css_parse_double("1E-2", &v) && near(v, 0.01));
        CHECK(css_parse_double("5.", &v) && near(v, 5));
        // Failures must yield 0, matching TryParse's out-param default.
        CHECK(!css_parse_double(".", &v) && near(v, 0));
        CHECK(!css_parse_double("-", &v) && near(v, 0));
        CHECK(!css_parse_double("", &v) && near(v, 0));
        CHECK(!css_parse_double("+", &v) && near(v, 0));
        // Locale independence: from_chars is immune to LC_NUMERIC, strtod is not.
        CHECK(css_parse_double("1.5", &v) && near(v, 1.5));
    }

    // ---- dimensions, percentages, signed values
    {
        T t;
        CHECK(t.run("10px -3.5em +2% 0 1e3s"));
        CHECK(t.kind(0) == CssTokenKind::Dimension && near(t.toks[0].number, 10) && t.toks[0].unit == "px");
        CHECK(t.kind(2) == CssTokenKind::Dimension && near(t.toks[2].number, -3.5) && t.toks[2].unit == "em");
        CHECK(t.kind(4) == CssTokenKind::Percentage && near(t.toks[4].number, 2) && t.toks[4].unit == "%");
        CHECK(t.kind(6) == CssTokenKind::Number && near(t.toks[6].number, 0));
        CHECK(t.kind(8) == CssTokenKind::Dimension && near(t.toks[8].number, 1000) && t.toks[8].unit == "s");
    }

    // ---- "1em" must not be read as an exponent: the rewind path
    {
        T t;
        CHECK(t.run("1em 1e5 1e+5 1ex"));
        CHECK(t.kind(0) == CssTokenKind::Dimension && t.toks[0].unit == "em" && near(t.toks[0].number, 1));
        CHECK(t.kind(2) == CssTokenKind::Number && near(t.toks[2].number, 100000));
        CHECK(t.kind(4) == CssTokenKind::Number && near(t.toks[4].number, 100000));
        CHECK(t.kind(6) == CssTokenKind::Dimension && t.toks[6].unit == "ex");
    }

    // ---- non-ASCII identifiers survive as single tokens (the >= 0x80 rule).
    // Byte-wise scanning is equivalent here because continuation bytes are
    // also >= 0x80 — that is why this file needs no UTF-8 decoding.
    {
        T t;
        CHECK(t.run(".café{} --日本語:1"));
        CHECK(t.kind(0) == CssTokenKind::Delim && t.text(0) == ".");
        CHECK(t.kind(1) == CssTokenKind::Ident && t.text(1) == "café");
        CHECK(t.kind(5) == CssTokenKind::Ident && t.text(5) == "--日本語");
    }

    // ---- whitespace runs collapse to a single token
    {
        T t;
        CHECK(t.run("a  \t\n  b"));
        CHECK(t.kind(1) == CssTokenKind::Whitespace && t.text(1) == " ");
        CHECK(t.kind(2) == CssTokenKind::Ident && t.text(2) == "b");
    }

    // ---- comments, CDO/CDC.
    //
    // Note `c-->d` yields Ident("c--") + Delim(">") + Ident("d"), not a CDC.
    // `-` is a name code point, so read_ident absorbs both dashes; CDC is only
    // recognised at a token boundary. Matches the C# (IsNameChar('-') is true).
    {
        T t;
        CHECK(t.run("a/* note */b<!--c-->d"));
        CHECK(t.kind(0) == CssTokenKind::Ident && t.text(0) == "a");
        CHECK(t.kind(1) == CssTokenKind::Ident && t.text(1) == "b");
        CHECK(t.kind(2) == CssTokenKind::Ident && t.text(2) == "c--");
        CHECK(t.kind(3) == CssTokenKind::Delim && t.text(3) == ">");
        CHECK(t.kind(4) == CssTokenKind::Ident && t.text(4) == "d");
    }

    // ---- a CDC at a real token boundary IS skipped.
    //
    // It emits nothing, so the whitespace either side survives as TWO adjacent
    // Whitespace tokens rather than one collapsed run. Downstream consumers
    // must tolerate consecutive whitespace; asserted here so that stays true.
    {
        T t;
        CHECK(t.run("a --> b"));
        CHECK(t.kind(0) == CssTokenKind::Ident && t.text(0) == "a");
        CHECK(t.kind(1) == CssTokenKind::Whitespace);
        CHECK(t.kind(2) == CssTokenKind::Whitespace);
        CHECK(t.kind(3) == CssTokenKind::Ident && t.text(3) == "b");
    }

    // ---- hash, at-keyword, and their Delim fallbacks
    {
        T t;
        CHECK(t.run("#fff @media # @"));
        CHECK(t.kind(0) == CssTokenKind::Hash && t.text(0) == "fff");
        CHECK(t.kind(2) == CssTokenKind::AtKeyword && t.text(2) == "media");
        CHECK(t.kind(4) == CssTokenKind::Delim && t.text(4) == "#");
        CHECK(t.kind(6) == CssTokenKind::Delim && t.text(6) == "@");
    }

    // ---- strings and escapes
    {
        T t;
        CHECK(t.run("\"a\\41 b\" 'x\\ny'"));
        CHECK(t.kind(0) == CssTokenKind::String && t.text(0) == "aAb");
        CHECK(t.kind(2) == CssTokenKind::String && t.text(2) == "x\ny");
    }

    // ---- invalid escapes become U+FFFD rather than aborting the sheet.
    // §4.3.7: zero, surrogates and >U+10FFFF are all invalid. The single
    // whitespace after each escape's hex digits is the escape terminator and is
    // consumed, so no space survives into the string.
    {
        T t;
        CHECK(t.run("\"\\D800 \\0 \\110000 \""));
        CHECK(t.kind(0) == CssTokenKind::String);
        CHECK(t.text(0) == "\xEF\xBF\xBD\xEF\xBF\xBD\xEF\xBF\xBD");
    }

    // ---- but a SECOND space after an escape is literal content
    {
        T t;
        CHECK(t.run("\"\\41  b\""));
        CHECK(t.text(0) == "A b");
    }

    // ---- url(): bare, quoted (-> Function), and recovery
    {
        T t;
        CHECK(t.run("url(a/b.png)"));
        CHECK(t.kind(0) == CssTokenKind::Url && t.text(0) == "a/b.png");

        T t2;
        CHECK(t2.run("url(\"a.png\")"));
        CHECK(t2.kind(0) == CssTokenKind::Function && t2.text(0) == "url");

        T t3;
        CHECK(t3.run("URL(a.png)"));
        CHECK(t3.kind(0) == CssTokenKind::Url);   // case-insensitive
    }

    // ---- lenient recovery: one bad token must not kill the sheet
    {
        T t;
        CHECK(t.run("a{b:\"unterminated\nc:1}", /*strict=*/false));
        bool saw_bad = false, saw_after = false;
        for (auto& k : t.toks) {
            if (k.kind == CssTokenKind::BadString) saw_bad = true;
            if (saw_bad && k.kind == CssTokenKind::Ident && k.text == "c") saw_after = true;
        }
        CHECK(saw_bad);
        CHECK(saw_after);   // tokenizing resumed past the bad string

        T t2;
        CHECK(t2.run("url(a b)", /*strict=*/false));
        CHECK(t2.kind(0) == CssTokenKind::BadUrl);
    }

    // ---- strict mode reports instead of throwing
    {
        T t;
        CHECK(!t.run("\"unterminated\n"));
        CHECK(t.err.message == "Unterminated string");

        T t2;
        CHECK(!t2.run("/* open"));
        CHECK(t2.err.message == "Unterminated comment");

        T t3;
        CHECK(!t3.run("url(a b)"));
        CHECK(t3.err.message.find("url()") != std::string::npos);
    }

    // ---- a realistic declaration block tokenizes to the expected shape
    {
        T t;
        CHECK(t.run(".a > .b:hover { color: #fff; margin: 0 auto; }"));
        CHECK(t.toks.back().kind == CssTokenKind::Eof);
        int braces = 0, colons = 0;
        for (auto& k : t.toks) {
            if (k.kind == CssTokenKind::LBrace || k.kind == CssTokenKind::RBrace) braces++;
            if (k.kind == CssTokenKind::Colon) colons++;
        }
        CHECK(braces == 2);
        CHECK(colons == 3);   // :hover, color:, margin:
    }
}
