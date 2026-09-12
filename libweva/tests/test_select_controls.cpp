#include "check.h"
#include "weva_c.h"
#include "weva/form_state.h"
#include <cstring>
#include <string>
#include <vector>

namespace {
struct SelectDoc {
    weva_document_t doc;
    explicit SelectDoc(bool multiple = true, const char* size = "4", const char* selected = "") {
        weva_config config{};
        config.viewport_width = 400; config.viewport_height = 300; config.use_user_agent_stylesheet = 1;
        doc = weva_document_create(&config);
        const char* css = "html,body{margin:0}select{display:block;width:240px;height:210px;padding:0;border:0}"
                          "option{height:24px;padding:0}button{display:block}";
        weva_document_add_css(doc, css, std::strlen(css));
        std::string html = "<form id=f><select id=s size='" + std::string(size) + "'" + (multiple ? " multiple>" : ">");
        for (char c : std::string("abcdegh")) {
            if (c == 'e') html += "<optgroup disabled label=Off>";
            html += "<option id=o" + std::string(1, c) + " value=" + c;
            if (c == 'c') html += " disabled";
            if (std::strchr(selected, c)) html += " selected";
            html += ">" + std::string(1, c) + "</option>";
            if (c == 'e') html += "</optgroup>";
        }
        html += "</select><button id=other type=button>Other</button></form>";
        weva_document_load_html(doc, html.data(), html.size());
        update();
        weva_document_set_focus(doc, at("#s")); update(); events();
    }
    ~SelectDoc() { weva_document_destroy(doc); }
    weva_element_t at(const char* selector) const { return weva_document_query(doc, selector); }
    void update() { CHECK(weva_document_update(doc, 0) == WEVA_OK); }
    std::string selected() const {
        std::string out;
        for (char c : std::string("abcdegh")) {
            const std::string selector = "#o" + std::string(1, c) + ":checked";
            if (at(selector.c_str()) != WEVA_ELEMENT_NONE) out += c;
        }
        return out;
    }
    std::vector<int> events() const {
        std::vector<int> out; weva_event event{};
        while (weva_document_poll_event(doc, &event))
            if (event.kind == WEVA_EVENT_VALUE_CHANGED || event.kind == WEVA_EVENT_CHANGE) {
                CHECK(event.target == at("#s")); out.push_back(event.kind);
            }
        return out;
    }
    void changed(const std::string& expected, bool change = true) {
        CHECK_EQ(selected(), expected);
        CHECK(events() == (change ? std::vector<int>{WEVA_EVENT_VALUE_CHANGED, WEVA_EVENT_CHANGE} : std::vector<int>{}));
        update();
    }
    void key(int key, uint32_t mods = 0) {
        CHECK(weva_document_key(doc, key, mods, 1) == 1);
        weva_document_key(doc, key, mods, 0); update();
    }
    void pointer(char row, uint32_t buttons, uint32_t mods = 0) {
        const std::string selector = "#o" + std::string(1, row);
        double x=0,y=0,w=0,h=0;
        CHECK(weva_element_bounds(doc, at(selector.c_str()), &x,&y,&w,&h) == WEVA_OK);
        weva_document_set_pointer_modifiers(doc, x+w/2, y+h/2, buttons, mods);
        update();
    }
    void click(char row, uint32_t mods = 0) { pointer(row, 0, mods); pointer(row, 1, mods); pointer(row, 0, mods); }
};
}

