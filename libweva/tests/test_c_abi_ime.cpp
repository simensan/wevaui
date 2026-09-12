#include "check.h"
#include "weva_c.h"

#include <cmath>
#include <cstring>
#include <string>
#include <vector>

namespace {
struct Document {
    weva_document_t doc;
    weva_element_t field;
    explicit Document(const char* html, const char* css = "html,body{margin:0}input,textarea{font-size:16px}") {
        weva_config config{};
        config.viewport_width = 400;
        config.viewport_height = 300;
        config.use_user_agent_stylesheet = 1;
        doc = weva_document_create(&config);
        weva_document_add_css(doc, css, std::strlen(css));
        weva_document_load_html(doc, html, std::strlen(html));
        weva_document_update(doc, 0);
        field = weva_document_query(doc, "#field");
        weva_document_set_focus(doc, field);
        events();
    }
    ~Document() { weva_document_destroy(doc); }
    std::string value() const {
        char buffer[1024]{};
        weva_element_value(doc, field, buffer, sizeof(buffer));
        return buffer;
    }
    std::vector<int> events() const {
        std::vector<int> result;
        weva_event event{};
        while (weva_document_poll_event(doc, &event)) result.push_back(event.kind);
        return result;
    }
    std::vector<float> geometry() const {
        weva_document_update(doc, 0);
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(doc, &count);
        std::vector<float> result;
        for (size_t i = 0; i < count; ++i)
            for (size_t j = 0; j < draws[i].vertex_count; ++j) {
                result.push_back(draws[i].vertices[j].x);
                result.push_back(draws[i].vertices[j].y);
            }
        return result;
    }
};
}

void test_abi_ime_composition() {
    for (const char* html : {"<input id=field value=abcd><button id=other>Other</button>",
                             "<textarea id=field>abcd</textarea><button id=other>Other</button>"}) {
        Document d(html);
        CHECK(weva_document_text_input_target(d.doc) == d.field);
        weva_element_set_selection(d.doc, d.field, 1, 3);
        CHECK(weva_document_set_composition(d.doc, u8"に", 3, 3) == 1);
        CHECK_EQ(d.value(), u8"aにd");
        CHECK(d.events() == std::vector<int>({WEVA_EVENT_COMPOSITION_START, WEVA_EVENT_COMPOSITION_UPDATE, WEVA_EVENT_VALUE_CHANGED}));
        int from = 0, to = 0;
        CHECK(weva_document_composition(d.doc, &from, &to) == d.field);
        CHECK(from == 1 && to == 4);
        CHECK(weva_document_set_composition(d.doc, u8"日本", 0, 6) == 1);
        CHECK_EQ(d.value(), u8"a日本d");
        CHECK(d.events() == std::vector<int>({WEVA_EVENT_COMPOSITION_UPDATE, WEVA_EVENT_VALUE_CHANGED}));
        weva_element_selection(d.doc, d.field, &from, &to);
        CHECK(from == 1 && to == 7);
        CHECK(weva_document_commit_composition(d.doc, nullptr) == 1);
        CHECK(d.events() == std::vector<int>({WEVA_EVENT_COMPOSITION_END}));
        CHECK_EQ(d.value(), u8"a日本d");
        CHECK(weva_document_undo(d.doc) == 1);
        CHECK_EQ(d.value(), "abcd");
        CHECK(weva_document_redo(d.doc) == 1);
        CHECK_EQ(d.value(), u8"a日本d");

        weva_element_set_value(d.doc, d.field, "abcd");
        weva_element_set_selection(d.doc, d.field, 1, 3);
        weva_document_set_composition(d.doc, u8"に", 3, 3);
        weva_document_set_composition(d.doc, u8"日本", 6, 6);
        d.events();
        CHECK(weva_document_commit_composition(d.doc, u8"日本語") == 1);
        CHECK_EQ(d.value(), u8"a日本語d");
        CHECK(d.events() == std::vector<int>({WEVA_EVENT_COMPOSITION_UPDATE, WEVA_EVENT_VALUE_CHANGED, WEVA_EVENT_TEXT_INPUT, WEVA_EVENT_COMPOSITION_END}));
        CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
        weva_document_try_text_input(d.doc, "x");
        weva_document_undo(d.doc);
        CHECK_EQ(d.value(), u8"a日本語d");
        weva_document_undo(d.doc);
        CHECK_EQ(d.value(), "abcd");

        // An insertion cancelled back to its original value adds no undo
        // checkpoint and preserves the edit that came before it.
        weva_element_set_value(d.doc, d.field, "");
        weva_document_try_text_input(d.doc, "prior");
        weva_document_set_composition(d.doc, "preview", 7, 7);
        weva_document_commit_composition(d.doc, "");
        CHECK_EQ(d.value(), "prior");
        CHECK(weva_document_undo(d.doc) == 1);
        CHECK_EQ(d.value(), "");
        CHECK(weva_document_redo(d.doc) == 1);
        CHECK_EQ(d.value(), "prior");

        weva_element_set_value(d.doc, d.field, "abcd");
        weva_element_set_selection(d.doc, d.field, 1, 3);
        weva_document_set_composition(d.doc, u8"日本", 6, 6);
        CHECK(weva_document_set_composition(d.doc, "", 0, 0) == 1);
        CHECK_EQ(d.value(), "ad"); // Chrome cancellation removes the replaced range too.
        weva_document_undo(d.doc);
        CHECK_EQ(d.value(), "abcd");

        weva_element_set_selection(d.doc, d.field, 1, 3);
        weva_document_set_composition(d.doc, u8"日本", 6, 6);
        d.events();
        weva_document_set_focus(d.doc, weva_document_query(d.doc, "#other"));
        CHECK_EQ(d.value(), u8"a日本d");
        CHECK(d.events() == std::vector<int>({WEVA_EVENT_COMPOSITION_END, WEVA_EVENT_CHANGE, WEVA_EVENT_BLUR, WEVA_EVENT_FOCUS}));
        CHECK(weva_document_text_input_target(d.doc) == WEVA_ELEMENT_NONE);
        CHECK(weva_document_set_composition(d.doc, "bad", 0, 0) == 0);
    }
}

