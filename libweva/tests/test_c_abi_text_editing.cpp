#include "check.h"
#include "weva_c.h"
#include "weva/grapheme.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>
#include <vector>

namespace {
struct Field {
    weva_document_t doc;
    weva_element_t field;
    explicit Field(const char* html = "<input id=f><button id=other>Other</button>") {
        weva_config config{};
        config.viewport_width = 400;
        config.viewport_height = 300;
        config.use_user_agent_stylesheet = 1;
        doc = weva_document_create(&config);
        const char* css = "html,body{margin:0}input,textarea{display:block;box-sizing:border-box;"
                          "width:100px;height:30px;padding:0;border:0;font-size:20px}";
        weva_document_add_css(doc, css, std::strlen(css));
        weva_document_load_html(doc, html, std::strlen(html));
        weva_document_update(doc, 0);
        field = weva_document_query(doc, "#f");
        weva_document_set_focus(doc, field);
    }
    ~Field() { weva_document_destroy(doc); }
    void value(const char* text) {
        weva_element_set_value(doc, field, text);
        select(static_cast<int>(std::strlen(text)));
    }
    std::string value() {
        char buffer[4096]{};
        weva_element_value(doc, field, buffer, sizeof(buffer));
        return buffer;
    }
    void select(int offset) { weva_element_set_selection(doc, field, offset, offset); }
    int selection() {
        int start = -1, end = -1;
        weva_element_selection(doc, field, &start, &end);
        CHECK(start == end);
        return end;
    }
    void key(int code, uint32_t modifiers = 0) {
        CHECK(weva_document_key(doc, code, modifiers, 1) == 1);
        weva_document_key(doc, code, modifiers, 0);
    }
    double caret_x() {
        weva_document_update(doc, 0);
        double x = 0, y = 0, width = 0, height = 0;
        CHECK(weva_document_caret_bounds(doc, &x, &y, &width, &height) == 1);
        CHECK(width > 0 && height > 0);
        return x;
    }
    void click(double x) {
        weva_document_set_pointer(doc, x, 15, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(doc, x, 15, 0);
    }
};
bool near(double a, double b) { return std::abs(a - b) < 0.01; }
void same_paint(Field& actual, Field& expected) {
    weva_document_update(actual.doc, 0);
    weva_document_update(expected.doc, 0);
    size_t an = 0, en = 0;
    const auto* a = weva_document_draws(actual.doc, &an);
    const auto* e = weva_document_draws(expected.doc, &en);
    CHECK(an == en);
    for (size_t i = 0; i < std::min(an, en); ++i) {
        CHECK(a[i].vertex_count == e[i].vertex_count);
        for (size_t j = 0; j < std::min(a[i].vertex_count, e[i].vertex_count); ++j) {
            CHECK(near(a[i].vertices[j].x, e[i].vertices[j].x));
            CHECK(near(a[i].vertices[j].y, e[i].vertices[j].y));
        }
    }
}
void same_selection(Field& actual, Field& expected) {
    // Tabs split glyph runs. Compare the actual selection geometry independently
    // of glyph batching and the order of non-overlapping glyph runs.
    const auto band = [](Field& field) {
        weva_document_update(field.doc, 0);
        size_t count = 0;
        const auto* draws = weva_document_draws(field.doc, &count);
        std::vector<weva_vertex> result;
        for (size_t i = 0; i < count; ++i)
            for (size_t j = 0; j < draws[i].vertex_count; ++j)
                if (near(draws[i].vertices[j].a, .45)) result.push_back(draws[i].vertices[j]);
        return result;
    };
    const auto a = band(actual), e = band(expected);
    CHECK(!a.empty());
    CHECK(a.size() == e.size());
    for (size_t i = 0; i < std::min(a.size(), e.size()); ++i) {
        CHECK(near(a[i].x, e[i].x)); CHECK(near(a[i].y, e[i].y));
    }
}
}

void test_abi_text_editing_boundaries() {
    {
        Field d("<textarea id=f></textarea>");
        weva_element_set_attribute(d.doc, d.field, "style", "width:40px;height:200px;word-break:break-all");
        d.value("abcdefgh\nijkl\nmnop"); d.select(1);
        d.key(WEVA_KEY_DOWN, WEVA_MOD_CTRL); CHECK(d.selection() == 10);
        d.key(WEVA_KEY_UP, WEVA_MOD_CTRL); CHECK(d.selection() == 5);
        d.key(WEVA_KEY_UP, WEVA_MOD_CTRL); CHECK(d.selection() == 0);
        d.select(5);
        d.key(WEVA_KEY_DOWN, WEVA_MOD_CTRL | WEVA_MOD_SHIFT);
        int anchor = -1, end = -1;
        weva_element_selection(d.doc, d.field, &anchor, &end);
        CHECK(anchor == 5 && end == 10);
        d.value("abc\n\ndef"); d.select(2);
        d.key(WEVA_KEY_DOWN, WEVA_MOD_CTRL); CHECK(d.selection() == 4);
        d.key(WEVA_KEY_DOWN, WEVA_MOD_CTRL); CHECK(d.selection() == 7);
        d.key(WEVA_KEY_UP, WEVA_MOD_CTRL); CHECK(d.selection() == 4);
        d.key(WEVA_KEY_UP, WEVA_MOD_CTRL); CHECK(d.selection() == 2);
    }
    for (const char* decoration : {"underline", "overline", "line-through"}) {
        Field actual("<textarea id=f></textarea>"), expected("<textarea id=f></textarea>");
        const std::string style = std::string("white-space:pre;tab-size:4;text-decoration:") + decoration;
        weva_element_set_attribute(actual.doc, actual.field, "style", style.c_str());
        weva_element_set_attribute(expected.doc, expected.field, "style", style.c_str());
        actual.value("\t"); expected.value("    ");
        same_paint(actual, expected);
    }
    for (const char* whitespace : {"pre", "pre-wrap"}) {
        Field d("<textarea id=f></textarea>");
        const std::string style = std::string("height:200px;width:300px;tab-size:25px;white-space:") + whitespace;
        weva_element_set_attribute(d.doc, d.field, "style", style.c_str());
        d.value("\t"); CHECK(near(d.caret_x(), 25));
        d.value("\t\t"); CHECK(near(d.caret_x(), 50));
        d.click(30); CHECK(d.selection() == 1);
        d.click(45); CHECK(d.selection() == 2);
        d.value("a\t\na\t"); d.select(2);
        CHECK(near(d.caret_x(), 25));
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 5);
        CHECK(near(d.caret_x(), 25));
        d.key(WEVA_KEY_UP); CHECK(d.selection() == 2);
    }
    for (const char* whitespace : {"pre", "pre-wrap"}) {
        Field d("<textarea id=f></textarea>");
        const std::string style = std::string("height:200px;tab-size:0;white-space:") + whitespace;
        weva_element_set_attribute(d.doc, d.field, "style", style.c_str());
        d.value("a"); const double letter = d.caret_x();
        d.value("a\t\t"); CHECK(near(d.caret_x(), letter));
        d.key(WEVA_KEY_LEFT); CHECK(d.selection() == 2); CHECK(near(d.caret_x(), letter));
        d.value("\t"); CHECK(near(d.caret_x(), 0));
        CHECK_EQ(d.value(), "\t");
    }
    for (const char* whitespace : {"pre", "pre-wrap"}) {
        Field d("<textarea id=f></textarea>");
        const std::string style = std::string("height:200px;width:300px;tab-size:4;white-space:") + whitespace;
        weva_element_set_attribute(d.doc, d.field, "style", style.c_str());
        for (const char* prefix : {"a\t", "\t\t", "a\t \t", u8"é\t"}) {
            const int end = int(std::strlen(prefix));
            d.value((std::string(prefix) + "b").c_str()); d.select(end);
            const double with_suffix = d.caret_x();
            d.value(prefix);
            CHECK(near(d.caret_x(), with_suffix)); // trailing tabs retain their full advance
        }
        d.value("a\tb"); d.select(1); const double left = d.caret_x();
        d.select(2); const double right = d.caret_x();
        d.click(left + (right - left) * .2); CHECK(d.selection() == 1);
        d.click(left + (right - left) * .8); CHECK(d.selection() == 2);
        d.value("a\t\na\t"); d.select(2);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 5);
        d.key(WEVA_KEY_UP); CHECK(d.selection() == 2);
        d.key(WEVA_KEY_HOME); CHECK(d.selection() == 0);
        d.key(WEVA_KEY_END); CHECK(d.selection() == 2);
        // Selection of the tab paints the same band as the equivalent spaces.
        Field literal("<textarea id=f></textarea>");
        weva_element_set_attribute(literal.doc, literal.field, "style", style.c_str());
        d.value("a\t"); literal.value("a   ");
        weva_element_set_selection(d.doc, d.field, 1, 2);
        weva_element_set_selection(literal.doc, literal.field, 1, 4);
        same_selection(d, literal);
        // Recycled text boxes must not retain an earlier expansion map.
        d.value("xy"); literal.value("xy");
        same_paint(d, literal);
    }
    {
        Field wrapped("<textarea id=f></textarea>"), preserved("<textarea id=f></textarea>");
        weva_element_set_attribute(wrapped.doc, wrapped.field, "style", "white-space:pre-wrap;text-align:right;height:200px");
        weva_element_set_attribute(preserved.doc, preserved.field, "style", "white-space:pre;text-align:right;height:200px");
        for (const char* text : {"a   ", "a   \nb"}) {
            wrapped.value(text); preserved.value(text);
            wrapped.select(1); preserved.select(1);
            CHECK(near(wrapped.caret_x(), preserved.caret_x()));
            wrapped.select(4); preserved.select(4);
            CHECK(near(wrapped.caret_x(), preserved.caret_x()));
        }
    }
    for (const auto& example : {std::pair<const char*, const char*>{"uppercase", "AB CD\nEF GH"},
                               {"lowercase", "ab cd\nef gh"}, {"capitalize", "Ab Cd\nEf Gh"}}) {
        Field actual("<textarea id=f></textarea>"), expected("<textarea id=f></textarea>");
        actual.value("ab cd\nef gh"); expected.value(example.second);
        const std::string transform = std::string("text-transform:") + example.first + ";height:200px;width:";
        for (const char* width : {"100px", "40px", "100px"}) {
            weva_element_set_attribute(actual.doc, actual.field, "style", (transform + width).c_str());
            weva_element_set_attribute(expected.doc, expected.field, "style", (std::string("height:200px;width:") + width).c_str());
            actual.select(1); expected.select(1);
            CHECK(near(actual.caret_x(), expected.caret_x()));
            actual.key(WEVA_KEY_DOWN); expected.key(WEVA_KEY_DOWN);
            CHECK(actual.selection() == expected.selection());
            weva_element_set_selection(actual.doc, actual.field, 1, 8);
            weva_element_set_selection(expected.doc, expected.field, 1, 8);
            same_paint(actual, expected); // selection bands use displayed advances
        }
        actual.select(8); expected.select(8);
        CHECK(near(actual.caret_x(), expected.caret_x()));
        double x = 0, y = 0, w = 0, h = 0;
        CHECK(weva_document_caret_bounds(actual.doc, &x, &y, &w, &h) == 1);
        weva_document_set_pointer(actual.doc, x + .1, y + h / 2, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(actual.doc, x + .1, y + h / 2, 0);
        CHECK(actual.selection() == 8);
        CHECK_EQ(actual.value(), "ab cd\nef gh");
        weva_element_set_selection(actual.doc, actual.field, 1, 8);
        weva_element_set_selection(expected.doc, expected.field, 1, 8);
        CHECK(weva_document_set_composition(actual.doc, "jk", 0, 1) == 1);
        CHECK(weva_document_set_composition(expected.doc,
            std::strcmp(example.first, "uppercase") == 0 ? "JK" : "jk", 0, 1) == 1);
        same_paint(actual, expected); // mapped preedit underline and selection
    }
    {
        Field d("<textarea id=f></textarea>");
        weva_element_set_attribute(d.doc, d.field, "style", "width:32px;height:200px");
        d.value("abcdefghi");
        for (const char* wrap : {"soft", "off", "OFF", "hard", "unknown"}) {
            weva_element_set_attribute(d.doc, d.field, "wrap", wrap);
            d.select(1);
            d.key(WEVA_KEY_DOWN);
            CHECK(d.selection() == ((std::strcmp(wrap, "off") == 0 || std::strcmp(wrap, "OFF") == 0) ? 9 : 4));
        }
        weva_element_set_attribute(d.doc, d.field, "wrap", "off");
        weva_element_set_attribute(d.doc, d.field, "style", "width:32px;height:200px;white-space:pre-wrap;overflow-wrap:break-word");
        d.select(1); d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 4);
    }
    {
        Field d("<textarea id=f></textarea>");
        for (const char* text : {"ab cd ef", "ab\n\ncd"}) {
            d.value(text);
            const int expected = text[2] == '\n' ? 3 : 7;
            d.select(expected);
            weva_document_update(d.doc, 0);
            double x = 0, y = 0, w = 0, h = 0;
            CHECK(weva_document_caret_bounds(d.doc, &x, &y, &w, &h) == 1);
            weva_document_set_pointer(d.doc, x + 0.1, y + h * 0.5, WEVA_BUTTON_PRIMARY);
            weva_document_set_pointer(d.doc, x + 0.1, y + h * 0.5, 0);
            CHECK(d.selection() == expected);
        }
    }
    {
        Field d("<textarea id=f></textarea>");
        d.value("abcdef\nx\nabcdef"); d.select(4);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 8);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 13);
        d.key(WEVA_KEY_UP); CHECK(d.selection() == 8);
        d.key(WEVA_KEY_UP); CHECK(d.selection() == 4);
        d.key(WEVA_KEY_DOWN);
        d.key(WEVA_KEY_LEFT);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 9); // horizontal movement resets x
        d.select(4);
        d.key(WEVA_KEY_DOWN, WEVA_MOD_SHIFT);
        d.key(WEVA_KEY_DOWN, WEVA_MOD_SHIFT);
        int anchor = -1, end = -1;
        weva_element_selection(d.doc, d.field, &anchor, &end);
        CHECK(anchor == 4 && end == 13);
        d.value("abcd\n\nabcd\n"); d.select(3);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 5);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 9);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 11);
        d.key(WEVA_KEY_UP); CHECK(d.selection() == 9);
        d.key(WEVA_KEY_HOME); CHECK(d.selection() == 6);
        d.key(WEVA_KEY_END); CHECK(d.selection() == 10);
        d.value(u8"abc\n😀x"); d.select(2);
        d.key(WEVA_KEY_DOWN);
        const int at = d.selection();
        CHECK(at == 4 || at == 8 || at == 9); // never inside the four-byte emoji
        CHECK(weva_document_try_text_input(d.doc, "Q") == 1);
        const std::string expected = std::string(u8"abc\n😀x").insert(size_t(at), "Q");
        CHECK_EQ(d.value(), expected);
    }
    {
        Field d("<textarea id=f></textarea>");
        d.value("a");
        const double advance = d.caret_x();
        const std::string style = "word-break:break-all;white-space:pre-wrap;width:" +
            std::to_string(advance * 3.2) + "px";
        weva_element_set_attribute(d.doc, d.field, "style", style.c_str());
        d.value("abcdefghi"); d.select(1);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 4);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 7);
        d.key(WEVA_KEY_UP); CHECK(d.selection() == 4);
        d.key(WEVA_KEY_HOME); CHECK(d.selection() == 3);
        d.key(WEVA_KEY_END); CHECK(d.selection() == 6);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 9);
        weva_element_set_attribute(d.doc, d.field, "style", "word-break:break-all;width:1px");
        d.value(u8"ááá"); d.select(0);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 3);
        d.key(WEVA_KEY_DOWN); CHECK(d.selection() == 6);
    }
    for (const char* html : {"<input id=f>", "<textarea id=f></textarea>"}) {
        Field d(html);
        d.value(u8"ab😀cd界ef");
        for (bool backward : {false, true}) {
            for (int key : {WEVA_KEY_LEFT, WEVA_KEY_RIGHT}) {
                weva_element_set_selection(d.doc, d.field, backward ? 8 : 2, backward ? 2 : 8);
                d.key(key);
                CHECK(d.selection() == (key == WEVA_KEY_LEFT ? 2 : 8));
                CHECK_EQ(d.value(), u8"ab😀cd界ef");
            }
        }
    }
    const struct { const char* text; const char* remaining; } cases[] = {
        {u8"a\u0301", "a"}, {u8"a\u0301\u0327", u8"a\u0301"}, {u8"각", u8"가"},
        {u8"😀", ""}, {u8"界", ""}, {u8"👨‍👩‍👧‍👦", ""}, {u8"👩🏽‍💻", ""},
        {u8"🇸🇪", ""}, {u8"🇸🇪🇫", u8"🇸🇪"}, {u8"🇸🇪🇫🇷", u8"🇸🇪"},
        {u8"👍🏽", ""}, {u8"a🏽", "a"}, {u8"1️⃣", ""}, {u8"#⃣", ""}, {u8"a⃣", "a"},
        {u8"❤️", ""}, {u8"a\uFE0F", ""}, {u8"a\u0301\uFE0F", u8"a\u0301"},
        {u8"a\uFE0F\uFE0F", u8"a\uFE0F"}, {u8"a‍😀", u8"a‍"},
        {u8"🏴\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F", ""},
        {u8"क्‍ष", u8"क्‍"},
    };
    for (const char* html : {"<input id=f>", "<textarea id=f></textarea>"}) {
        Field d(html);
        for (const auto& c : cases) {
            d.value(c.text);
            d.key(WEVA_KEY_BACKSPACE);
            CHECK_EQ(d.value(), c.remaining);
            CHECK(d.selection() == static_cast<int>(std::strlen(c.remaining)));
            CHECK(weva_document_undo(d.doc) == 1);
            CHECK_EQ(d.value(), c.text);
            CHECK(weva_document_redo(d.doc) == 1);
            CHECK_EQ(d.value(), c.remaining);
        }
        for (const char* text : {u8"a\u0301", u8"각", u8"👨‍👩‍👧‍👦", u8"👩🏽‍💻", u8"🇸🇪", u8"1️⃣"}) {
            d.value(text);
            d.key(WEVA_KEY_LEFT); CHECK(d.selection() == 0);
            d.key(WEVA_KEY_RIGHT); CHECK(d.selection() == static_cast<int>(std::strlen(text)));
            d.key(WEVA_KEY_HOME); d.key(WEVA_KEY_DELETE); CHECK_EQ(d.value(), "");
        }
        d.value(u8"क्‍ष");
        d.key(WEVA_KEY_LEFT); CHECK(d.selection() == 9);
        d.key(WEVA_KEY_HOME); d.key(WEVA_KEY_DELETE); CHECK_EQ(d.value(), u8"ष");
        d.value(u8"a😀界");
        weva_element_set_selection(d.doc, d.field, 2, 6);
        int from = -1, to = -1;
        weva_element_selection(d.doc, d.field, &from, &to);
        CHECK(from == 1 && to == 5); // Programmatic byte offsets cannot split UTF-8.
        d.key(WEVA_KEY_BACKSPACE); CHECK_EQ(d.value(), u8"a界");
    }
    // Backspace scans long emoji chains iteratively, without stack growth.
    std::string sequence = u8"👩🏽";
    for (int i = 0; i < 20000; ++i) sequence += u8"‍👩🏽";
    CHECK(weva::backward_delete_boundary(sequence, sequence.size()) == 0);
    CHECK(weva::backward_delete_boundary("\r\n", 2) == 0);
    CHECK(weva::backward_delete_boundary("", 20) == 0);
    CHECK(weva::backward_delete_boundary("\xFF", 1) == 0);
}

