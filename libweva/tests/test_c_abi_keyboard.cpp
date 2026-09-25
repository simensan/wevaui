// Keyboard input and tab navigation.
//
// Native form actions live in the document. Hosts route keys and text, then
// observe the same click/input/change/submit handlers used by pointer input.
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

    std::vector<weva_event> drain() {
        std::vector<weva_event> out;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) out.push_back(e);
        return out;
    }
};

}   // namespace

void test_abi_embedded_focus_and_text() {
    Doc doc("", "<input id=field><button id=second tabindex=2>Go</button><button id=last>Last</button>");
    const auto field = weva_document_query(doc.d, "#field");
    const auto second = weva_document_query(doc.d, "#second");
    const auto last = weva_document_query(doc.d, "#last");
    CHECK(weva_document_focus_step(doc.d, 0, 0) == second);
    CHECK(weva_document_focus_step(doc.d, 0, 0) == field);
    CHECK(weva_document_focus_step(doc.d, 0, 0) == last);
    CHECK(weva_document_focus_step(doc.d, 0, 0) == WEVA_ELEMENT_NONE);
    CHECK(weva_document_focus(doc.d) == WEVA_ELEMENT_NONE);
    CHECK(weva_document_focus_step(doc.d, 1, 0) == last);
    CHECK(weva_document_focus_step(doc.d, 1, 0) == field);
    CHECK(weva_document_focus_step(doc.d, 1, 0) == second);
    CHECK(weva_document_focus_step(doc.d, 1, 0) == WEVA_ELEMENT_NONE);
    CHECK(weva_document_focus_next(doc.d, 1) == last);
    CHECK(weva_document_try_text_input(doc.d, "x") == 0);
    weva_document_set_focus(doc.d, field);
    CHECK(weva_document_try_text_input(doc.d, "abc") == 1);
    CHECK(weva_document_try_text_input(doc.d, "\t") == 0);
    weva_element_set_attribute(doc.d, field, "readonly", "");
    CHECK(weva_document_try_text_input(doc.d, "x") == 0);
    weva_document_select_all(doc.d);
    CHECK(weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1) == 1);
    CHECK(weva_document_undo(doc.d) == 0);
    char value[32]{};
    weva_element_value(doc.d, field, value, sizeof(value));
    CHECK_EQ(std::string(value), "abc");
    weva_element_set_attribute(doc.d, field, "readonly", nullptr);
    CHECK(weva_document_try_text_input(doc.d, "d") == 1);
    weva_element_value(doc.d, field, value, sizeof(value));
    CHECK_EQ(std::string(value), "d");
    weva_document_set_focus(doc.d, WEVA_ELEMENT_NONE);
    CHECK(weva_document_try_text_input(doc.d, "x") == 0);
    CHECK(weva_document_focus_step(nullptr, 0, 0) == WEVA_ELEMENT_NONE);
    CHECK(weva_document_try_text_input(nullptr, "x") == 0);
    Doc empty("", "<div>Empty</div>");
    CHECK(weva_document_focus_step(empty.d, 0, 0) == WEVA_ELEMENT_NONE);

    Doc clicks("html,body{margin:0}button,div,a{display:block;width:100px;height:30px}",
               "<input id=field><button id=button><span id=label>Go</span></button>"
               "<div id=negative tabindex=-1>Click</div><a id=link href='#'>Link</a>"
               "<button id=disabled disabled>Disabled</button><div id=plain>Plain</div>");
    const auto click = [&](const char* selector) {
        double x=0, y=0, w=0, h=0;
        const auto e = weva_document_query(clicks.d, selector);
        weva_element_bounds(clicks.d, e, &x, &y, &w, &h);
        weva_document_set_pointer(clicks.d, x+w/2, y+h/2, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(clicks.d, x+w/2, y+h/2, 0);
    };
    click("#label");
    CHECK(weva_document_focus(clicks.d) == weva_document_query(clicks.d, "#button"));
    click("#negative");
    CHECK(weva_document_focus(clicks.d) == weva_document_query(clicks.d, "#negative"));
    click("#link");
    CHECK(weva_document_focus(clicks.d) == weva_document_query(clicks.d, "#link"));
    click("#disabled");
    CHECK(weva_document_focus(clicks.d) == weva_document_query(clicks.d, "#link"));
    click("#plain");
    CHECK(weva_document_focus(clicks.d) == WEVA_ELEMENT_NONE);

    Doc popup("", "<select id=list><option>A</option><option>B</option></select>"
                  "<div id=auto popover=auto>Auto</div><div id=manual popover=manual>Manual</div>");
    const auto list = weva_document_query(popup.d, "#list");
    const auto automatic = weva_document_query(popup.d, "#auto");
    const auto manual = weva_document_query(popup.d, "#manual");
    CHECK(weva_document_transient_version(popup.d) == 0);
    weva_element_show_popover(popup.d, manual);
    CHECK(weva_document_transient_version(popup.d) == 0);
    weva_document_open_select(popup.d, list);
    const uint64_t before = weva_document_transient_version(popup.d);
    CHECK(before != 0);
    weva_element_show_popover(popup.d, automatic);
    CHECK(weva_document_dismiss_transients(popup.d, before) == 0);
    CHECK(weva_document_open_select_element(popup.d) == list);
    const uint64_t current = weva_document_transient_version(popup.d);
    CHECK(weva_document_dismiss_transients(popup.d, current) == 1);
    CHECK(weva_document_open_select_element(popup.d) == WEVA_ELEMENT_NONE);
    CHECK(weva_document_transient_version(popup.d) == 0);
    CHECK(weva_document_query(popup.d, "#manual[data-popover-open]") == manual);
    CHECK(weva_document_query(popup.d, "#auto[data-popover-open]") == WEVA_ELEMENT_NONE);
    CHECK(weva_document_dismiss_transients(popup.d, current) == 0);
    CHECK(weva_document_dismiss_transients(nullptr, current) == 0);
    weva_document_open_select(popup.d, list);
    weva_element_show_popover(popup.d, automatic);
    const uint64_t removed = weva_document_transient_version(popup.d);
    weva_element_remove(popup.d, automatic);
    CHECK(weva_document_dismiss_transients(popup.d, removed) == 0);
    CHECK(weva_document_open_select_element(popup.d) == list);
}