void test_abi_ime_lifecycle() {
    Document d("<fieldset id=group><input id=field value=abcd></fieldset>");
    weva_element_set_selection(d.doc, d.field, 1, 3);
    CHECK(weva_document_set_composition(d.doc, u8"😀界", 2, 99) == 1);
    int start = -1, end = -1;
    weva_element_selection(d.doc, d.field, &start, &end);
    CHECK(start == 1 && end == 8); // A selection never splits a UTF-8 code point.
    weva_document_key(d.doc, WEVA_KEY_LEFT, 0, 1);
    CHECK_EQ(d.value(), u8"a😀界d");
    weva_document_key(d.doc, WEVA_KEY_ENTER, 0, 1);
    CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
    CHECK_EQ(d.value(), u8"a😀界d");
    weva_document_set_composition(d.doc, "x", 1, 1);
    weva_element_set_value(d.doc, d.field, "script");
    CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
    CHECK_EQ(d.value(), "script");
    weva_element_set_attribute(d.doc, d.field, "readonly", "");
    CHECK(weva_document_text_input_target(d.doc) == WEVA_ELEMENT_NONE);
    CHECK(weva_document_set_composition(d.doc, "bad", 0, 0) == 0);
    weva_element_set_attribute(d.doc, d.field, "readonly", nullptr);
    weva_document_set_composition(d.doc, "x", 1, 1);
    weva_element_set_attribute(d.doc, weva_document_query(d.doc, "#group"), "disabled", "");
    const auto disabled_value = d.value();
    CHECK(weva_document_try_text_input(d.doc, "ignored") == 0);
    CHECK_EQ(d.value(), disabled_value);
    CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
    weva_element_set_attribute(d.doc, weva_document_query(d.doc, "#group"), "disabled", nullptr);
    weva_document_set_composition(d.doc, "x", 1, 1);
    weva_element_remove(d.doc, d.field);
    CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
    weva_document_update(d.doc, 0);
    CHECK(weva_document_commit_composition(d.doc, nullptr) == 0);
    CHECK(weva_document_set_composition(nullptr, "x", 0, 0) == 0);
    CHECK(weva_document_commit_composition(nullptr, "x") == 0);
    CHECK(weva_document_composition(nullptr, &start, &end) == WEVA_ELEMENT_NONE);
    CHECK(start == 0 && end == 0);
}