void test_abi_input_text_scroll() {
    Field d;
    d.value("abcdefghijklmnopqrstuvwxyz");
    CHECK(near(d.caret_x(), 99));
    d.key(WEVA_KEY_HOME); CHECK(near(d.caret_x(), 0));
    d.key(WEVA_KEY_END); CHECK(near(d.caret_x(), 99));
    d.key(WEVA_KEY_LEFT); CHECK(d.caret_x() > 80 && d.caret_x() < 99);
    d.key(WEVA_KEY_END); d.caret_x();
    d.click(98); CHECK(d.selection() == 26); // Hit testing includes the retained scroll offset.
    d.click(1); CHECK(d.selection() > 0 && d.selection() < 26);
    CHECK(d.caret_x() >= 0 && d.caret_x() <= 99);
    d.key(WEVA_KEY_END);
    weva_element_set_attribute(d.doc, d.field, "style", "width:60px");
    CHECK(near(d.caret_x(), 59));
    weva_element_set_attribute(d.doc, d.field, "style", "width:400px");
    const double whole = d.caret_x();
    CHECK(whole > 100 && whole < 400);
    weva_element_set_attribute(d.doc, d.field, "style", "width:100px");
    CHECK(near(d.caret_x(), 99));
    d.value("xy"); CHECK(d.caret_x() > 0 && d.caret_x() < 50);
    d.value("abcdefghijklmnopqrstuvwxyz"); d.caret_x();
    weva_document_set_focus(d.doc, weva_document_query(d.doc, "#other"));
    weva_document_update(d.doc, 0);
    d.click(1); CHECK(d.selection() == 0); // Blur resets the field's text scroll.
    CHECK(near(d.caret_x(), 0));
    d.value("abcdefghijklmnopqrstuvwxyz"); d.caret_x();
    const char* html = "<input id=f value=xy>";
    weva_document_load_html(d.doc, html, std::strlen(html));
    weva_document_update(d.doc, 0);
    d.field = weva_document_query(d.doc, "#f");
    weva_document_set_focus(d.doc, d.field);
    CHECK(near(d.caret_x(), 0)); // Reload restores the initial selection, not the old end.
}

