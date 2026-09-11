#include "check.h"
#include "weva_c.h"
#include <cstring>
#include <string>
#include <utility>
#include <vector>

namespace {
struct Field {
    weva_document_t doc;
    weva_element_t field;
    explicit Field(const std::string& markup) {
        weva_config config{};
        config.viewport_width = 400; config.viewport_height = 300;
        config.use_user_agent_stylesheet = 1;
        doc = weva_document_create(&config);
        weva_document_load_html(doc, markup.data(), markup.size());
        weva_document_update(doc, 0);
        field = weva_document_query(doc, "#f");
        CHECK(field != WEVA_ELEMENT_NONE);
        weva_document_set_focus(doc, field);
        events();
    }
    ~Field() { weva_document_destroy(doc); }
    std::string value() const {
        char buffer[4096]{};
        weva_element_value(doc, field, buffer, sizeof(buffer));
        return buffer;
    }
    void value(const char* text, int from, int to) {
        weva_element_set_value(doc, field, text);
        weva_element_set_selection(doc, field, from, to);
        events();
    }
    void selection(int from, int to) {
        int a = -1, b = -1;
        CHECK(weva_element_selection(doc, field, &a, &b) == WEVA_OK);
        CHECK(a == from); CHECK(b == to);
    }
    std::vector<std::pair<int, std::string>> events() const {
        std::vector<std::pair<int, std::string>> out;
        weva_event event{};
        while (weva_document_poll_event(doc, &event)) {
            char text[4096]{};
            weva_document_event_text(doc, text, sizeof(text));
            out.emplace_back(event.kind, text);
        }
        return out;
    }
};
std::string markup(const char* tag, const char* limit) {
    return std::string("<") + tag + " id=f maxlength='" + limit + "'></" + tag + "><button id=other>Other</button>";
}
}

void test_abi_maxlength_insertions() {
    for (const char* tag : {"input", "textarea"}) {
        const struct { const char* limit; const char* before; int from, to; const char* input; const char* after; int caret; } cases[] = {
            {"3", "", 0, 0, "abcdef", "abc", 3},
            {"1", "", 0, 0, u8"😀a", "", 0}, {"2", "", 0, 0, u8"😀a", u8"😀", 4},
            {"1", "", 0, 0, u8"a\u0301", "a", 1},
            {"3", "abc", 3, 3, "x", "abc", 3}, {"3", "abc", 1, 2, "XYZ", "aXc", 2},
            {"3", "abcdef", 2, 4, "z", "abef", 2},
            {"3", "abcdef", 0, 6, "XYZW", "XYZ", 3},
            {"3", "abcdef", 1, 5, "XY", "aXf", 2},
            {"0", "", 0, 0, "x", "", 0},
            {"4", u8"a😀b", 1, 5, u8"界😀", u8"a界b", 4},
            {"4", u8"a😀b", 5, 5, "x", u8"a😀b", 5},
        };
        for (const auto& c : cases) {
            Field d(markup(tag, c.limit));
            d.value(c.before, c.from, c.to);
            CHECK(weva_document_try_text_input(d.doc, c.input) == 1);
            CHECK_EQ(d.value(), c.after);
            d.selection(c.caret, c.caret);
            const auto events = d.events();
            if (c.from == c.to && std::string(c.before) == c.after) {
                CHECK(events.empty());
                CHECK(weva_document_undo(d.doc) == 0);
            } else {
                CHECK(events.size() == 2);
                CHECK(events[0].first == WEVA_EVENT_VALUE_CHANGED);
                CHECK(events[0].second == c.after);
                CHECK(events[1].first == WEVA_EVENT_TEXT_INPUT);
                CHECK(events[1].second == std::string(c.after).substr(c.from, c.caret - c.from));
                CHECK(weva_document_undo(d.doc) == 1); CHECK_EQ(d.value(), c.before);
                CHECK(weva_document_redo(d.doc) == 1); CHECK_EQ(d.value(), c.after);
            }
        }
        for (const char* limit : {"3x", "3.5", "+3", " \t3", "003", "3e5"}) {
            Field d(markup(tag, limit));
            weva_document_try_text_input(d.doc, "abcde"); CHECK_EQ(d.value(), "abc");
        }
        for (const char* limit : {"", "-1", "2147483648", "99999999999999999999", "x3", "+-3"}) {
            Field d(markup(tag, limit));
            weva_document_try_text_input(d.doc, "abcde"); CHECK_EQ(d.value(), "abcde");
        }
        Field d(markup(tag, "3"));
        weva_document_try_text_input(d.doc, "ab");
        weva_document_try_text_input(d.doc, "c");
        d.events();
        weva_document_try_text_input(d.doc, "ignored");
        CHECK(d.events().empty());
        CHECK(weva_document_undo(d.doc) == 1); CHECK_EQ(d.value(), "");
        // A rejected insertion does not clear redo or create its own checkpoint.
        weva_element_set_attribute(d.doc, d.field, "maxlength", "0");
        weva_document_try_text_input(d.doc, "ignored");
        CHECK(weva_document_redo(d.doc) == 1); CHECK_EQ(d.value(), "abc");
        d.value("programmatic", 12, 12);
        weva_document_update(d.doc, 0); CHECK_EQ(d.value(), "programmatic");
        weva_element_set_attribute(d.doc, d.field, "maxlength", nullptr);
        weva_document_try_text_input(d.doc, "!"); CHECK_EQ(d.value(), "programmatic!");
        d.value("", 0, 0);
        weva_document_try_text_input(d.doc, "typed");
        CHECK(weva_document_paste_text(d.doc, "\tpaste\r\nnext") == 1);
        CHECK_EQ(d.value(), std::string(tag) == "textarea" ? "typed\tpaste\nnext" : "typed\tpaste next");
        weva_document_try_text_input(d.doc, "!");
        CHECK(weva_document_undo(d.doc) == 1);
        CHECK(weva_document_undo(d.doc) == 1); CHECK_EQ(d.value(), "typed");
        CHECK(weva_document_undo(d.doc) == 1); CHECK_EQ(d.value(), "");
        d.events();
        weva_element_set_attribute(d.doc, d.field, "readonly", "");
        CHECK(weva_document_paste_text(d.doc, "ignored") == 0);
        CHECK(d.events().empty()); CHECK_EQ(d.value(), "");
        CHECK(weva_document_paste_text(nullptr, "x") == 0);
        CHECK(weva_document_paste_text(d.doc, nullptr) == 0);
    }
    for (const char* type : {"text", "password", "search", "email", "url", "tel", "TEXT"}) {
        Field d(std::string("<input id=f maxlength=2 type=") + type + ">");
        weva_document_try_text_input(d.doc, "abc"); CHECK_EQ(d.value(), "ab");
    }
    Field number("<input id=f maxlength=2 type=number>");
    weva_document_try_text_input(number.doc, "1234"); CHECK_EQ(number.value(), "1234");
}

