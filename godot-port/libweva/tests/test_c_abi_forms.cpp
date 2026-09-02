// Form controls, driven the way a user drives them.
//
// The engine already PAINTED these from their attributes -- a checkbox from
// `checked`, a range from `value` -- so they looked like controls and behaved
// like pictures. What is tested here is the part a user does: clicking,
// dragging, typing. The state stays in the attributes, so a script sets it the
// way it reads it and a stylesheet can select on it.
#include "check.h"
#include "weva_c.h"

#include <cstdlib>
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
        const weva_element_t e = weva_document_query(d, selector);
        if (e == WEVA_ELEMENT_NONE) return "<none>";
        char buf[256];
        weva_element_value(d, e, buf, sizeof(buf));
        return std::string(buf);
    }
    void click(double x, double y) {
        weva_document_set_pointer(d, x, y, 0);
        weva_document_set_pointer(d, x, y, 1);
        weva_document_set_pointer(d, x, y, 0);
        weva_document_update(d, 0);
    }
    std::vector<weva_event> drain() {
        std::vector<weva_event> out;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) out.push_back(e);
        return out;
    }
    void bounds(const char* selector, double* x, double* y, double* w, double* h) {
        weva_element_bounds(d, weva_document_query(d, selector), x, y, w, h);
    }
};

const char* kCss = "html, body { margin: 0 } input { display: block }";

}   // namespace

void test_abi_checkbox() {
    Doc doc(kCss, "<input id=a type=checkbox><input id=b type=checkbox checked>");
    CHECK(doc.value("#a").empty());
    CHECK(doc.value("#b") == "on");

    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#a", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#a") == "on");

    // And back off: a checkbox is a toggle.
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#a").empty());

    // The change reaches the host as an event, which is what a script binds to.
    doc.drain();
    doc.click(x + w / 2, y + h / 2);
    bool changed = false;
    for (const weva_event& e : doc.drain()) {
        if (e.kind == WEVA_EVENT_VALUE_CHANGED &&
            e.target == weva_document_query(doc.d, "#a")) {
            changed = true;
            CHECK(std::string(e.text) == "on");
        }
    }
    CHECK(changed);

    // `:checked` selects it, so a stylesheet can show the state.
    Doc styled("html, body { margin: 0 } input { display: block; width: 20px; height: 20px }"
               "input:checked { background: #ff0000 }",
               "<input id=a type=checkbox>");
    size_t before = 0;
    weva_document_draws(styled.d, &before);
    styled.bounds("#a", &x, &y, &w, &h);
    styled.click(x + w / 2, y + h / 2);
    size_t after = 0;
    weva_document_draws(styled.d, &after);
    CHECK(after != before);
}

void test_abi_radio_group() {
    Doc doc(kCss,
            "<input id=a type=radio name=g>"
            "<input id=b type=radio name=g>"
            "<input id=c type=radio name=other>");
    double x = 0, y = 0, w = 0, h = 0;

    doc.bounds("#a", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#a") == "on");

    // Choosing the other one in the SAME group turns the first off. That is
    // the whole behaviour of a radio group and nothing else provides it.
    doc.bounds("#b", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#b") == "on");
    CHECK(doc.value("#a").empty());

    // A different group is untouched.
    doc.bounds("#c", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#c") == "on");
    CHECK(doc.value("#b") == "on");

    // Clicking the one already chosen does NOT turn it off -- a radio can only
    // be unset by another in its group being set.
    doc.bounds("#b", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#b") == "on");
}

void test_abi_range_drag() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 20px }",
            "<input id=r type=range min=0 max=100 value=50>");
    CHECK(doc.value("#r") == "50");

    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#r", &x, &y, &w, &h);

    // Pressing takes effect immediately, before any release -- a slider jumps
    // to where you pressed it.
    weva_document_set_pointer(doc.d, x + w * 0.25, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    const double quarter = std::atof(doc.value("#r").c_str());
    CHECK(quarter > 20 && quarter < 30);

    // Still held, and moving: it follows.
    weva_document_set_pointer(doc.d, x + w * 0.75, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    const double three_quarters = std::atof(doc.value("#r").c_str());
    CHECK(three_quarters > 70 && three_quarters < 80);

    // Dragged past the end, it clamps rather than running off.
    weva_document_set_pointer(doc.d, x + w * 3, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#r") == "100");
    weva_document_set_pointer(doc.d, x - 500, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#r") == "0");

    // Released, it stops following.
    weva_document_set_pointer(doc.d, x + w * 0.5, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    const std::string settled = doc.value("#r");
    weva_document_set_pointer(doc.d, x + w * 0.9, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#r") == settled);
}

void test_abi_range_step_and_bounds() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 20px }",
            "<input id=r type=range min=0 max=10 step=5 value=0>");
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#r", &x, &y, &w, &h);
    // Just past the middle snaps to the step, not to the exact position.
    weva_document_set_pointer(doc.d, x + w * 0.55, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#r") == "5");
    weva_document_set_pointer(doc.d, x + w * 0.95, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#r") == "10");
}

void test_abi_text_field_editing() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 30px }",
            "<input id=t type=text value=''><input id=other type=checkbox>");
    const weva_element_t t = weva_document_query(doc.d, "#t");

    // Clicking a field focuses it, which is what makes typing land somewhere.
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#t", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);

    weva_document_text_input(doc.d, "h");
    weva_document_text_input(doc.d, "i");
    CHECK(doc.value("#t") == "hi");

    // Backspace removes a character and reports itself as consumed, since no
    // host can do it without knowing where the text is kept.
    CHECK(weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1) == 1);
    CHECK(doc.value("#t") == "h");

    // One CHARACTER, not one byte.
    weva_document_text_input(doc.d, "\xc3\xa9");
    CHECK(doc.value("#t") == "h\xc3\xa9");
    CHECK(weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1) == 1);
    CHECK(doc.value("#t") == "h");

    // Backspace on an empty field is still consumed and does not underflow.
    weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1);
    CHECK(doc.value("#t").empty());
    CHECK(weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1) == 1);
    CHECK(doc.value("#t").empty());

    // Typing with focus elsewhere does not reach the field.
    CHECK(weva_document_set_focus(doc.d, weva_document_query(doc.d, "#other")) == WEVA_OK);
    weva_document_text_input(doc.d, "z");
    CHECK(doc.value("#t").empty());

    // And the value shows up in the LAYOUT -- the field paints what it holds.
    CHECK(weva_element_set_value(doc.d, t, "typed") == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#t") == "typed");
}

