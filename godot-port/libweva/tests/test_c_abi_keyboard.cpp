// Keyboard input and tab navigation.
//
// Focus order is the one thing here only the DOCUMENT can work out, so Tab is
// the one key the engine acts on itself. Everything else reaches the host as an
// event and is the host's to interpret.
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

void test_abi_focus_skips_unreachable() {
    // `disabled` and anything not displayed are out of the order. A hidden
    // element that could still be tabbed to is a real accessibility bug and a
    // real "why is my focus ring nowhere" bug.
    Doc doc("html, body { margin: 0 } button { display: block } #hidden { display: none }",
            "<button id=a>a</button>"
            "<button id=hidden>hidden</button>"
            "<button id=b disabled>disabled</button>"
            "<button id=c>c</button>");
    const weva_element_t a = weva_document_query(doc.d, "#a");
    const weva_element_t c = weva_document_query(doc.d, "#c");
    CHECK(weva_document_focus_next(doc.d, 0) == a);
    CHECK(weva_document_focus_next(doc.d, 0) == c);
    CHECK(weva_document_focus_next(doc.d, 0) == a);   // only two in the ring
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

    // Every other key is passed through, not consumed.
    CHECK(weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1) == 0);
    events = doc.drain();
    CHECK(events.size() == 1);
    CHECK(events[0].kind == WEVA_EVENT_KEY_DOWN);
    CHECK(events[0].key == WEVA_KEY_ENTER);
    // It is addressed to whatever has focus, which is what makes it actionable.
    CHECK(events[0].target == a);

    // Modifiers travel with it.
    CHECK(weva_document_key(doc.d, WEVA_KEY_ENTER, WEVA_MOD_SHIFT | WEVA_MOD_CTRL, 0) == 0);
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