void test_select_keyboard_ranges() {
    // The exact sequence is also checked against native Chrome listboxes by
    // Tools/oracle/check_select_chrome.cjs, including optgroup page distances.
    for (bool multiple : {false, true}) {
        SelectDoc d(multiple);
        d.key(WEVA_KEY_DOWN); d.changed("a");
        d.key(WEVA_KEY_DOWN); d.changed("b");
        d.key(WEVA_KEY_UP, WEVA_MOD_SHIFT); d.changed(multiple ? "ab" : "a");
        d.key(WEVA_KEY_DOWN, WEVA_MOD_SHIFT); d.changed("b");
        d.key(WEVA_KEY_DOWN, WEVA_MOD_CTRL); d.changed(multiple ? "b" : "d", !multiple);
        d.key(WEVA_KEY_SPACE, WEVA_MOD_CTRL); d.changed(multiple ? "bd" : "d", multiple);
        d.key(WEVA_KEY_DOWN); d.changed("g");
        d.key(WEVA_KEY_HOME); d.changed("a");
        d.key(WEVA_KEY_END, WEVA_MOD_SHIFT); d.changed(multiple ? "abdgh" : "h");
        d.key(WEVA_KEY_PAGE_UP); d.changed("d");
        d.key(WEVA_KEY_PAGE_DOWN); d.changed("g");
        CHECK(weva_document_select_all(d.doc) == (multiple ? 1 : 0));
        d.changed(multiple ? "abdgh" : "g", multiple);
        d.key(WEVA_KEY_ENTER); d.changed(multiple ? "abdgh" : "g", false);
        d.key(WEVA_KEY_SPACE); d.changed(multiple ? "abdgh" : "g", false);
    }
    SelectDoc d(true, "4", "bg");
    d.key(WEVA_KEY_DOWN); d.changed("d"); // first selected row is the initial keyboard position
    d.key(WEVA_KEY_END); d.changed("h");
    d.key(WEVA_KEY_DOWN); d.changed("h", false);
    CHECK(weva_element_set_value(d.doc, d.at("#s"), "a") == WEVA_OK); d.update(); d.events();
    d.key(WEVA_KEY_DOWN, WEVA_MOD_SHIFT); d.changed("ab");
    CHECK(weva_document_reset_form(d.doc, d.at("#f")) == WEVA_OK); d.update(); d.events();
    d.key(WEVA_KEY_DOWN); d.changed("d");
    SelectDoc empty;
    empty.key(WEVA_KEY_UP); empty.changed("h");
    SelectDoc disabled(true, "4", "c");
    disabled.click('b'); disabled.changed("b"); // plain replacement clears disabled selected defaults
}

void test_select_pointer_ranges() {
    SelectDoc d;
    d.click('b'); d.changed("b");
    d.click('g', WEVA_MOD_CTRL); d.changed("bg");
    d.click('d', WEVA_MOD_SHIFT); d.changed("dg");
    d.click('a', WEVA_MOD_SHIFT); d.changed("abdg");
    d.click('h', WEVA_MOD_CTRL | WEVA_MOD_SHIFT); d.changed("gh");
    d.click('d'); d.changed("d");
    d.click('d'); d.changed("d", false);
    d.click('d', WEVA_MOD_CTRL); d.changed("");
    d.click('b', WEVA_MOD_SHIFT); d.changed("bd");
    d.click('c'); d.changed("bd", false);
    d.click('e'); d.changed("bd", false);
    CHECK(weva_document_open_select(d.doc, d.at("#s")) == 0);

    SelectDoc drag(true, "4", "bg");
    drag.pointer('b', 1); drag.changed("b", false);
    drag.pointer('g', 1); drag.changed("bdg", false);
    drag.pointer('g', 0); drag.changed("bdg");
    drag.pointer('b', 1, WEVA_MOD_CTRL); drag.changed("dg", false);
    drag.pointer('g', 1, WEVA_MOD_CTRL); drag.changed("", false);
    drag.pointer('g', 0, WEVA_MOD_CTRL); drag.changed("");
    drag.pointer('a', 1); drag.changed("a", false);
    weva_document_clear_pointer(drag.doc); drag.changed("a");

    SelectDoc single(false);
    single.click('b'); single.changed("b");
    single.click('b', WEVA_MOD_CTRL); single.changed("b", false);
    single.click('g', WEVA_MOD_SHIFT); single.changed("g");
    single.pointer('b', 1); single.changed("b", false);
    single.pointer('h', 1); single.changed("h", false);
    single.pointer('h', 0); single.changed("h");
}

void test_select_display_size() {
    for (const char* raw : {"", "0", "1", "01", "+1", "1x", "-1", "x", "-0", "4294967296"}) {
        SelectDoc d(false, raw);
        CHECK_EQ(d.selected(), "a");
        double x=0,y=0,w=0,h=0;
        CHECK(weva_element_bounds(d.doc, d.at("#oa"), &x,&y,&w,&h) != WEVA_OK);
        CHECK(weva_document_open_select(d.doc, d.at("#s")) == 1);
        weva_document_open_select(d.doc, WEVA_ELEMENT_NONE);
    }
    for (const char* raw : {"2", "2x", "2.5", "+2", "4294967295"}) {
        SelectDoc d(false, raw);
        CHECK(d.selected().empty());
        double x=0,y=0,w=0,h=0;
        CHECK(weva_element_bounds(d.doc, d.at("#oa"), &x,&y,&w,&h) == WEVA_OK);
        CHECK(weva_document_open_select(d.doc, d.at("#s")) == 0);
    }
    SelectDoc d(false, "1");
    // Keep the selected option unchanged so only display-mode inputs can
    // invalidate the retained box tree. Exercise both directions repeatedly.
    for (int i = 0; i < 3; ++i) {
        for (const char* size : {"4", "1"}) {
            CHECK(weva_element_set_attribute(d.doc, d.at("#s"), "size", size) == WEVA_OK); d.update();
            double x=0,y=0,w=0,h=0;
            CHECK((weva_element_bounds(d.doc, d.at("#oa"), &x,&y,&w,&h) == WEVA_OK) == (size[0]=='4'));
            CHECK_EQ(d.selected(), "a");
        }
    }
    CHECK(weva_element_set_attribute(d.doc, d.at("#s"), "multiple", "") == WEVA_OK); d.update();
    double x=0,y=0,w=0,h=0;
    CHECK(weva_element_bounds(d.doc, d.at("#oa"), &x,&y,&w,&h) == WEVA_OK);
    SelectDoc popup(false, "1");
    CHECK(weva_document_open_select(popup.doc, popup.at("#s")) == 1);
    CHECK(weva_element_set_attribute(popup.doc, popup.at("#s"), "size", "4") == WEVA_OK); popup.update();
    CHECK(weva_document_open_select_element(popup.doc) == WEVA_ELEMENT_NONE);
}