void test_abi_tab_navigation() {
    // Document order, with one element opting out and one opting in.
    Doc doc("html, body { margin: 0 } button, a, div { display: block }",
            "<button id=a>a</button>"
            "<div id=skip>not focusable</div>"
            "<a id=b href='#'>b</a>"
            "<button id=c tabindex=-1>c</button>"    // reachable by script only
            "<button id=d>d</button>");

    const weva_element_t a = weva_document_query(doc.d, "#a");
    const weva_element_t b = weva_document_query(doc.d, "#b");
    const weva_element_t c = weva_document_query(doc.d, "#c");
    const weva_element_t d = weva_document_query(doc.d, "#d");

    // From nothing, Tab lands on the first.
    CHECK(weva_document_focus_next(doc.d, 0) == a);
    CHECK(weva_document_focus_next(doc.d, 0) == b);
    // The plain div is skipped, and so is tabindex="-1".
    CHECK(weva_document_focus_next(doc.d, 0) == d);
    // And it wraps.
    CHECK(weva_document_focus_next(doc.d, 0) == a);

    // Backwards.
    CHECK(weva_document_focus_next(doc.d, 1) == d);
    CHECK(weva_document_focus_next(doc.d, 1) == b);

    // tabindex="-1" is still focusable deliberately, which is the whole point
    // of the distinction.
    CHECK(weva_document_set_focus(doc.d, c) == WEVA_OK);
}

void test_abi_tabindex_order() {
    // HTML's rule, and not the one most people expect: POSITIVE tabindex comes
    // first in numeric order, then everything else in document order.
    Doc doc("html, body { margin: 0 } button { display: block }",
            "<button id=a>a</button>"
            "<button id=b tabindex=2>b</button>"
            "<button id=c tabindex=1>c</button>"
            "<button id=d>d</button>");
    const weva_element_t a = weva_document_query(doc.d, "#a");
    const weva_element_t b = weva_document_query(doc.d, "#b");
    const weva_element_t c = weva_document_query(doc.d, "#c");
    const weva_element_t d = weva_document_query(doc.d, "#d");

    CHECK(weva_document_focus_next(doc.d, 0) == c);   // tabindex 1
    CHECK(weva_document_focus_next(doc.d, 0) == b);   // tabindex 2
    CHECK(weva_document_focus_next(doc.d, 0) == a);   // then document order
    CHECK(weva_document_focus_next(doc.d, 0) == d);
}