void test_abi_maxlength_newlines() {
    for (const char* tag : {"input", "textarea"}) {
        const bool area = std::string(tag) == "textarea";
        Field d(markup(tag, "5"));
        weva_document_paste_text(d.doc, "\nb\rc\r\nd");
        CHECK_EQ(d.value(), area ? "\nb\nc\n" : " b c ");
        CHECK(weva_document_undo(d.doc) == 1); CHECK_EQ(d.value(), "");
        weva_element_set_attribute(d.doc, d.field, "maxlength", "20");
        weva_document_try_text_input(d.doc, "abc\n\r\n");
        CHECK_EQ(d.value(), area ? "abc\n\n" : "abc");
    }
    Field d(markup("textarea", "3"));
    d.value("abc", 1, 2);
    CHECK(weva_document_key(d.doc, WEVA_KEY_ENTER, 0, 1) == 1);
    CHECK_EQ(d.value(), "a\nc"); d.selection(2, 2);
    weva_document_key(d.doc, WEVA_KEY_ENTER, 0, 0);
    CHECK(weva_document_undo(d.doc) == 1); CHECK_EQ(d.value(), "abc");
    d.value("abcdef", 2, 4);
    CHECK(weva_document_key(d.doc, WEVA_KEY_ENTER, 0, 1) == 1);
    CHECK_EQ(d.value(), "abcdef"); d.selection(2, 4);
    CHECK(weva_document_undo(d.doc) == 0);
    d.value("abc", 3, 3);
    CHECK(weva_document_key(d.doc, WEVA_KEY_ENTER, 0, 1) == 1);
    CHECK_EQ(d.value(), "abc"); CHECK(weva_document_undo(d.doc) == 0);
}

void test_abi_maxlength_composition() {
    for (const char* tag : {"input", "textarea"}) {
        for (const char* result : {u8"日本", u8"😀"}) {
            Field d(markup(tag, "1"));
            CHECK(weva_document_set_composition(d.doc, result, 0, static_cast<int>(std::strlen(result))) == 1);
            CHECK_EQ(d.value(), result); // Preedit remains unlimited.
            d.events();
            CHECK(weva_document_commit_composition(d.doc, result) == 1);
            const std::string accepted = std::string(result) == u8"日本" ? u8"日" : "";
            CHECK_EQ(d.value(), accepted);
            const auto events = d.events();
            CHECK(events == (std::vector<std::pair<int, std::string>>{
                {WEVA_EVENT_COMPOSITION_UPDATE, result}, {WEVA_EVENT_VALUE_CHANGED, accepted},
                {WEVA_EVENT_TEXT_INPUT, accepted}, {WEVA_EVENT_COMPOSITION_END, result}}));
            CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
            CHECK(weva_document_undo(d.doc) == (accepted.empty() ? 0 : 1)); CHECK_EQ(d.value(), "");
        }
        Field d(markup(tag, "3"));
        d.value("abc", 1, 2);
        weva_document_set_composition(d.doc, u8"日本語", 0, 9);
        CHECK_EQ(d.value(), u8"a日本語c");
        weva_document_commit_composition(d.doc, u8"日本語");
        CHECK_EQ(d.value(), u8"a日c"); d.selection(4, 4);
        weva_document_undo(d.doc); CHECK_EQ(d.value(), "abc");
        weva_document_redo(d.doc); CHECK_EQ(d.value(), u8"a日c");
        d.value("", 0, 0);
        weva_document_set_composition(d.doc, u8"日本語", 9, 9);
        weva_element_set_attribute(d.doc, d.field, "maxlength", "1");
        weva_document_set_focus(d.doc, weva_document_query(d.doc, "#other"));
        CHECK_EQ(d.value(), u8"日"); // Blur finishes and enforces the new limit.
        weva_document_set_focus(d.doc, d.field);
        weva_document_undo(d.doc); CHECK_EQ(d.value(), "");
        weva_document_set_composition(d.doc, u8"日本", 6, 6);
        weva_element_set_attribute(d.doc, d.field, "disabled", "");
        weva_document_update(d.doc, 0);
        CHECK_EQ(d.value(), u8"日本"); // Disabling retains the current preedit, as Chrome does.
        CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
    }
}
