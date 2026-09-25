// ABI minor 39: key auto-repeat and double-click-to-word owned by the core.
//
// Both hosts had their own idea of these -- Godot took them from the platform
// (key echo, is_double_click), Unity had neither -- so the behaviour a page
// saw depended on the host. The core now owns both under its input clock,
// opt-in, and this is the one place they are tested.
#include "check.h"
#include "weva_c.h"
#include <cstring>
#include <string>
#include <vector>
namespace {
weva_config config() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    return c;
}
struct Doc {
    weva_document_t d = nullptr;
    Doc(const char* css, const char* html) {
        weva_config c = config();
        d = weva_document_create(&c);
        weva_document_add_css(d, css, std::strlen(css));
        weva_document_load_html(d, html, std::strlen(html));
        weva_document_update(d, 0);
    }
    ~Doc() { weva_document_destroy(d); }
    Doc(const Doc&) = delete;
    Doc& operator=(const Doc&) = delete;
    std::string value(const char* selector) {
        char buf[128] = {};
        weva_element_value(d, weva_document_query(d, selector), buf, sizeof buf);
        return buf;
    }
    std::string selected() {
        char buf[128] = {};
        weva_document_selected_text(d, buf, sizeof buf);
        return buf;
    }
    // Advances only the input clock: CSS time stays still, like a paused game.
    void tick(double seconds) { weva_document_update_with_input_time(d, 0, seconds); }
    void key(int k, int down, uint32_t mods = 0) { weva_document_key(d, k, mods, down); }
    void tap(int k) { key(k, 1); key(k, 0); }
    int clicks() {
        int n = 0;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) if (e.kind == WEVA_EVENT_CLICK) ++n;
        return n;
    }
};
const char* kField = "<input id=f value=abcdef>";
}   // namespace

void test_abi_key_repeat() {
    // Off by default: a held Backspace deletes once, however long it is held.
    {
        Doc doc("", kField);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#f"));
        doc.tap(WEVA_KEY_END);
        doc.key(WEVA_KEY_BACKSPACE, 1);
        CHECK(doc.value("#f") == "abcde");
        CHECK(weva_document_needs_input_tick(doc.d) == 0);
        doc.tick(5.0);
        CHECK(doc.value("#f") == "abcde");
        doc.key(WEVA_KEY_BACKSPACE, 0);
    }
    // On: the edge deletes one, the delay passes, then one per interval.
    {
        Doc doc("", kField);
        weva_document_set_key_repeat(doc.d, 0.5, 0.1);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#f"));
        doc.tap(WEVA_KEY_END);
        doc.key(WEVA_KEY_BACKSPACE, 1);
        CHECK(doc.value("#f") == "abcde");
        CHECK(weva_document_needs_input_tick(doc.d) == 1);
        doc.tick(0.4);
        CHECK(doc.value("#f") == "abcde");          // inside the delay
        doc.tick(0.2);                              // t = 0.6: the repeats at 0.5 and 0.6
        CHECK(doc.value("#f") == "abc");
        doc.tick(0.3);                              // t = 0.9: 0.7, 0.8, 0.9
        CHECK(doc.value("#f") == "");
        doc.key(WEVA_KEY_BACKSPACE, 0);
        CHECK(weva_document_needs_input_tick(doc.d) == 0);
        doc.tick(2.0);
        CHECK(doc.value("#f") == "");
    }
    // A long frame catches up rather than losing repeats.
    {
        Doc doc("", kField);
        weva_document_set_key_repeat(doc.d, 0.5, 0.1);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#f"));
        doc.tap(WEVA_KEY_END);
        doc.key(WEVA_KEY_BACKSPACE, 1);
        doc.tick(0.75);                             // 0.5, 0.6, 0.7 -> three repeats
        CHECK(doc.value("#f") == "ab");
        doc.key(WEVA_KEY_BACKSPACE, 0);
    }
    // Another key-down disarms the held one; arrows repeat as well.
    {
        Doc doc("", kField);
        weva_document_set_key_repeat(doc.d, 0.5, 0.1);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#f"));
        doc.tap(WEVA_KEY_END);
        doc.key(WEVA_KEY_BACKSPACE, 1);
        doc.key(WEVA_KEY_LEFT, 1);                  // takes over the arm
        doc.tick(1.0);
        CHECK(doc.value("#f") == "abcde");          // Backspace fired once, on its edge
        doc.key(WEVA_KEY_LEFT, 0);
        doc.key(WEVA_KEY_BACKSPACE, 0);
    }
    // Space never repeats: a button activates on the edge, once.
    {
        Doc doc("", "<button id=b>Go</button>");
        weva_document_set_key_repeat(doc.d, 0.5, 0.1);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#b"));
        doc.clicks();
        doc.key(WEVA_KEY_SPACE, 1);
        doc.tick(2.0);
        CHECK(weva_document_needs_input_tick(doc.d) == 0);
        doc.key(WEVA_KEY_SPACE, 0);
        CHECK(doc.clicks() == 1);
    }
    // Focus moving disarms: a held arrow does not keep firing into the next field.
    {
        Doc doc("", "<input id=f value=abcdef><input id=g value=xyz>");
        weva_document_set_key_repeat(doc.d, 0.5, 0.1);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#f"));
        doc.tap(WEVA_KEY_END);
        doc.key(WEVA_KEY_BACKSPACE, 1);
        doc.tick(0.55);
        CHECK(doc.value("#f") == "abcd");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#g"));
        doc.tick(1.0);
        CHECK(doc.value("#g") == "xyz");
        CHECK(doc.value("#f") == "abcd");
        doc.key(WEVA_KEY_BACKSPACE, 0);
    }
}