void test_abi_set_value_round_trips() {
    Doc doc(kCss, "<input id=c type=checkbox><input id=t type=text value=start>");
    const weva_element_t c = weva_document_query(doc.d, "#c");
    const weva_element_t t = weva_document_query(doc.d, "#t");

    CHECK(doc.value("#t") == "start");
    CHECK(weva_element_set_value(doc.d, t, "changed") == WEVA_OK);
    CHECK(doc.value("#t") == "changed");

    // A checkbox takes the truthy strings a form would.
    CHECK(weva_element_set_value(doc.d, c, "on") == WEVA_OK);
    CHECK(doc.value("#c") == "on");
    CHECK(weva_element_set_value(doc.d, c, "") == WEVA_OK);
    CHECK(doc.value("#c").empty());

    // Setting a value raises no event: whoever set it already knows.
    doc.drain();
    weva_element_set_value(doc.d, t, "quiet");
    for (const weva_event& e : doc.drain()) {
        CHECK(e.kind != WEVA_EVENT_VALUE_CHANGED);
    }
}


// The form pseudo-classes, which the matcher reads off the state provider and
// which had nothing feeding them: its own comment said they were waiting for a
// forms layer. A stylesheet can now show a disabled control as disabled and
// float a label off an empty field.
//
// Measured through LAYOUT rather than through the draw count. Background and
// border share one decoration mesh, so recolouring a box changes its vertices
// and not the number of draws -- which is what the first version of this test
// got wrong, and it accused the engine of a bug it did not have.
void test_abi_form_pseudo_classes() {
    const char* css =
        "html, body { margin: 0 } input { display: block; width: 40px; height: 20px }"
        "input:disabled { width: 111px }"
        "input:checked { width: 222px }"
        "input:placeholder-shown { width: 333px }";
    const auto width = [&](const char* html) {
        Doc doc(css, html);
        double x = 0, y = 0, w = 0, h = 0;
        doc.bounds("#a", &x, &y, &w, &h);
        return w;
    };
    CHECK(width("<input id=a>") == 40);
    CHECK(width("<input id=a disabled>") == 111);
    CHECK(width("<input id=a type=checkbox checked>") == 222);
    CHECK(width("<input id=a placeholder=hint>") == 333);
    // A field with a value is no longer showing its placeholder, which is the
    // whole point of the pseudo-class -- it is how a floating label knows.
    CHECK(width("<input id=a placeholder=hint value=typed>") == 40);

    // And it follows editing, live.
    Doc doc(css, "<input id=a placeholder=hint>");
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#a", &x, &y, &w, &h);
    CHECK(w == 333);
    doc.click(x + 5, y + 5);
    weva_document_text_input(doc.d, "z");
    weva_document_update(doc.d, 0);
    doc.bounds("#a", &x, &y, &w, &h);
    CHECK(w == 40);

    // Toggling a checkbox restyles it through :checked, live.
    Doc box(css, "<input id=a type=checkbox>");
    box.bounds("#a", &x, &y, &w, &h);
    CHECK(w == 40);
    box.click(x + 5, y + 5);
    box.bounds("#a", &x, &y, &w, &h);
    CHECK(w == 222);
}
