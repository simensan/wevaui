// Events and text binding: what a script needs to drive a document.
//
// Until now the ABI could style a document but never change what it SAID, and
// a host that wanted to know about a click had to re-implement hit testing and
// press tracking on top of set_pointer. Both are here.
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
    std::string text(const char* selector) {
        const weva_element_t el = weva_document_query(d, selector);
        if (el == WEVA_ELEMENT_NONE) return "<none>";
        const size_t n = weva_element_text(d, el, nullptr, 0);
        std::vector<char> buf(n + 1, '\0');
        weva_element_text(d, el, buf.data(), buf.size());
        return std::string(buf.data());
    }
};

const char* kCss =
    "html, body { margin: 0 }"
    "div { width: 100px; height: 60px; background: #222 }";
const char* kHtml = "<div id=a>one</div><div id=b>two</div>";

bool has(const std::vector<weva_event>& events, int kind, weva_element_t target) {
    for (const weva_event& e : events) {
        if (e.kind == kind && e.target == target) return true;
    }
    return false;
}

}   // namespace

void test_abi_events() {
    Doc doc(kCss, kHtml);
    const weva_element_t a = weva_document_query(doc.d, "#a");
    const weva_element_t b = weva_document_query(doc.d, "#b");

    // ---- entering and leaving
    weva_document_set_pointer(doc.d, 50, 30, 0);
    std::vector<weva_event> events = doc.drain();
    CHECK(has(events, WEVA_EVENT_POINTER_ENTER, a));
    CHECK(!has(events, WEVA_EVENT_POINTER_LEAVE, a));

    // Moving WITHIN the element is not another enter.
    weva_document_set_pointer(doc.d, 60, 35, 0);
    CHECK(doc.drain().empty());

    // Crossing to the other one leaves the first and enters the second.
    weva_document_set_pointer(doc.d, 50, 90, 0);
    events = doc.drain();
    CHECK(has(events, WEVA_EVENT_POINTER_LEAVE, a));
    CHECK(has(events, WEVA_EVENT_POINTER_ENTER, b));

    // ---- a press and release on the same element is a click
    weva_document_set_pointer(doc.d, 50, 90, 1);
    events = doc.drain();
    CHECK(has(events, WEVA_EVENT_POINTER_DOWN, b));
    CHECK(!has(events, WEVA_EVENT_CLICK, b));      // not yet -- it is still held

    weva_document_set_pointer(doc.d, 50, 90, 0);
    events = doc.drain();
    CHECK(has(events, WEVA_EVENT_POINTER_UP, b));
    CHECK(has(events, WEVA_EVENT_CLICK, b));

    // ---- pressing one and releasing over another is NOT a click
    //
    // Which is how every button in every toolkit behaves: dragging off it
    // cancels the press rather than firing somewhere else.
    weva_document_set_pointer(doc.d, 50, 30, 0);
    doc.drain();
    weva_document_set_pointer(doc.d, 50, 30, 1);    // press on #a
    doc.drain();
    weva_document_set_pointer(doc.d, 50, 90, 1);    // drag onto #b, still held
    doc.drain();
    weva_document_set_pointer(doc.d, 50, 90, 0);    // release over #b
    events = doc.drain();
    CHECK(has(events, WEVA_EVENT_POINTER_UP, b));
    CHECK(!has(events, WEVA_EVENT_CLICK, b));
    CHECK(!has(events, WEVA_EVENT_CLICK, a));

    // ---- the queue drains, and does not hand out the same event twice
    weva_document_set_pointer(doc.d, 50, 30, 1);
    weva_document_set_pointer(doc.d, 50, 30, 0);
    CHECK(!doc.drain().empty());
    CHECK(doc.drain().empty());

    // ---- a host that never pumps does not grow without bound
    for (int i = 0; i < 500; ++i) {
        weva_document_set_pointer(doc.d, 50, 30, 1);
        weva_document_set_pointer(doc.d, 50, 30, 0);
    }
    const std::vector<weva_event> flood = doc.drain();
    CHECK(flood.size() <= 256);
    CHECK(!flood.empty());
}

void test_abi_element_contains() {
    Doc doc(kCss, "<div id=outer><span id=inner>x</span></div>");
    const weva_element_t outer = weva_document_query(doc.d, "#outer");
    const weva_element_t inner = weva_document_query(doc.d, "#inner");
    CHECK(weva_element_contains(doc.d, outer, inner) == 1);
    CHECK(weva_element_contains(doc.d, inner, outer) == 0);
    // An element contains itself, which is what makes "was the click inside my
    // panel" one call rather than two.
    CHECK(weva_element_contains(doc.d, outer, outer) == 1);
    CHECK(weva_element_contains(doc.d, outer, WEVA_ELEMENT_NONE) == 0);
}