void test_abi_text_advances_and_password() {
    for (const char* html : {"<input id=f>", "<textarea id=f></textarea>"}) {
        Field transformed(html);
        transformed.value("abcdef");
        transformed.select(2);
        const double advance = transformed.caret_x();
        weva_element_set_attribute(transformed.doc, transformed.field, "style",
                                  "transform-origin:0 0;transform:translate(120px,40px) scale(2)");
        weva_document_update(transformed.doc, 0);
        const double x = 120 + advance * 2;
        weva_document_set_pointer(transformed.doc, x, 55, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(transformed.doc, x, 55, 0);
        CHECK(transformed.selection() == 2);
    }
    Field d;
    d.value("a"); const double letter = d.caret_x();
    d.value("a "); const double space = d.caret_x() - letter;
    CHECK(space > 0); // Spaces have advance even when they have no bitmap.
    d.value("a   "); CHECK(near(d.caret_x(), letter + 3 * space));
    d.click(letter + 2.8 * space); CHECK(d.selection() == 4);
    weva_element_set_attribute(d.doc, d.field, "type", "password");
    d.value("a"); const double bullet = d.caret_x();
    CHECK(bullet > 0);
    for (const char* text : {u8"😀", u8"界", u8"a\u0301", u8"👨‍👩‍👧‍👦", u8"🇸🇪", u8"각", u8"क्‍ष"}) {
        d.value(text); CHECK(near(d.caret_x(), bullet));
        d.click(bullet * 0.8); CHECK(d.selection() == static_cast<int>(std::strlen(text)));
        d.click(bullet * 0.2); CHECK(d.selection() == 0);
    }
    d.value(u8"😀界a"); CHECK(near(d.caret_x(), 3 * bullet));
    d.click(bullet * 1.8); CHECK(d.selection() == 7);
    d.key(WEVA_KEY_BACKSPACE); CHECK_EQ(d.value(), u8"😀a");
    std::string long_password;
    for (int i = 0; i < 40; ++i) long_password += u8"😀";
    d.value(long_password.c_str()); CHECK(near(d.caret_x(), 99));
    d.click(98); CHECK(d.selection() == 160);
    d.key(WEVA_KEY_HOME); CHECK(near(d.caret_x(), 0));

    d.key(WEVA_KEY_END);
    weva_element_set_attribute(d.doc, d.field, "type", "text");
    d.value("abcdefghijklmnopqrstuvwxyz");
    weva_element_remove(d.doc, weva_document_query(d.doc, "#other"));
    weva_element_set_attribute(d.doc, d.field, "style", "transform:translate(120px,40px)");
    CHECK(near(d.caret_x(), 219));
    size_t count = 0, clipped_text = 0;
    const weva_draw* draws = weva_document_draws(d.doc, &count);
    for (size_t i = 0; i < count; ++i) if (draws[i].texture_id && draws[i].vertex_count) {
        ++clipped_text;
        CHECK(draws[i].has_scissor && draws[i].scissor_x == 120 && draws[i].scissor_y == 40);
        for (size_t j = 0; j < draws[i].vertex_count; ++j) {
            CHECK(draws[i].vertices[j].x >= 119.99 && draws[i].vertices[j].x <= 220.01);
            CHECK(draws[i].vertices[j].y >= 39.99 && draws[i].vertices[j].y <= 70.01);
        }
    }
    CHECK(clipped_text > 0);

    Field area("<textarea id=f></textarea>");
    area.value("a   ");
    const double caret = area.caret_x();
    draws = weva_document_draws(area.doc, &count);
    bool painted_caret = false;
    for (size_t i = 0; i < count; ++i) if (!draws[i].texture_id) {
        for (size_t j = 0; j + 2 < draws[i].index_count; j += 3) {
            double left = 1e20, right = -1e20;
            for (size_t k = 0; k < 3; ++k) {
                const auto& vertex = draws[i].vertices[draws[i].indices[j + k]];
                left = std::min<double>(left, vertex.x);
                right = std::max<double>(right, vertex.x);
            }
            if (near(right - left, 1) && near(left, caret)) painted_caret = true;
        }
    }
    CHECK(painted_caret); // The drawn textarea bar and IME anchor use the same advances.
}