namespace {
std::string draw_geometry(weva_document_t doc) {
    size_t count = 0;
    const auto* draws = weva_document_draws(doc, &count);
    std::string out;
    for (size_t i = 0; i < count; ++i) {
        const auto& d = draws[i];
        out.append(reinterpret_cast<const char*>(&d.kind), sizeof(d.kind));
        out.append(reinterpret_cast<const char*>(d.vertices), d.vertex_count * sizeof(weva_vertex));
        out.append(reinterpret_cast<const char*>(d.indices), d.index_count * sizeof(uint32_t));
    }
    return out;
}
}
void test_select_retained_paint() {
    SelectDoc d(true, "4", "b");
    const auto before = draw_geometry(d.doc);
    d.key(WEVA_KEY_DOWN, WEVA_MOD_CTRL); d.changed("b", false);
    CHECK(draw_geometry(d.doc) != before); // focus-only navigation must paint a visible row cue
    auto full_matches = [&] {
        const auto incremental = draw_geometry(d.doc);
        weva_document_set_viewport(d.doc, 401, 300);
        weva_document_set_viewport(d.doc, 400, 300);
        d.update();
        CHECK(draw_geometry(d.doc) == incremental);
    };
    full_matches();
    for (int key : {WEVA_KEY_UP, WEVA_KEY_DOWN, WEVA_KEY_END, WEVA_KEY_HOME}) {
        d.key(key, WEVA_MOD_CTRL); d.changed("b", false); full_matches();
    }
    weva_document_set_focus(d.doc, d.at("#other")); d.update(); full_matches();
    weva_document_set_focus(d.doc, d.at("#s")); d.update(); full_matches();
    const uint64_t serial = weva_document_draw_serial(d.doc);
    for (int i=0; i<100; ++i) d.update();
    CHECK(weva_document_draw_serial(d.doc) == serial);
    CHECK(weva_element_remove(d.doc, d.at("#oa")) == WEVA_OK); d.update(); full_matches();
    d.key(WEVA_KEY_DOWN); d.changed("d"); full_matches();
    CHECK(weva_element_set_attribute(d.doc, d.at("#s"), "style", "height:48px") == WEVA_OK); d.update();
    d.key(WEVA_KEY_END, WEVA_MOD_CTRL); d.changed("d", false); full_matches();
    double y = 0;
    CHECK(weva_element_scroll(d.doc, d.at("#s"), nullptr, &y, nullptr, nullptr) == WEVA_OK);
    CHECK(y > 0);
}

void test_select_disabled_dropdown() {
    SelectDoc d(false, "1");
    d.key(WEVA_KEY_DOWN); d.changed("b");
    d.key(WEVA_KEY_DOWN); d.changed("d");
    d.key(WEVA_KEY_DOWN); d.changed("g");
    d.key(WEVA_KEY_SPACE); d.events();
    d.key(WEVA_KEY_UP); d.changed("g", false);
    d.key(WEVA_KEY_ENTER); d.changed("d");
    CHECK(weva_document_open_select_element(d.doc) == WEVA_ELEMENT_NONE);
    d.key(WEVA_KEY_SPACE); d.events();
    d.key(WEVA_KEY_ENTER); d.changed("d", false);
    CHECK(weva_element_set_attribute(d.doc, d.at("#s"), "disabled", "") == WEVA_OK); d.update();
    CHECK(weva_document_open_select(d.doc, d.at("#s")) == 0);
}