void test_abi_double_click() {
    const char* html = "<input id=f value=\"hello world\">";
    const auto press_twice = [](Doc& doc, double gap, double dx) {
        double bx = 0, by = 0, bw = 0, bh = 0;
        weva_element_bounds(doc.d, weva_document_query(doc.d, "#f"), &bx, &by, &bw, &bh);
        const double x = bx + bw * 0.9, y = by + bh / 2;
        weva_document_set_pointer(doc.d, x, y, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(doc.d, x, y, 0);
        doc.tick(gap);
        weva_document_set_pointer(doc.d, x + dx, y, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(doc.d, x + dx, y, 0);
    };
    // Off by default: two quick presses are two clicks, no selection.
    {
        Doc doc("", html);
        press_twice(doc, 0.1, 0);
        CHECK(doc.selected().empty());
    }
    // On: two presses inside the window select the word under them.
    {
        Doc doc("", html);
        weva_document_set_double_click(doc.d, 0.5, 4);
        press_twice(doc, 0.1, 0);
        CHECK(doc.selected() == "world");
    }
    // Beyond the window, or too far apart, they are two clicks.
    {
        Doc doc("", html);
        weva_document_set_double_click(doc.d, 0.5, 4);
        press_twice(doc, 0.6, 0);
        CHECK(doc.selected().empty());
    }
    {
        Doc doc("", html);
        weva_document_set_double_click(doc.d, 0.5, 4);
        press_twice(doc, 0.1, -12);
        CHECK(doc.selected().empty());
    }
    // A third press inside the window starts over rather than chaining.
    {
        Doc doc("", html);
        weva_document_set_double_click(doc.d, 0.5, 4);
        press_twice(doc, 0.1, 0);
        CHECK(doc.selected() == "world");
        double bx = 0, by = 0, bw = 0, bh = 0;
        weva_element_bounds(doc.d, weva_document_query(doc.d, "#f"), &bx, &by, &bw, &bh);
        const double x = bx + bw * 0.9, y = by + bh / 2;
        doc.tick(0.1);
        weva_document_set_pointer(doc.d, x, y, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(doc.d, x, y, 0);
        CHECK(doc.selected().empty());              // a plain click collapses it
    }
}