void test_abi_keyboard_events() {
    Doc doc("html, body { margin: 0 } button { display: block }",
            "<button id=a>a</button><button id=b>b</button>");
    const weva_element_t a = weva_document_query(doc.d, "#a");
    doc.drain();

    // Tab is consumed by the engine, because focus order is the document's to
    // know. The host is told so, and does not act on it twice.
    CHECK(weva_document_key(doc.d, WEVA_KEY_TAB, 0, 1) == 1);
    std::vector<weva_event> events = doc.drain();
    bool saw_key = false, saw_focus = false;
    for (const weva_event& e : events) {
        if (e.kind == WEVA_EVENT_KEY_DOWN && e.key == WEVA_KEY_TAB) saw_key = true;
        if (e.kind == WEVA_EVENT_FOCUS && e.target == a) saw_focus = true;
    }
    CHECK(saw_key);
    CHECK(saw_focus);

    // Unrecognized keys are passed through, not consumed.
    CHECK(weva_document_key(doc.d, WEVA_KEY_OTHER, 0, 1) == 0);
    events = doc.drain();
    CHECK(events.size() == 1);
    CHECK(events[0].kind == WEVA_EVENT_KEY_DOWN);
    CHECK(events[0].key == WEVA_KEY_OTHER);
    // It is addressed to whatever has focus, which is what makes it actionable.
    CHECK(events[0].target == a);

    // Modifiers travel with it.
    CHECK(weva_document_key(doc.d, WEVA_KEY_OTHER, WEVA_MOD_SHIFT | WEVA_MOD_CTRL, 0) == 0);
    events = doc.drain();
    CHECK(events.size() == 1);
    CHECK(events[0].kind == WEVA_EVENT_KEY_UP);
    CHECK((events[0].modifiers & WEVA_MOD_SHIFT) != 0);
    CHECK((events[0].modifiers & WEVA_MOD_CTRL) != 0);

    // Ctrl+Tab is the browser's own gesture, not a focus move, so the engine
    // leaves it alone.
    const weva_element_t before = weva_document_focus_next(doc.d, 0);
    CHECK(weva_document_key(doc.d, WEVA_KEY_TAB, WEVA_MOD_CTRL, 1) == 0);
    doc.drain();
    CHECK(weva_document_focus_next(doc.d, 1) != before ||
          weva_document_query(doc.d, "#b") == before);
}

void test_abi_text_input() {
    Doc doc("html, body { margin: 0 } button { display: block }",
            "<button id=a>a</button>");
    weva_document_focus_next(doc.d, 0);
    doc.drain();

    weva_document_text_input(doc.d, "x");
    std::vector<weva_event> events = doc.drain();
    CHECK(events.size() == 1);
    CHECK(events[0].kind == WEVA_EVENT_TEXT_INPUT);
    CHECK(std::string(events[0].text) == "x");
    CHECK(events[0].target == weva_document_query(doc.d, "#a"));

    // Multi-byte UTF-8 survives, since a host hands over characters and not
    // bytes it has decoded.
    weva_document_text_input(doc.d, "\xc3\xa9");
    events = doc.drain();
    CHECK(events.size() == 1);
    CHECK(std::string(events[0].text) == "\xc3\xa9");

    // Nothing is not an event.
    weva_document_text_input(doc.d, "");
    CHECK(doc.drain().empty());
}