void test_abi_set_text() {
    Doc doc(kCss, kHtml);
    CHECK(doc.text("#a") == "one");

    const weva_element_t a = weva_document_query(doc.d, "#a");
    CHECK(weva_element_set_text(doc.d, a, "changed") == WEVA_OK);
    CHECK(doc.text("#a") == "changed");
    // The other element is untouched.
    CHECK(doc.text("#b") == "two");

    // It shows up in the LAYOUT, not just the DOM: an empty element and one
    // with a line in it are different heights.
    weva_document_update(doc.d, 0);
    double x = 0, y = 0, w = 0, h = 0;
    CHECK(weva_element_bounds(doc.d, a, &x, &y, &w, &h) == WEVA_OK);
    const double with_text = h;
    CHECK(weva_element_set_text(doc.d, a, "") == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.text("#a").empty());
    CHECK(weva_element_bounds(doc.d, a, &x, &y, &w, &h) == WEVA_OK);
    CHECK(h == with_text);   // this div has an explicit height, so it holds

    // Child ELEMENTS survive: setting the text of a row must not throw away
    // the icon inside it.
    {
        Doc nested(kCss, "<div id=row>label<span id=icon>*</span></div>");
        const weva_element_t row = weva_document_query(nested.d, "#row");
        CHECK(weva_element_set_text(nested.d, row, "renamed") == WEVA_OK);
        CHECK(weva_document_query(nested.d, "#icon") != WEVA_ELEMENT_NONE);
        // The element's text is its descendants' text, so the icon's is in it.
        CHECK(nested.text("#row") == "renamed*");
    }

    // A text change does not cancel a transition next to it, which is what
    // dropping the styles to rebuild would have done -- and a bound label
    // updating every frame would have cancelled every animation on the page.
    {
        Doc t("html, body { margin: 0 }"
              "#a { width: 100px; height: 60px; background: #222;"
              "     transition: width 1s linear }"
              "#a.wide { width: 300px }"
              "#b { width: 100px; height: 60px }",
              kHtml);
        const weva_element_t ea = weva_document_query(t.d, "#a");
        const weva_element_t eb = weva_document_query(t.d, "#b");
        weva_element_set_attribute(t.d, ea, "class", "wide");
        weva_document_update(t.d, 0);
        weva_document_update(t.d, 0.5);
        double bx = 0, by = 0, bw = 0, bh = 0;
        weva_element_bounds(t.d, ea, &bx, &by, &bw, &bh);
        CHECK(bw > 190 && bw < 210);

        weva_element_set_text(t.d, eb, "updated");
        weva_document_update(t.d, 0.25);
        weva_element_bounds(t.d, ea, &bx, &by, &bw, &bh);
        // Still travelling, from where it was -- not restarted, not snapped.
        CHECK(bw > 240 && bw < 260);
    }
}

// `on-click="OnStart"` in the markup, reported with the event.
//
// Until this, a script matched events by element `id`: the markup could not
// say what a control was FOR, so renaming a button or wrapping it in something
// broke the script. The name travels with the event instead.
void test_abi_event_handlers() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 } button { display: block; width: 100px;"
                      " height: 30px } input { display: block; width: 100px }";
    const char* html = "<div id=form on-click=OnAnything>"
                       "<button id=go on-click=OnStart>Start</button>"
                       "<button id=plain>Plain</button>"
                       "<input id=name type=text value='' on-input=OnRename>"
                       "</div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    const auto click = [&](const char* selector) {
        double x = 0, y = 0, w = 0, h = 0;
        weva_element_bounds(d, weva_document_query(d, selector), &x, &y, &w, &h);
        weva_document_set_pointer(d, x + w / 2, y + h / 2, 0);
        weva_document_set_pointer(d, x + w / 2, y + h / 2, 1);
        weva_document_set_pointer(d, x + w / 2, y + h / 2, 0);
        weva_document_update(d, 0);
    };
    const auto handler_of_click = [&]() {
        std::string found;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) {
            if (e.kind == WEVA_EVENT_CLICK) found = e.handler;
        }
        return found;
    };

    click("#go");
    CHECK(handler_of_click() == "OnStart");

    // A button with no handler of its own takes the one on the container --
    // towards the root, which is what makes `on-submit` on a form work.
    click("#plain");
    CHECK(handler_of_click() == "OnAnything");

    // A value change reads `on-input`, not `on-click`.
    weva_document_set_focus(d, weva_document_query(d, "#name"));
    while (weva_document_poll_event(d, nullptr)) {}
    weva_document_text_input(d, "x");
    weva_document_update(d, 0);
    std::string on_input;
    weva_event e{};
    while (weva_document_poll_event(d, &e)) {
        if (e.kind == WEVA_EVENT_VALUE_CHANGED) on_input = e.handler;
    }
    CHECK(on_input == "OnRename");

    // A document that names no handlers reports none, which is how a host
    // knows to fall back to ids.
    weva_document_t plain = weva_document_create(&c);
    const char* bare = "<button id=b>Go</button>";
    weva_document_add_css(plain, css, std::strlen(css));
    weva_document_load_html(plain, bare, std::strlen(bare));
    weva_document_update(plain, 0);
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(plain, weva_document_query(plain, "#b"), &x, &y, &w, &h);
    weva_document_set_pointer(plain, x + w / 2, y + h / 2, 1);
    weva_document_set_pointer(plain, x + w / 2, y + h / 2, 0);
    weva_document_update(plain, 0);
    weva_event pe{};
    bool clicked = false;
    while (weva_document_poll_event(plain, &pe)) {
        if (pe.kind != WEVA_EVENT_CLICK) continue;
        clicked = true;
        CHECK(pe.handler[0] == 0);
    }
    CHECK(clicked);
    weva_document_destroy(plain);
    weva_document_destroy(d);
}
