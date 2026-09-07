#include "check.h"
#include "weva_c.h"
#include "weva/grapheme.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>

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
}

void test_abi_text_editing_boundaries() {
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
    CHECK(d.caret_x() > 0 && d.caret_x() < 50);
}

void test_abi_text_advances_and_password() {
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