void test_abi_keyboard_activation() {
    // Chrome 151: buttons activate on each Enter down, or once on Space up.
    // Keyboard activation is a click, with no synthesized pointer events.
    for (const char* html : {"<button id=go>Go</button>", "<input id=go type=button>",
                             "<input id=go type=submit>",
                             "<details><summary id=go>Go</summary></details>"}) {
        Doc doc("", html);
        const auto go = weva_document_query(doc.d, "#go");
        CHECK(weva_document_focus_next(doc.d, 0) == go);
        doc.drain();
        for (int i = 0; i < 2; ++i) {
            CHECK(weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1) == 1);
            const auto events = doc.drain();
            int clicks = 0, pointers = 0;
            for (const auto& e : events) {
                if (e.kind == WEVA_EVENT_CLICK) { ++clicks; CHECK(e.target == go); }
                if (e.kind == WEVA_EVENT_POINTER_DOWN || e.kind == WEVA_EVENT_POINTER_UP) ++pointers;
            }
            CHECK(clicks == 1 && pointers == 0);
        }
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        CHECK(doc.drain().size() == 1);
        for (int i = 0; i < 2; ++i) {
            CHECK(weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 1) == 1);
            CHECK(doc.drain().size() == 1);
        }
        // Moving the mouse away must not cancel a keyboard press.
        weva_document_clear_pointer(doc.d);
        CHECK(weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 0) == 1);
        int clicks = 0;
        for (const auto& e : doc.drain()) if (e.kind == WEVA_EVENT_CLICK) ++clicks;
        CHECK(clicks == 1);
        weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 0);
        CHECK(doc.drain().size() == 1); // a second release is not a click
    }
    Doc doc("#covered{pointer-events:none}",
            "<input id=check type=checkbox><input id=radio type=radio>"
            "<button id=covered>Go</button><input id=other><a id=link href=''>Link</a>"
            "<div id=custom role=button tabindex=0>Custom</div>");
    const auto activate = [&](const char* selector, int key) {
        weva_document_set_focus(doc.d, weva_document_query(doc.d, selector));
        doc.drain();
        weva_document_key(doc.d, key, 0, 1);
        weva_document_key(doc.d, key, 0, 0);
        return doc.drain();
    };
    auto events = activate("#check", WEVA_KEY_SPACE);
    std::vector<int> kinds;
    for (const auto& e : events) kinds.push_back(e.kind);
    CHECK((kinds == std::vector<int>{WEVA_EVENT_KEY_DOWN, WEVA_EVENT_KEY_UP, WEVA_EVENT_CLICK,
                                    WEVA_EVENT_VALUE_CHANGED, WEVA_EVENT_CHANGE}));
    CHECK(weva_document_query(doc.d, "#check:checked") != WEVA_ELEMENT_NONE);
    activate("#check", WEVA_KEY_ENTER);
    CHECK(weva_document_query(doc.d, "#check:checked") != WEVA_ELEMENT_NONE);
    activate("#check", WEVA_KEY_SPACE);
    CHECK(weva_document_query(doc.d, "#check:checked") == WEVA_ELEMENT_NONE);
    activate("#radio", WEVA_KEY_SPACE);
    CHECK(weva_document_query(doc.d, "#radio:checked") != WEVA_ELEMENT_NONE);
    events = activate("#radio", WEVA_KEY_SPACE);
    CHECK(events.size() == 3); // checked radio clicks again without input/change
    CHECK(activate("#covered", WEVA_KEY_ENTER).size() == 3); // pointer-events does not disable keys
    CHECK(activate("#custom", WEVA_KEY_ENTER).size() == 2); // ARIA does not invent native behavior
    CHECK(activate("#link", WEVA_KEY_SPACE).size() == 2);
    CHECK(activate("#link", WEVA_KEY_ENTER).size() == 3);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    doc.drain();
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    CHECK(doc.drain().size() == 1); // link activation does not repeat
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);

    const auto go = weva_document_query(doc.d, "#covered");
    weva_document_set_focus(doc.d, go);
    weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 1);
    weva_document_set_focus(doc.d, weva_document_query(doc.d, "#other"));
    doc.drain();
    weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 0);
    CHECK(doc.drain().size() == 1); // blur cancels the pending click
    weva_document_set_focus(doc.d, go);
    weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 1);
    weva_element_set_attribute(doc.d, go, "disabled", "");
    doc.drain();
    weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 0);
    CHECK(doc.drain().size() == 1);
    weva_element_set_attribute(doc.d, go, "disabled", nullptr);
    weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 1);
    weva_element_remove(doc.d, go);
    doc.drain();
    weva_document_key(doc.d, WEVA_KEY_SPACE, 0, 0);
    CHECK(doc.drain().size() == 1);
}

