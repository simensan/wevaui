// Events and text binding: what a script needs to drive a document.
//
// Until now the ABI could style a document but never change what it SAID, and
// a host that wanted to know about a click had to re-implement hit testing and
// press tracking on top of set_pointer. Both are here.
#include "check.h"
#include "weva_c.h"

#include <cstring>
#include <tuple>
#include <utility>
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

// `change` is not `input`. A search box wants to run once, when the user is
// done, not once per keystroke -- and nothing but the focus leaving can tell
// the engine that the user is done.
void test_abi_change_event() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 } input { display: block; width: 120px }";
    const char* html = "<input id=a type=text value='' on-change=OnSearch>"
                       "<input id=b type=text value=''>"
                       "<input id=c type=checkbox on-change=OnToggle>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    const auto drain = [&](int kind) {
        std::vector<std::string> handlers;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) {
            if (e.kind == kind) handlers.push_back(e.handler);
        }
        return handlers;
    };

    // Typing raises `input` every keystroke and `change` not at all.
    weva_document_set_focus(d, weva_document_query(d, "#a"));
    drain(WEVA_EVENT_CHANGE);
    weva_document_text_input(d, "h");
    weva_document_text_input(d, "i");
    CHECK(drain(WEVA_EVENT_CHANGE).empty());

    // The focus leaving commits it, once.
    weva_document_set_focus(d, weva_document_query(d, "#b"));
    const std::vector<std::string> committed = drain(WEVA_EVENT_CHANGE);
    CHECK(committed.size() == 1);
    CHECK(committed[0] == "OnSearch");

    // Visiting a field and leaving it unchanged commits nothing: `change`
    // means the value moved, not that the user passed through.
    weva_document_set_focus(d, weva_document_query(d, "#a"));
    weva_document_set_focus(d, weva_document_query(d, "#b"));
    CHECK(drain(WEVA_EVENT_CHANGE).empty());

    // A checkbox has no editing state to leave, so it commits the moment it
    // changes -- `input` and `change` are the same instant for it.
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(d, weva_document_query(d, "#c"), &x, &y, &w, &h);
    weva_document_set_pointer(d, x + w / 2, y + h / 2, 1);
    weva_document_set_pointer(d, x + w / 2, y + h / 2, 0);
    weva_document_update(d, 0);
    const std::vector<std::string> toggled = drain(WEVA_EVENT_CHANGE);
    CHECK(toggled.size() == 1);
    CHECK(toggled[0] == "OnToggle");
    weva_document_destroy(d);
}

// A form submits, from the keyboard or from a button, and reports itself
// rather than whatever was pressed.
void test_abi_submit_event() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 } input, button { display: block; width: 120px;"
                      " height: 24px }";
    const char* html = "<form id=login on-submit=OnLogin>"
                       "<input id=user type=text value=vintner>"
                       "<button id=go>Sign in</button>"
                       "</form>"
                       "<input id=loose type=text value=x>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    const auto submits = [&]() {
        int n = 0;
        std::string handler;
        weva_element_t target = WEVA_ELEMENT_NONE;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) {
            if (e.kind != WEVA_EVENT_SUBMIT) continue;
            ++n;
            handler = e.handler;
            target = e.target;
        }
        return std::make_tuple(n, handler, target);
    };

    // Enter in a field inside the form.
    weva_document_set_focus(d, weva_document_query(d, "#user"));
    CHECK(weva_document_key(d, WEVA_KEY_ENTER, 0, 1) == 1);
    auto [count, handler, target] = submits();
    CHECK(count == 1);
    CHECK(handler == "OnLogin");
    CHECK(target == weva_document_query(d, "#login"));   // the FORM, not the field

    // And a click on the button in it.
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(d, weva_document_query(d, "#go"), &x, &y, &w, &h);
    weva_document_set_pointer(d, x + w / 2, y + h / 2, 1);
    weva_document_set_pointer(d, x + w / 2, y + h / 2, 0);
    weva_document_update(d, 0);
    auto [clicked, click_handler, click_target] = submits();
    CHECK(clicked == 1);
    CHECK(click_handler == "OnLogin");
    CHECK(click_target == weva_document_query(d, "#login"));

    // Enter in a field with no form around it submits nothing.
    weva_document_set_focus(d, weva_document_query(d, "#loose"));
    weva_document_key(d, WEVA_KEY_ENTER, 0, 1);
    CHECK(std::get<0>(submits()) == 0);
    weva_document_destroy(d);
}

// Scrolling is an event, however it was caused -- a wheel, a bar, the
// keyboard, or a script. A list that loads more when it reaches the bottom
// needs to hear about all four.
void test_abi_scroll_event() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 }"
                      ".list { width: 200px; height: 60px; overflow: auto }"
                      ".row { height: 40px }";
    const char* html = "<div id=list class=list on-scroll=OnScrolled>"
                       "<div class=row></div><div class=row></div>"
                       "<div class=row></div></div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    const auto scrolls = [&]() {
        std::vector<std::pair<std::string, double>> out;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) {
            if (e.kind == WEVA_EVENT_SCROLL) out.emplace_back(e.handler, e.y);
        }
        return out;
    };

    CHECK(weva_document_scroll(d, 100, 30, 0, 20) == 1);
    const auto wheeled = scrolls();
    CHECK(wheeled.size() == 1);
    CHECK(wheeled[0].first == "OnScrolled");
    CHECK(wheeled[0].second == 20);   // where it scrolled TO

    // A script moving it says so too, so a host that drives the scroll sees
    // the same events as one that lets the user.
    weva_element_set_scroll(d, weva_document_query(d, "#list"), 0, 45);
    weva_document_update(d, 0);   // offsets are applied by the update
    const auto scripted = scrolls();
    CHECK(scripted.size() == 1);
    CHECK(scripted[0].second == 45);

    // A wheel that moves nothing raises nothing.
    weva_document_scroll(d, 100, 30, 0, 1000);
    weva_document_update(d, 0);
    scrolls();
    CHECK(weva_document_scroll(d, 100, 30, 0, 10) == 0);
    CHECK(scrolls().empty());
    weva_document_destroy(d);
}