void test_abi_ime_external_mutation() {
    for (bool update_first : {false, true}) {
        for (bool markup : {false, true}) {
            Document d("<textarea id=field>abcd</textarea>");
            weva_element_set_selection(d.doc, d.field, 1, 3);
            weva_document_set_composition(d.doc, "preview", 7, 7);
            d.events();
            if (markup) weva_element_set_html(d.doc, d.field, "x", 1);
            else weva_element_set_text(d.doc, d.field, "x");
            if (update_first) weva_document_update(d.doc, 0);
            // A DOM text replacement changes the reset default. Chrome keeps
            // the dirty live value and composition in progress.
            CHECK_EQ(d.value(), "apreviewd");
            CHECK(weva_document_composition(d.doc, nullptr, nullptr) == d.field);
            CHECK(d.events().empty());
            CHECK(weva_document_commit_composition(d.doc, "late result") == 1);
            CHECK_EQ(d.value(), "alate resultd");
            CHECK(weva_document_composition(d.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
            CHECK(d.events() == std::vector<int>({WEVA_EVENT_COMPOSITION_UPDATE, WEVA_EVENT_VALUE_CHANGED,
                  WEVA_EVENT_TEXT_INPUT, WEVA_EVENT_COMPOSITION_END}));
            CHECK(weva_document_undo(d.doc) == 1); CHECK_EQ(d.value(), "abcd");
            char text[16]{}; weva_element_text(d.doc, d.field, text, sizeof(text)); CHECK(std::string(text) == "x");
        }
    }
}

void test_abi_event_text_payload() {
    Document d("<input id=field>");
    const std::string preview = u8"日本語の長い候補😀";
    const std::string committed = u8"日本語の確定された文字列😀";
    weva_document_set_composition(d.doc, preview.c_str(), 0, 0);
    weva_document_set_composition(d.doc, preview.c_str(), 0, 0); // no duplicate update
    weva_document_commit_composition(d.doc, committed.c_str());
    const std::vector<std::pair<int, std::string>> expected{
        {WEVA_EVENT_COMPOSITION_START, ""},
        {WEVA_EVENT_COMPOSITION_UPDATE, preview}, {WEVA_EVENT_VALUE_CHANGED, preview},
        {WEVA_EVENT_COMPOSITION_UPDATE, committed}, {WEVA_EVENT_VALUE_CHANGED, committed},
        {WEVA_EVENT_TEXT_INPUT, committed}, {WEVA_EVENT_COMPOSITION_END, committed}};
    CHECK(weva_document_event_text(d.doc, nullptr, 0) == 0);
    for (const auto& [kind, text] : expected) {
        weva_event event{};
        CHECK(weva_document_poll_event(d.doc, &event) == 1);
        CHECK(event.kind == kind);
        CHECK_EQ(std::string(event.text), text.empty() ? "" : u8"日本");
        CHECK(weva_document_event_text(d.doc, nullptr, 0) == text.size());
        std::vector<char> buffer(text.size() + 1, 'x');
        CHECK(weva_document_event_text(d.doc, buffer.data(), buffer.size()) == text.size());
        CHECK_EQ(std::string(buffer.data()), text);
        char short_text[8]{};
        CHECK(weva_document_event_text(d.doc, short_text, sizeof(short_text)) == text.size());
        CHECK_EQ(std::string(short_text), text.empty() ? "" : u8"日本");
        char empty = 'x';
        weva_document_event_text(d.doc, &empty, 1);
        CHECK(empty == 0);
    }
    weva_event event{};
    CHECK(weva_document_poll_event(d.doc, &event) == 0);
    CHECK(weva_document_event_text(d.doc, nullptr, 0) == committed.size());
    weva_document_set_focus(d.doc, WEVA_ELEMENT_NONE);
    d.events();
    CHECK(weva_document_event_text(d.doc, nullptr, 0) == 0); // blur has no text
    CHECK(weva_document_event_text(nullptr, nullptr, 0) == 0);
}

void test_abi_ime_caret_geometry() {
    for (const char* initial : {"", "first\nsecond"}) {
        const std::string html = std::string("<textarea id=field>") + initial + "</textarea>";
        Document area(html.c_str());
        double x = 0, y = 0, width = 0, height = 0;
        weva_element_set_selection(area.doc, area.field, 0, 0);
        weva_document_update(area.doc, 0);
        CHECK(weva_document_caret_bounds(area.doc, &x, &y, &width, &height) == 1);
        const double first_y = y;
        weva_element_set_selection(area.doc, area.field, 99, 99);
        weva_document_update(area.doc, 0);
        CHECK(weva_document_caret_bounds(area.doc, &x, &y, &width, &height) == 1);
        CHECK(*initial ? y > first_y : y == first_y);
        weva_document_set_composition(area.doc, "XY", 2, 2);
        weva_document_update(area.doc, 0);
        CHECK(weva_document_caret_bounds(area.doc, &x, &y, &width, &height) == 1);
        CHECK(height > 0);
    }
    for (const char* html : {"<input id=field value=abcd>", "<textarea id=field>abcd</textarea>"}) {
        Document text(html);
        weva_element_set_selection(text.doc, text.field, 1, 3);
        weva_document_set_composition(text.doc, "XY", 0, 2);
        const auto preedit = text.geometry();
        weva_document_commit_composition(text.doc, nullptr);
        CHECK(text.geometry() != preedit); // The underline disappears without changing the value/selection.
    }
    Document d("<div id=wrap><input id=field value=abcd></div>");
    double x = 0, y = 0, width = 0, height = 0;
    weva_element_set_selection(d.doc, d.field, 0, 0);
    weva_document_update(d.doc, 0);
    CHECK(weva_document_caret_bounds(d.doc, &x, &y, &width, &height) == 1);
    CHECK(width == 1 && height > 0);
    const double beginning = x;
    weva_element_set_selection(d.doc, d.field, 4, 4);
    weva_document_update(d.doc, 0);
    weva_document_caret_bounds(d.doc, &x, &y, &width, &height);
    CHECK(x > beginning);
    const double original_x = x, original_y = y;
    weva_element_set_attribute(d.doc, weva_document_query(d.doc, "#wrap"), "style", "transform:translate(11px,7px)");
    weva_document_update(d.doc, 0.6);
    CHECK(weva_document_caret_bounds(d.doc, &x, &y, &width, &height) == 1);
    CHECK(std::abs(x - original_x - 11) < 0.01 && std::abs(y - original_y - 7) < 0.01);
    weva_element_set_attribute(d.doc, d.field, "style", "display:none");
    weva_document_update(d.doc, 0);
    CHECK(weva_document_caret_bounds(d.doc, &x, &y, &width, &height) == 0);
    CHECK(x == 0 && y == 0 && width == 0 && height == 0);
    CHECK(weva_document_caret_bounds(nullptr, nullptr, nullptr, nullptr, nullptr) == 0);
}

void test_abi_ime_candidate() {
    CHECK(weva_document_text_input_candidate(nullptr) == WEVA_ELEMENT_NONE);
    for (const char* type : {"text", "password", "search", "email", "url", "tel", "number", "checkbox", "radio", "range", "button"}) {
        Document d("<input id=field><button id=other>Other</button>");
        const auto serial = weva_document_draw_serial(d.doc);
        weva_element_set_attribute(d.doc, d.field, "type", type);
        const bool text = std::strcmp(type,"checkbox") && std::strcmp(type,"radio") && std::strcmp(type,"range") && std::strcmp(type,"button");
        CHECK(weva_document_text_input_candidate(d.doc) == (text ? d.field : WEVA_ELEMENT_NONE));
        weva_element_set_attribute(d.doc, d.field, "readonly", "");
        CHECK(weva_document_text_input_candidate(d.doc) == (text ? d.field : WEVA_ELEMENT_NONE));
        CHECK(weva_document_text_input_target(d.doc) == WEVA_ELEMENT_NONE);
        CHECK(weva_document_draw_serial(d.doc) == serial);
        weva_document_set_focus(d.doc, weva_document_query(d.doc,"#other"));
        CHECK(weva_document_text_input_candidate(d.doc) == WEVA_ELEMENT_NONE);
    }
    Document d("<textarea id=field>text</textarea>");
    CHECK(weva_document_text_input_candidate(d.doc) == d.field);
    weva_element_set_attribute(d.doc,d.field,"style","visibility:hidden");
    CHECK(weva_document_text_input_candidate(d.doc) == d.field);
    weva_document_update(d.doc,0);
    CHECK(weva_document_text_input_target(d.doc) == WEVA_ELEMENT_NONE);
}