void test_abi_implicit_submission() {
    Doc nested("", "<details id=d><summary><button id=b>Action</button><span id=t>More</span></summary></details>");
    weva_document_set_focus(nested.d, weva_document_query(nested.d, "#b"));
    weva_document_key(nested.d, WEVA_KEY_ENTER, 0, 1);
    CHECK(weva_document_query(nested.d, "#d[open]") == WEVA_ELEMENT_NONE);
    weva_document_key(nested.d, WEVA_KEY_SPACE, 0, 1);
    weva_document_key(nested.d, WEVA_KEY_SPACE, 0, 0);
    CHECK(weva_document_query(nested.d, "#d[open]") == WEVA_ELEMENT_NONE);
    double x=0, y=0, w=0, h=0;
    weva_element_bounds(nested.d, weva_document_query(nested.d, "#t"), &x, &y, &w, &h);
    weva_document_set_pointer(nested.d, x+w/2, y+h/2, 1);
    weva_document_set_pointer(nested.d, x+w/2, y+h/2, 0);
    CHECK(weva_document_query(nested.d, "#d[open]") != WEVA_ELEMENT_NONE);

    const auto check_submit = [](const char* html, const char* expected_button, bool expected_submit) {
        Doc doc("", html);
        const auto field = weva_document_query(doc.d, "#field");
        const auto form = weva_document_query(doc.d, "#form");
        weva_document_set_focus(doc.d, field);
        doc.drain();
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        const auto events = doc.drain();
        std::vector<int> kinds;
        for (const auto& e : events) kinds.push_back(e.kind);
        std::vector<int> expected{WEVA_EVENT_KEY_DOWN};
        if (expected_button) expected.push_back(WEVA_EVENT_CLICK);
        if (expected_submit) expected.push_back(WEVA_EVENT_SUBMIT);
        CHECK(kinds == expected);
        if (expected_button && events.size() > 1)
            CHECK(events[1].target == weva_document_query(doc.d, expected_button));
        if (expected_submit && !events.empty()) CHECK(events.back().target == form);
        CHECK(weva_document_focus(doc.d) == field);
    };
    check_submit("<form id=form><input id=field><button id=go>Go</button></form>", "#go", true);
    check_submit("<form id=form><input id=field><button disabled>Off</button><button>Next</button></form>", nullptr, false);
    check_submit("<form id=form><input id=field></form>", nullptr, true);
    check_submit("<form id=form><input id=field><input></form>", nullptr, false);
    check_submit("<form id=form><input id=field><button type=button>Other</button></form>", nullptr, true);
    check_submit("<input id=field form=form><button id=go form=form>Go</button><form id=form></form>", "#go", true);
    check_submit("<form id=form><input id=field form=missing><button>Go</button></form>", nullptr, false);
    check_submit("<form id=form><input id=field><button id=go type=SUBMIT>Go</button></form>", "#go", true);
    check_submit("<form id=form><input id=field><button id=go type=unknown>Go</button></form>", "#go", true);
    check_submit("<form id=form><input id=field type=checkbox></form>", nullptr, false);
    check_submit("<form id=form><input id=field type=radio><button id=go>Go</button></form>", "#go", true);
    check_submit("<form id=form><input id=field><input disabled></form>", nullptr, false);
}

