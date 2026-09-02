// CSS transitions, driven the way a host drives them: hover something, then
// hand the document time.
//
// The transition-* properties have been registered since the port began, so
// they cascaded and read back and did nothing at all. What makes them work is
// that the incremental update already knows exactly which properties changed on
// which element -- so a transition starts at the one moment when the value on
// screen and the value the cascade just produced are both in hand.
#include "check.h"
#include "weva_c.h"

#include <cmath>
#include <cstring>
#include <string>
#include <vector>

namespace {

weva_config config(int w = 400, int h = 300) {
    weva_config c{};
    c.viewport_width = w;
    c.viewport_height = h;
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

    double width_of(const char* selector) {
        double x = 0, y = 0, w = 0, h = 0;
        const weva_element_t e = weva_document_query(d, selector);
        if (e == WEVA_ELEMENT_NONE) return -1;
        if (weva_element_bounds(d, e, &x, &y, &w, &h) != WEVA_OK) return -1;
        return w;
    }
};

const char* kHtml = "<div id=a>x</div>";

}   // namespace

void test_abi_transition_runs() {
    // ---- a width transition, from a :hover rule
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: width 1s linear }"
                "#a:hover { width: 300px }",
                kHtml);
        CHECK(doc.width_of("#a") == 100);

        // Hovering sets the target. The FIRST frame must not jump there --
        // that is the whole bug a transition exists to prevent.
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        CHECK(doc.width_of("#a") == 100);

        // Half a second of a one-second linear transition is halfway.
        weva_document_update(doc.d, 0.5);
        const double mid = doc.width_of("#a");
        CHECK(mid > 190 && mid < 210);

        // Three quarters.
        weva_document_update(doc.d, 0.25);
        const double late = doc.width_of("#a");
        CHECK(late > mid);
        CHECK(late > 240 && late < 260);

        // And it LANDS on the declared value, exactly. A transition that stops
        // near its target leaves the document subtly wrong for good.
        weva_document_update(doc.d, 0.5);
        CHECK(doc.width_of("#a") == 300);

        // Once arrived, further time changes nothing.
        weva_document_update(doc.d, 1.0);
        CHECK(doc.width_of("#a") == 300);
    }

    // ---- unhovering transitions back
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: width 1s linear }"
                "#a:hover { width: 300px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, 1.0);
        CHECK(doc.width_of("#a") == 300);

        weva_document_clear_pointer(doc.d);
        weva_document_update(doc.d, 0);
        CHECK(doc.width_of("#a") == 300);       // still there on the frame it left
        weva_document_update(doc.d, 0.5);
        const double back = doc.width_of("#a");
        CHECK(back > 190 && back < 210);
        weva_document_update(doc.d, 0.5);
        CHECK(doc.width_of("#a") == 100);
    }

    // ---- reversing mid-flight carries on from where it is
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: width 1s linear }"
                "#a:hover { width: 300px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, 0.5);
        const double mid = doc.width_of("#a");
        CHECK(mid > 190 && mid < 210);

        // Leave halfway. It must go back from HERE, not snap to 300 first and
        // not restart from 100.
        weva_document_clear_pointer(doc.d);
        weva_document_update(doc.d, 0);
        CHECK(std::fabs(doc.width_of("#a") - mid) < 1.0);
        weva_document_update(doc.d, 0.5);
        const double halfway_back = doc.width_of("#a");
        CHECK(halfway_back < mid);
        CHECK(halfway_back > 140 && halfway_back < 160);
    }

    // ---- a delay holds the start value
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: width 1s linear 0.5s }"
                "#a:hover { width: 300px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, 0.4);
        CHECK(doc.width_of("#a") == 100);       // still waiting
        weva_document_update(doc.d, 0.6);       // 0.5s of the run done
        const double mid = doc.width_of("#a");
        CHECK(mid > 190 && mid < 210);
    }

    // ---- with no transition declared, a change is immediate
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222 }"
                "#a:hover { width: 300px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        CHECK(doc.width_of("#a") == 300);
    }

    // ---- a property the transition list does not name is immediate
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: background-color 1s linear }"
                "#a:hover { width: 300px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        CHECK(doc.width_of("#a") == 300);
    }

    // ---- `all` names everything
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: all 1s linear }"
                "#a:hover { width: 300px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, 0.5);
        const double mid = doc.width_of("#a");
        CHECK(mid > 190 && mid < 210);
    }
}

// The clock is the only thing that moved, so the document must still redraw --
// and must not restyle to find that out.
void test_abi_transition_costs_nothing_when_idle() {
    Doc doc("html, body { margin: 0 }"
            "#a { width: 100px; height: 40px; background: #222;"
            "     transition: width 1s linear }"
            "#a:hover { width: 300px }",
            kHtml);
    // Nothing is animating: time passes and the frame stands.
    size_t before = 0;
    weva_document_draws(doc.d, &before);
    weva_document_update(doc.d, 0.5);
    weva_document_update(doc.d, 0.5);
    size_t after = 0;
    weva_document_draws(doc.d, &after);
    CHECK(before == after);
    CHECK(after > 0);
    CHECK(doc.width_of("#a") == 100);

    // Now one IS running, so the same call has to move it.
    weva_document_set_pointer(doc.d, 50, 20, 0);
    weva_document_update(doc.d, 0);
    weva_document_update(doc.d, 0.25);
    const double a = doc.width_of("#a");
    weva_document_update(doc.d, 0.25);
    const double b = doc.width_of("#a");
    CHECK(b > a);
}

// A shorthand with several entries, which is how a stylesheet actually writes
// this, and the ragged-list rule that lets one duration serve two properties.
void test_transition_shorthand_lists() {
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: width 1s linear, height 2s linear }"
                "#a:hover { width: 300px; height: 140px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, 1.0);
        // Width is done at one second; height is halfway through two.
        CHECK(doc.width_of("#a") == 300);
        double x = 0, y = 0, w = 0, h = 0;
        weva_element_bounds(doc.d, weva_document_query(doc.d, "#a"), &x, &y, &w, &h);
        CHECK(h > 85 && h < 95);
    }
    {
        // One duration, two properties: the list repeats its last entry.
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition-property: width, height;"
                "     transition-duration: 1s;"
                "     transition-timing-function: linear }"
                "#a:hover { width: 300px; height: 140px }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 20, 0);
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, 0.5);
        double x = 0, y = 0, w = 0, h = 0;
        weva_element_bounds(doc.d, weva_document_query(doc.d, "#a"), &x, &y, &w, &h);
        CHECK(w > 190 && w < 210);
        CHECK(h > 85 && h < 95);
    }
}