void test_abi_radio_keyboard_navigation() {
    const char* html = "<button id=before>B</button><form id=f>"
        "<input id=a type=radio name=g><input id=off type=radio name=g disabled>"
        "<input id=c type=radio name=g></form><form><input id=other type=radio name=g checked></form>"
        "<button id=after>A</button>";
    Doc doc("", html);
    const auto q = [&](const char* selector) { return weva_document_query(doc.d, selector); };
    weva_document_set_focus(doc.d, q("#before"));
    CHECK(weva_document_focus_next(doc.d, 0) == q("#a"));
    CHECK(weva_document_focus_next(doc.d, 0) == q("#other"));
    CHECK(weva_document_focus_next(doc.d, 0) == q("#after"));
    CHECK(weva_document_focus_next(doc.d, 1) == q("#other"));
    CHECK(weva_document_focus_next(doc.d, 1) == q("#a")); // remember the visited member
    doc.drain();
    CHECK(weva_document_key(doc.d, WEVA_KEY_RIGHT, 0, 1) == 1);
    CHECK(weva_document_focus(doc.d) == q("#c"));
    CHECK(q("#c:checked") != WEVA_ELEMENT_NONE && q("#other:checked") != WEVA_ELEMENT_NONE);
    int clicks=0, inputs=0, changes=0;
    for (const auto& e : doc.drain()) {
        if (e.kind == WEVA_EVENT_CLICK) { ++clicks; CHECK(e.target == q("#c")); }
        if (e.kind == WEVA_EVENT_VALUE_CHANGED) ++inputs;
        if (e.kind == WEVA_EVENT_CHANGE) ++changes;
    }
    CHECK(clicks == 1 && inputs == 1 && changes == 1);
    CHECK(weva_document_key(doc.d, WEVA_KEY_RIGHT, 0, 1) == 1);
    CHECK(weva_document_focus(doc.d) == q("#a")); // wraps past the disabled member
    CHECK(q("#a:checked") != WEVA_ELEMENT_NONE && q("#c:checked") == WEVA_ELEMENT_NONE);
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_CTRL, 1);
    CHECK(weva_document_focus(doc.d) == q("#a"));
    CHECK(weva_document_focus_next(doc.d, 0) == q("#other"));
    weva_document_set_focus(doc.d, q("#c")); // a nonchecked member still leaves the group on Tab
    CHECK(weva_document_focus_next(doc.d, 0) == q("#other"));
    Doc fresh("", html);
    weva_document_set_focus(fresh.d, weva_document_query(fresh.d, "#after"));
    weva_document_focus_next(fresh.d, 1);
    CHECK(weva_document_focus_next(fresh.d, 1) == weva_document_query(fresh.d, "#c"));
    Doc unnamed("", "<input id=a type=radio checked><input id=b type=radio>");
    weva_document_set_focus(unnamed.d, weva_document_query(unnamed.d, "#b"));
    weva_document_key(unnamed.d, WEVA_KEY_SPACE, 0, 1);
    weva_document_key(unnamed.d, WEVA_KEY_SPACE, 0, 0);
    CHECK(weva_document_query(unnamed.d, "#a:checked") != WEVA_ELEMENT_NONE);
    CHECK(weva_document_query(unnamed.d, "#b:checked") != WEVA_ELEMENT_NONE);
    Doc external("", "<form id=f><input id=a type=radio name=g checked></form>"
                     "<input id=b type=radio name=g form=f>");
    weva_document_set_focus(external.d, weva_document_query(external.d, "#a"));
    weva_document_key(external.d, WEVA_KEY_RIGHT, 0, 1);
    CHECK(weva_document_query(external.d, "#a:checked") == WEVA_ELEMENT_NONE);
    CHECK(weva_document_query(external.d, "#b:checked") != WEVA_ELEMENT_NONE);
}

void test_abi_range_keyboard() {
    const int keys[] = {WEVA_KEY_RIGHT, WEVA_KEY_LEFT, WEVA_KEY_UP, WEVA_KEY_DOWN,
                       WEVA_KEY_HOME, WEVA_KEY_END, WEVA_KEY_PAGE_UP, WEVA_KEY_PAGE_DOWN};
    struct Case { const char* attributes; const char* before; const char* after[8]; };
    // Values captured from Chrome 151, including sanitization and step=any.
    const Case cases[] = {
        {"", "50", {"51","49","51","49","0","100","60","40"}},
        {"min=0 max=10 step=0.3 value=5.1", "5.1", {"5.4","4.8","5.4","4.8","0","9.9","6","4.2"}},
        {"min=2 max=7 step=any value=4", "4", {"4.05","3.95","4.05","3.95","2","7","4.5","3.5"}},
        {"min=10 max=5 value=8", "10", {"10","10","10","10","10","10","10","10"}},
        {"min=0 max=10 step=3 value=5", "6", {"9","3","9","3","0","9","9","3"}}
    };
    for (const auto& test : cases) for (int i=0; i<8; ++i) {
        const auto html = std::string("<input id=r type=range ") + test.attributes + ">";
        Doc doc("", html.c_str());
        const auto r = weva_document_query(doc.d, "#r");
        char value[64]{};
        weva_element_value(doc.d, r, value, sizeof(value));
        CHECK_EQ(std::string(value), test.before);
        weva_document_set_focus(doc.d, r);
        doc.drain();
        CHECK(weva_document_key(doc.d, keys[i], 0, 1) == 1);
        weva_element_value(doc.d, r, value, sizeof(value));
        CHECK_EQ(std::string(value), test.after[i]);
        std::vector<int> kinds;
        for (const auto& e : doc.drain()) kinds.push_back(e.kind);
        const bool changed = std::strcmp(test.before, test.after[i]) != 0;
        CHECK((kinds == (changed ? std::vector<int>{WEVA_EVENT_KEY_DOWN, WEVA_EVENT_VALUE_CHANGED, WEVA_EVENT_CHANGE}
                                  : std::vector<int>{WEVA_EVENT_KEY_DOWN})));
        CHECK(weva_document_key(doc.d, keys[i], 0, 0) == 1);
        CHECK(doc.drain().size() == 1);
    }
}
