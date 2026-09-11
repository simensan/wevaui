// Form controls, driven the way a user drives them.
//
// The engine already PAINTED these from their attributes -- a checkbox from
// `checked`, a range from `value` -- so they looked like controls and behaved
// like pictures. What is tested here is the part a user does: clicking,
// dragging, typing. The state stays in the attributes, so a script sets it the
// way it reads it and a stylesheet can select on it.
#include "check.h"
#include "weva_c.h"

#include <cmath>
#include <cstdlib>
#include <utility>
#include <algorithm>
#include <cstring>
#include <tuple>
#include <string>
#include <string_view>
#include <vector>

namespace {

// Editing/scrolling scenarios that start at the end must request that position;
// programmatic focus alone preserves the initial zero selection.
void place_caret_at_end(weva_document_t doc, weva_element_t field) {
    const int end = static_cast<int>(weva_element_value(doc, field, nullptr, 0));
    CHECK(weva_element_set_selection(doc, field, end, end) == WEVA_OK);
}

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
    // How many viewport-filling half-transparent black rects the document
    // draws: the `::backdrop` behind a modal dialog or an open popover.
    // Counted from the DRAWS rather than from a box query, because a backdrop
    // has no element and `weva_document_query` can never find it -- and
    // because what matters is that it covers the viewport, not that a box
    // exists somewhere.
    int backdrops() {
        weva_document_update(d, 0);
        const weva_config c = config();
        const int viewport_w = c.viewport_width;
        const int viewport_h = c.viewport_height;
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        int found = 0;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].texture_id != 0 || draws[i].vertex_count == 0) continue;
            double min_x = 1e9, min_y = 1e9, max_x = -1e9, max_y = -1e9;
            bool dim = true;
            for (size_t v = 0; v < draws[i].vertex_count; ++v) {
                const weva_vertex& vt = draws[i].vertices[v];
                min_x = std::min(min_x, static_cast<double>(vt.x));
                min_y = std::min(min_y, static_cast<double>(vt.y));
                max_x = std::max(max_x, static_cast<double>(vt.x));
                max_y = std::max(max_y, static_cast<double>(vt.y));
                // Half-transparent black, which is what the UA sheet gives it.
                if (vt.r > 0.05f || vt.g > 0.05f || vt.b > 0.05f ||
                    vt.a < 0.4f || vt.a > 0.6f) {
                    dim = false;
                }
            }
            if (!dim) continue;
            if (min_x <= 0.5 && min_y <= 0.5 && max_x >= viewport_w - 0.5 &&
                max_y >= viewport_h - 0.5) {
                ++found;
            }
        }
        return found;
    }
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
    double height(const char* selector) {
        double x = 0, y = 0, w = 0, h = 0;
        if (weva_element_bounds(d, weva_document_query(d, selector), &x, &y, &w, &h) != WEVA_OK) {
            return -1;
        }
        return h;
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
    {
        Doc tall("html,body{margin:0}input{display:block;width:200px;height:40px}",
                 "<input id=r type=range min=0 max=100 value=50>");
        double x,y,w,h; tall.bounds("#r",&x,&y,&w,&h);
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(tall.d,&count);
        double top = 1e9, bottom = -1e9;
        for (size_t i=0;i<count;++i) for (size_t v=0;v<draws[i].vertex_count;++v) {
            top = std::min(top,static_cast<double>(draws[i].vertices[v].y));
            bottom = std::max(bottom,static_cast<double>(draws[i].vertices[v].y));
        }
        CHECK(std::fabs((top+bottom)/2-(y+h/2)) < 0.01);
        // The 14px thumb has a subpixel antialias fringe on either side.
        CHECK(bottom-top >= 14 && bottom-top < 16);
        // Empty space above the rail is still part of the input's hit area.
        weva_document_set_pointer(tall.d,x+w*.25,y+1,1);
        weva_document_update(tall.d,0);
        const double value = std::atof(tall.value("#r").c_str());
        CHECK(value > 20 && value < 30);
    }
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


// The caret: where typing lands, and what the field draws to show it.
//
// A field you can type into with no visible cursor reads as broken, and one
// that always appends is not a text field -- it is a log. Both are here.
void test_abi_caret() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 30px }",
            "<input id=t type=text value=abcd>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    CHECK(weva_document_set_focus(doc.d, t) == WEVA_OK);
    place_caret_at_end(doc.d, t);

    // The explicit end-position setup appends, while Home below inserts in front.
    weva_document_text_input(doc.d, "X");
    CHECK(doc.value("#t") == "abcdX");

    // Home, then typing goes at the FRONT -- which is the whole difference
    // between a caret and an append.
    CHECK(weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1) == 1);
    weva_document_text_input(doc.d, "0");
    CHECK(doc.value("#t") == "0abcdX");

    // One Right -- the caret is already after the '0' it just typed -- then
    // insert in the middle.
    weva_document_key(doc.d, WEVA_KEY_RIGHT, 0, 1);
    weva_document_text_input(doc.d, "-");
    CHECK(doc.value("#t") == "0a-bcdX");

    // Backspace takes the character BEFORE the cursor, Delete the one after.
    CHECK(weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1) == 1);
    CHECK(doc.value("#t") == "0abcdX");
    CHECK(weva_document_key(doc.d, WEVA_KEY_DELETE, 0, 1) == 1);
    CHECK(doc.value("#t") == "0acdX");

    // End, and Delete there does nothing rather than running off.
    CHECK(weva_document_key(doc.d, WEVA_KEY_END, 0, 1) == 1);
    weva_document_key(doc.d, WEVA_KEY_DELETE, 0, 1);
    CHECK(doc.value("#t") == "0acdX");
    // Home, and Backspace there likewise.
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1);
    CHECK(doc.value("#t") == "0acdX");

    // Multi-byte text moves by CHARACTER. A cursor between the bytes of one
    // codepoint is not a position at all, and deleting there would corrupt it.
    Doc utf("html, body { margin: 0 } input { display: block; width: 200px }",
            "<input id=t type=text value=''>");
    const weva_element_t u = weva_document_query(utf.d, "#t");
    weva_document_set_focus(utf.d, u);
    weva_document_text_input(utf.d, "a");
    weva_document_text_input(utf.d, "é");   // e-acute, two bytes
    weva_document_text_input(utf.d, "b");
    CHECK(utf.value("#t") == "aé" "b");
    weva_document_key(utf.d, WEVA_KEY_LEFT, 0, 1);    // before 'b'
    weva_document_key(utf.d, WEVA_KEY_BACKSPACE, 0, 1);
    CHECK(utf.value("#t") == "ab");                   // the whole codepoint went

    // Editing keys reach a field only when one has focus.
    Doc other("html, body { margin: 0 } input { display: block }",
              "<input id=t type=text value=abc><input id=c type=checkbox>");
    weva_document_set_focus(other.d, weva_document_query(other.d, "#c"));
    CHECK(weva_document_key(other.d, WEVA_KEY_BACKSPACE, 0, 1) == 0);
    CHECK(other.value("#t") == "abc");
}

// The caret is drawn, and it blinks.
void test_abi_caret_is_drawn() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 30px }",
            "<input id=t type=text value=abc>");
    size_t unfocused = 0;
    weva_document_draws(doc.d, &unfocused);

    weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
    weva_document_update(doc.d, 0);
    size_t focused = 0;
    weva_document_draws(doc.d, &focused);
    // Focusing adds the caret AND the focus ring the UA stylesheet asks for,
    // so the blink is measured against the frame with the ring in it rather
    // than against the unfocused one.
    CHECK(focused > unfocused);

    // A focused field keeps the document animating, or a host that stops
    // handing over time freezes the cursor mid-blink.
    CHECK(weva_document_is_animating(doc.d) == 1);

    // Half a second on, half off: one draw's difference, the bar itself.
    weva_document_update(doc.d, 0.6);
    size_t dark = 0;
    weva_document_draws(doc.d, &dark);
    CHECK(dark == focused - 1);
    weva_document_update(doc.d, 0.5);
    size_t lit = 0;
    weva_document_draws(doc.d, &lit);
    CHECK(lit == focused);

    // Moving it restarts the blink: a cursor that winks out while you are
    // moving it is worse than none.
    weva_document_update(doc.d, 0.6);
    weva_document_draws(doc.d, &dark);
    CHECK(dark == focused - 1);
    weva_document_key(doc.d, WEVA_KEY_LEFT, 0, 1);
    weva_document_update(doc.d, 0);
    weva_document_draws(doc.d, &lit);
    CHECK(lit == focused);
}

// CSS UI 4 §5.4 caret-color: the bar takes the author's colour, `auto` is
// the text colour, and `transparent` hides it without removing focus.
void test_abi_caret_color() {
    // The caret is the one untextured draw a pixel wide.
    const auto caret_of = [](weva_document_t d, float* r, float* g, float* b, float* a) {
        weva_document_update(d, 0);
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        int found = 0;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].texture_id != 0 || draws[i].vertex_count < 3) continue;
            float lo = 1e9f, hi = -1e9f;
            for (size_t v = 0; v < draws[i].vertex_count; ++v) {
                lo = std::min(lo, draws[i].vertices[v].x);
                hi = std::max(hi, draws[i].vertices[v].x);
            }
            if (hi - lo > 1.5f) continue;
            *r = draws[i].vertices[0].r;
            *g = draws[i].vertices[0].g;
            *b = draws[i].vertices[0].b;
            *a = draws[i].vertices[0].a;
            ++found;
        }
        return found;
    };
    float r = 0, g = 0, b = 0, a = 0;
    {
        Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 30px;"
                " color: rgb(255, 0, 0); caret-color: rgb(0, 255, 0) }",
                "<input id=t type=text value=abc>");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
        CHECK(caret_of(doc.d, &r, &g, &b, &a) == 1);
        CHECK(r == 0 && g == 1 && b == 0 && a == 1);
    }
    {
        // `auto` and no declaration: the text colour. Inherited: set on the
        // body, seen by the field.
        Doc doc("html, body { margin: 0 } body { caret-color: auto }"
                " input { display: block; width: 200px; height: 30px; color: rgb(255, 0, 0) }",
                "<input id=t type=text value=abc>");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
        CHECK(caret_of(doc.d, &r, &g, &b, &a) == 1);
        CHECK(r == 1 && g == 0 && b == 0 && a == 1);
    }
    {
        Doc doc("html, body { margin: 0 } body { caret-color: rgb(0, 0, 255) }"
                " input { display: block; width: 200px; height: 30px; color: rgb(255, 0, 0) }",
                "<input id=t type=text value=abc>");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
        CHECK(caret_of(doc.d, &r, &g, &b, &a) == 1);
        CHECK(r == 0 && g == 0 && b == 1 && a == 1);
    }
    {
        // A <textarea> caret goes through the text-run path; same property.
        Doc doc("html, body { margin: 0 } textarea { display: block; width: 200px; height: 80px;"
                " color: rgb(255, 0, 0); caret-color: rgb(0, 255, 0) }",
                "<textarea id=t>abc</textarea>");
        const weva_element_t t = weva_document_query(doc.d, "#t");
        weva_document_set_focus(doc.d, t);
        place_caret_at_end(doc.d, t);
        CHECK(caret_of(doc.d, &r, &g, &b, &a) == 1);
        CHECK(r == 0 && g == 1 && b == 0 && a == 1);
    }
    {
        Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 30px;"
                " color: rgb(255, 0, 0); caret-color: transparent }",
                "<input id=t type=text value=abc>");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
        const int n = caret_of(doc.d, &r, &g, &b, &a);
        CHECK(n == 0 || a == 0);
    }
}

// Minor 28: the host's colour-scheme preference reaches both the media
// query and light-dark(), and flipping it restyles the live document.
void test_abi_color_scheme() {
    Doc doc("html, body { margin: 0 }"
            " #a { display: block; width: 100px; height: 20px; background-color: light-dark(rgb(255, 0, 0), rgb(0, 0, 255)) }"
            " #b { display: block; width: 100px; height: 20px; background-color: rgb(0, 255, 0) }"
            " @media (prefers-color-scheme: dark) { #b { display: none } }",
            "<div id=a></div><div id=b></div>");
    const auto fill_of = [](weva_document_t d, double y) {
        weva_document_update(d, 0);
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        float r = -1, g = -1, b = -1;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].texture_id != 0 || draws[i].vertex_count < 3) continue;
            float lo = 1e9f, hi = -1e9f;
            for (size_t v = 0; v < draws[i].vertex_count; ++v) {
                lo = std::min(lo, draws[i].vertices[v].y);
                hi = std::max(hi, draws[i].vertices[v].y);
            }
            if (lo > y || hi < y) continue;
            r = draws[i].vertices[0].r; g = draws[i].vertices[0].g; b = draws[i].vertices[0].b;
        }
        return std::make_tuple(r, g, b);
    };
    CHECK(fill_of(doc.d, 10) == std::make_tuple(1.f, 0.f, 0.f));
    CHECK(fill_of(doc.d, 30) == std::make_tuple(0.f, 1.f, 0.f));

    weva_document_set_color_scheme(doc.d, 1);
    CHECK(fill_of(doc.d, 10) == std::make_tuple(0.f, 0.f, 1.f));
    // #b is display:none under the dark scheme, so nothing is drawn there.
    CHECK(fill_of(doc.d, 30) == std::make_tuple(-1.f, -1.f, -1.f));

    // Setting the same scheme again is free; switching back restores the page.
    weva_document_set_color_scheme(doc.d, 1);
    weva_document_set_color_scheme(doc.d, 0);
    CHECK(fill_of(doc.d, 10) == std::make_tuple(1.f, 0.f, 0.f));
    CHECK(fill_of(doc.d, 30) == std::make_tuple(0.f, 1.f, 0.f));
}

// ::placeholder colours the hint text and ::selection the band behind a
// selection; without a rule the UA's faded text and blue band remain.
void test_abi_placeholder_and_selection_pseudos() {
    // The first textured draw is the field's text; the widest untextured
    // draw inside the field is the band.
    struct Found { int text = 0; float tr = 0, tg = 0, tb = 0, ta = 0; int band = 0; float br = 0, bg = 0, bb = 0, ba = 0; };
    const auto scan = [](weva_document_t d) {
        weva_document_update(d, 0);
        Found f;
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        float widest = 1.5f;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].vertex_count < 3) continue;
            const weva_vertex& v0 = draws[i].vertices[0];
            if (draws[i].texture_id != 0) {
                if (f.text++ == 0) { f.tr = v0.r; f.tg = v0.g; f.tb = v0.b; f.ta = v0.a; }
                continue;
            }
            float lo = 1e9f, hi = -1e9f, top = 1e9f, bottom = -1e9f;
            for (size_t k = 0; k < draws[i].vertex_count; ++k) {
                lo = std::min(lo, draws[i].vertices[k].x); hi = std::max(hi, draws[i].vertices[k].x);
                top = std::min(top, draws[i].vertices[k].y); bottom = std::max(bottom, draws[i].vertices[k].y);
            }
            // Inside the 200x30 field and not the field's own box or ring.
            if (lo < 0 || hi > 200 || top < 0 || bottom > 30 || hi - lo >= 199) continue;
            if (hi - lo > widest) { widest = hi - lo; f.band = 1; f.br = v0.r; f.bg = v0.g; f.bb = v0.b; f.ba = v0.a; }
        }
        return f;
    };
    const char* kField = "html, body { margin: 0 } input { display: block; width: 200px; height: 30px;"
                         " padding: 0; border: 0; color: rgb(255, 0, 0) }";
    {
        Doc doc((std::string(kField) + " input::placeholder { color: rgb(0, 255, 0) }").c_str(),
                "<input id=t type=text placeholder=hint>");
        const Found f = scan(doc.d);
        CHECK(f.text >= 1);
        CHECK(f.tr == 0 && f.tg == 1 && f.tb == 0 && f.ta == 1);
    }
    {
        // No rule: the host colour at half strength, as before.
        Doc doc(kField, "<input id=t type=text placeholder=hint>");
        const Found f = scan(doc.d);
        CHECK(f.text >= 1);
        CHECK(f.tr == 0.5f && f.tg == 0 && f.tb == 0 && f.ta == 0.5f);
    }
    {
        // Chrome's own UA rule shape: opacity on the pseudo.
        Doc doc((std::string(kField) + " ::placeholder { opacity: 0.25 }").c_str(),
                "<input id=t type=text placeholder=hint>");
        const Found f = scan(doc.d);
        CHECK(f.text >= 1);
        CHECK(f.tr == 1 && f.ta == 0.25f);
    }
    {
        Doc doc((std::string(kField) + " input::selection { background-color: rgb(255, 0, 255) }").c_str(),
                "<input id=t type=text value=hello>");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
        weva_document_update(doc.d, 0);
        CHECK(weva_document_select_all(doc.d) == 1);
        const Found f = scan(doc.d);
        CHECK(f.band == 1);
        CHECK(f.br == 1 && f.bg == 0 && f.bb == 1 && f.ba == 1);
    }
    {
        Doc doc(kField, "<input id=t type=text value=hello>");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
        weva_document_update(doc.d, 0);
        CHECK(weva_document_select_all(doc.d) == 1);
        const Found f = scan(doc.d);
        CHECK(f.band == 1);
        CHECK(f.ba > 0.44f && f.ba < 0.46f);   // the UA blue
    }
    {
        // A <textarea>'s selection goes through the text-run path.
        Doc doc("html, body { margin: 0 } textarea { display: block; width: 200px; height: 30px;"
                " padding: 0; border: 0; color: rgb(255, 0, 0) }"
                " textarea::selection { background-color: rgb(0, 255, 255) }",
                "<textarea id=t>hello</textarea>");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
        weva_document_update(doc.d, 0);
        CHECK(weva_document_select_all(doc.d) == 1);
        const Found f = scan(doc.d);
        CHECK(f.band == 1);
        CHECK(f.br == 0 && f.bg == 1 && f.bb == 1 && f.ba == 1);
    }
}

// CSS Positioned Layout L3 §6.3 position: sticky. A header pins to the top of
// its scroll container while its containing block is in view and scrolls
// away with it after; a footer with `bottom: 0` rides the bottom edge.
void test_abi_position_sticky() {
    Doc doc("html, body { margin: 0 }"
            " #sc { overflow: auto; height: 100px; width: 200px }"
            " #cb { height: 200px } #pre { height: 50px }"
            " #h { position: sticky; top: 0; height: 20px; background: #f00 }"
            " #tail { height: 400px }"
            " #f { position: sticky; bottom: 0; height: 20px; background: #00f }",
            "<div id=sc><div id=cb><div id=pre></div><div id=h></div></div>"
            "<div id=tail></div><div id=f></div></div>");
    const weva_element_t sc = weva_document_query(doc.d, "#sc");
    const weva_element_t h = weva_document_query(doc.d, "#h");
    const weva_element_t f = weva_document_query(doc.d, "#f");
    const auto top_of = [&](weva_element_t e) {
        weva_document_update(doc.d, 0);
        double x = 0, y = 0, w = 0, hh = 0;
        weva_element_bounds(doc.d, e, &x, &y, &w, &hh);
        return y;
    };
    // The natural positions: the header 50 down, the footer pinned up to the
    // scrollport's bottom edge from its place after the tail.
    CHECK(top_of(h) == 50);
    CHECK(top_of(f) == 80);

    // Scrolled 100: the header would be 50 above the scrollport, so it pins
    // to the top; the footer still sits on the bottom edge.
    weva_element_set_scroll(doc.d, sc, 0, 100);
    CHECK(top_of(h) == 0);
    CHECK(top_of(f) == 80);

    // Scrolled 300: the containing block (200 tall) ends at 180 for a 20px
    // header, so it was carried to 180 and has scrolled 120 out of view.
    weva_element_set_scroll(doc.d, sc, 0, 300);
    CHECK(top_of(h) == -120);
    CHECK(top_of(f) == 80);

    // At the end of the scroll the footer is 20 above its natural place, on
    // the edge; the header is long gone.
    weva_element_set_scroll(doc.d, sc, 0, 520);
    CHECK(top_of(f) == 80);
    CHECK(top_of(h) == -340);

    // Back at the top everything returns to its natural place, and what is
    // drawn agrees with the bounds: the header's red fill is at y=0 when
    // pinned, and hit testing finds it there.
    weva_element_set_scroll(doc.d, sc, 0, 100);
    weva_document_update(doc.d, 0);
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(doc.d, &count);
    bool red_at_top = false;
    for (size_t i = 0; i < count; ++i) {
        if (draws[i].texture_id != 0 || draws[i].vertex_count < 3) continue;
        const weva_vertex& v = draws[i].vertices[0];
        if (!(v.r == 1 && v.g == 0 && v.b == 0)) continue;
        float top = 1e9f;
        for (size_t k = 0; k < draws[i].vertex_count; ++k) top = std::min(top, draws[i].vertices[k].y);
        if (top == 0) red_at_top = true;
    }
    CHECK(red_at_top);
    CHECK(weva_document_element_at(doc.d, 100, 10) == h);
    weva_element_set_scroll(doc.d, sc, 0, 0);
    CHECK(top_of(h) == 50);
}

// CSS Cascade 5 §4: @import loads through the asset reader and splices the
// sheet in, under its media, supports and layer conditions; nested imports,
// cycles, a sheet that cannot be read and an import after other rules.
void test_abi_at_import() {
    static const char* kSheets[][2] = {
        {"a.css", "@import \"d.css\"; #a { color: rgb(1, 0, 0) }"},
        {"b.css", "#b { color: rgb(2, 0, 0) }"},
        {"c.css", "#c { color: rgb(3, 0, 0) }"},
        {"d.css", "#d { color: rgb(4, 0, 0) }"},
        {"cycle.css", "@import 'cycle.css'; #cy { color: rgb(5, 0, 0) }"},
        {"e.css", "#e { color: rgb(6, 0, 0) }"},
        {"f.css", "#f { color: rgb(7, 0, 0) }"},
        {"late.css", "#late { color: rgb(8, 0, 0) }"},
        {"g.css", "#g { color: rgb(10, 0, 0) }"},
    };
    const auto reader = [](void*, const char* path, uint8_t* out, size_t capacity) -> size_t {
        // The whole file name at the end of the resolved path: "missing.css"
        // ends in "g.css" and must not read as g.css.
        const std::string_view full(path);
        for (const auto& sheet : kSheets) {
            const std::string_view name(sheet[0]);
            if (full.size() < name.size() || full.substr(full.size() - name.size()) != name) continue;
            if (full.size() > name.size() && full[full.size() - name.size() - 1] != '/' &&
                full[full.size() - name.size() - 1] != '\\') continue;
            const size_t size = std::strlen(sheet[1]);
            if (out && capacity >= size) std::memcpy(out, sheet[1], size);
            return size;
        }
        return 0;
    };
    weva_config c = config();
    weva_document_t d = weva_document_create(&c);
    weva_document_set_asset_reader(d, reader, nullptr);
    const char* html = "<div id=a></div><div id=b></div><div id=c></div><div id=d></div><div id=cy></div>"
                       "<div id=e></div><div id=f></div><div id=late></div><div id=g></div>";
    weva_document_load_html(d, html, std::strlen(html));
    const char* css = "@import url(\"a.css\"); @import 'b.css' screen; @import \"c.css\" print;"
                      " @import \"cycle.css\"; @import \"e.css\" supports(display: grid);"
                      " @import url(f.css) layer(base); @import \"missing.css\";"
                      " @import \"g.css\" supports(bogus-property: 1);"
                      " #f { color: rgb(9, 0, 0) } @import \"late.css\";";
    CHECK(weva_document_add_css(d, css, std::strlen(css)) == WEVA_OK);
    weva_document_update(d, 0);
    const auto color_of = [&](const char* selector) {
        const weva_element_t e = weva_document_query(d, selector);
        std::string all(weva_element_computed_style_all(d, e, nullptr, 0) + 1, '\0');
        weva_element_computed_style_all(d, e, all.data(), all.size());
        size_t at = 0;
        while (at < all.size()) {
            const size_t nl = all.find('\n', at);
            const std::string_view line(all.data() + at, (nl == std::string::npos ? all.size() : nl) - at);
            if (line.substr(0, 6) == "color\t") return std::string(line.substr(6));
            if (nl == std::string::npos) break;
            at = nl + 1;
        }
        return std::string();
    };
    CHECK(color_of("#a") == "rgb(1, 0, 0)");
    CHECK(color_of("#d") == "rgb(4, 0, 0)");    // imported by a.css
    CHECK(color_of("#b") == "rgb(2, 0, 0)");    // `screen` matches
    CHECK(color_of("#c") != "rgb(3, 0, 0)");    // `print` does not
    CHECK(color_of("#cy") == "rgb(5, 0, 0)");   // the cycle loads once
    CHECK(color_of("#e") == "rgb(6, 0, 0)");    // supports() true
    CHECK(color_of("#g") != "rgb(10, 0, 0)");   // supports() false
    CHECK(color_of("#f") == "rgb(9, 0, 0)");    // unlayered beats the imported layer
    CHECK(color_of("#late") != "rgb(8, 0, 0)"); // an @import after a rule is ignored
    // The sheet that could not be read is reported.
    std::string diag(weva_document_css_diagnostics(d, nullptr, 0) + 1, '\0');
    weva_document_css_diagnostics(d, diag.data(), diag.size());
    CHECK(diag.find("missing.css") != std::string::npos);
    weva_document_destroy(d);
}

// CSS Scroll Snap L1: a programmatic scroll lands on a snap position at
// once; a wheel scroll moves freely, then settles onto one once the wheel is
// quiet, animated; `scroll-snap-stop: always` is not skipped; `proximity`
// only snaps within half the scrollport; padding, margin, end and center.
void test_abi_scroll_snap() {
    {
        Doc doc("html, body { margin: 0 }"
                " #s { overflow: auto; height: 200px; width: 200px; scroll-snap-type: y mandatory }"
                " .slide { height: 150px; scroll-snap-align: start } #b { scroll-snap-stop: always }",
                "<div id=s><div class=slide id=a></div><div class=slide id=b></div>"
                "<div class=slide id=c></div><div class=slide id=d></div></div>");
        const weva_element_t s = weva_document_query(doc.d, "#s");
        const auto scroll_y = [&]() {
            double x = 0, y = 0, mx = 0, my = 0;
            weva_element_scroll(doc.d, s, &x, &y, &mx, &my);
            return y;
        };
        // Snap positions 0, 150, 300 and 400 (450 clamped to the 400 maximum).
        weva_element_set_scroll(doc.d, s, 0, 100);
        weva_document_update(doc.d, 0);
        CHECK(scroll_y() == 150);
        weva_element_set_scroll(doc.d, s, 0, 60);
        weva_document_update(doc.d, 0);
        CHECK(scroll_y() == 0);

        // The wheel moves freely and keeps the document animating until the
        // settle; the settle animates to the nearest position.
        CHECK(weva_document_scroll(doc.d, 100, 100, 0, 100) == 1);
        weva_document_update(doc.d, 0.05);
        CHECK(scroll_y() == 100);
        CHECK(weva_document_is_animating(doc.d) == 1);
        weva_document_update(doc.d, 0.2);
        CHECK(scroll_y() > 100 && scroll_y() < 150);
        weva_document_update(doc.d, 1.0);
        CHECK(scroll_y() == 150);
        CHECK(weva_document_is_animating(doc.d) == 0);

        // A wheel sequence that passes over #b, which must not be skipped,
        // stops there instead of at the nearest position to where it ended.
        weva_element_set_scroll(doc.d, s, 0, 0);
        weva_document_update(doc.d, 0);
        CHECK(weva_document_scroll(doc.d, 100, 100, 0, 400) == 1);
        weva_document_update(doc.d, 0.2);
        weva_document_update(doc.d, 1.0);
        CHECK(scroll_y() == 150);
    }
    {
        Doc doc("html, body { margin: 0 }"
                " #s { overflow: auto; height: 200px; width: 200px; scroll-snap-type: y proximity;"
                "      scroll-padding-top: 10px }"
                " .tall { height: 600px }"
                " #p { height: 100px; scroll-snap-align: start; scroll-margin-top: 5px }"
                " #e { height: 100px; scroll-snap-align: end }"
                " #c { height: 50px; scroll-snap-align: center }",
                "<div id=s><div id=p></div><div class=tall></div><div id=e></div><div id=c></div>"
                "<div class=tall></div></div>");
        const weva_element_t s = weva_document_query(doc.d, "#s");
        const auto scroll_y = [&]() {
            double x = 0, y = 0, mx = 0, my = 0;
            weva_element_scroll(doc.d, s, &x, &y, &mx, &my);
            return y;
        };
        // Positions: #p start -> 0 - 5 - 10, clamped to 0; #e end -> 800 - 200 = 600;
        // #c center -> 825 - 100 = 725.
        weva_element_set_scroll(doc.d, s, 0, 40);
        weva_document_update(doc.d, 0);
        CHECK(scroll_y() == 0);
        weva_element_set_scroll(doc.d, s, 0, 300);   // nothing within 100
        weva_document_update(doc.d, 0);
        CHECK(scroll_y() == 300);
        weva_element_set_scroll(doc.d, s, 0, 640);
        weva_document_update(doc.d, 0);
        CHECK(scroll_y() == 600);
        weva_element_set_scroll(doc.d, s, 0, 700);
        weva_document_update(doc.d, 0);
        CHECK(scroll_y() == 725);
    }
    {
        // The inline axis: `x mandatory` over a row of inline-blocks, and a
        // container that snaps only on y leaves x alone.
        Doc doc("html, body { margin: 0 }"
                " #s { overflow: auto; height: 100px; width: 200px; white-space: nowrap; scroll-snap-type: x mandatory }"
                " .card { display: inline-block; width: 150px; height: 50px; scroll-snap-align: start }",
                "<div id=s><div class=card></div><div class=card></div><div class=card></div><div class=card></div></div>");
        const weva_element_t s = weva_document_query(doc.d, "#s");
        double x = 0, y = 0, mx = 0, my = 0;
        weva_element_set_scroll(doc.d, s, 100, 0);
        weva_document_update(doc.d, 0);
        weva_element_scroll(doc.d, s, &x, &y, &mx, &my);
        CHECK(x == 150);
    }
}

// Minor 29: the cursor the page asks for under the pointer -- the element's
// keyword, `auto` settled, a url() list's fallback -- so a host can show it.
void test_abi_cursor() {
    Doc doc("html, body { margin: 0 } div, input, button, a { display: block; width: 200px; height: 20px }"
            " #p { cursor: pointer } #g { cursor: url(hand.png) 2 2, grab } #m { cursor: MOVE }",
            "<div id=p>x</div><div id=g>y</div><div id=m>z</div><input id=i type=text>"
            "<button id=b disabled>b</button><a id=a href=\"#\">link</a><div id=d>plain</div>");
    weva_document_update(doc.d, 0);
    const auto cursor_over = [&](const char* selector, double dx) {
        double x = 0, y = 0, w = 0, h = 0;
        weva_element_bounds(doc.d, weva_document_query(doc.d, selector), &x, &y, &w, &h);
        weva_document_set_pointer(doc.d, x + dx, y + h / 2, 0);
        weva_document_update(doc.d, 0);
        std::string out(weva_document_cursor(doc.d, nullptr, 0) + 1, '\0');
        weva_document_cursor(doc.d, out.data(), out.size());
        out.resize(out.size() - 1);
        return out;
    };
    CHECK(cursor_over("#p", 100) == "pointer");
    CHECK(cursor_over("#g", 100) == "grab");        // the url() has no image here: its fallback
    CHECK(cursor_over("#m", 100) == "move");        // keywords come back lower-case
    CHECK(cursor_over("#i", 100) == "text");        // auto over a text field
    CHECK(cursor_over("#b", 100) == "not-allowed"); // the UA sheet's :disabled rule
    CHECK(cursor_over("#a", 2) == "pointer");       // auto over a link
    CHECK(cursor_over("#d", 2) == "text");          // auto over the word
    CHECK(cursor_over("#d", 150) == "default");     // auto over the block's empty run
    // Off the document: the host's arrow. cursor_at asks about any point.
    weva_document_clear_pointer(doc.d);
    weva_document_update(doc.d, 0);
    std::string out(weva_document_cursor(doc.d, nullptr, 0) + 1, '\0');
    weva_document_cursor(doc.d, out.data(), out.size());
    CHECK(std::string(out.c_str()) == "default");
    std::string at(weva_document_cursor_at(doc.d, 100, 10, nullptr, 0) + 1, '\0');
    weva_document_cursor_at(doc.d, 100, 10, at.data(), at.size());
    CHECK(std::string(at.c_str()) == "pointer");
}

// The Godot host's range_direction_tests sequence at the C ABI: a press one
// pixel inside the track's start sets the minimum (ltr) or the maximum
// (rtl), delivered as the host delivers it -- a motion, then the button down
// and up as pointer states, with an update after each.
void test_abi_range_click_at_edge() {
    for (const char* dir : {"ltr", "rtl"}) {
        const std::string css = std::string("#r{position:absolute;left:40px;top:40px;width:200px;height:30px;"
                                            "writing-mode:horizontal-tb;direction:") + dir + "}";
        Doc doc(css.c_str(), "<input id=\"r\" type=\"range\" min=\"0\" max=\"100\" value=\"50\">");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#r"));
        weva_document_update(doc.d, 0);
        double x = 0, y = 0, w = 0, h = 0;
        weva_element_bounds(doc.d, weva_document_query(doc.d, "#r"), &x, &y, &w, &h);
        const double px = x + 1, py = y + h / 2;
        weva_document_set_pointer_modifiers(doc.d, px, py, 0, 0);
        weva_document_update(doc.d, 0);
        weva_document_set_pointer_modifiers(doc.d, px, py, WEVA_BUTTON_PRIMARY, 0);
        weva_document_update(doc.d, 0);
        weva_document_set_pointer_modifiers(doc.d, px, py, 0, 0);
        weva_document_update(doc.d, 0);
        const std::string value = doc.value("#r");
        if (value != (std::string(dir) == "ltr" ? "0" : "100")) {
            std::printf("range edge click (%s): value %s at %.1f,%.1f bounds %.1f,%.1f %.1fx%.1f\n",
                        dir, value.c_str(), px, py, x, y, w, h);
        }
        CHECK(value == (std::string(dir) == "ltr" ? "0" : "100"));
    }
}

// Minor 30: engine counters for a stats window, and the hit test an
// inspector wants -- one that pointer-events: none and visibility: hidden do
// not hide anything from.
void test_abi_stats_and_devtools_hit() {
    Doc doc("html, body { margin: 0 } div { width: 100px; height: 40px; background: #123 }"
            " #n { pointer-events: none } #h { visibility: hidden }",
            "<div id=a></div><div id=n></div><div id=h></div>");
    weva_stats st{};
    weva_document_stats(doc.d, &st);
    CHECK(st.updates == 1);
    CHECK(st.elements >= 5);       // html, head?, body, three divs
    CHECK(st.boxes >= 4);
    CHECK(st.draws >= 1);          // #a's fill at least; #h draws nothing
    CHECK(st.update_ms >= 0 && st.cascade_ms >= 0 && st.layout_ms >= 0 && st.paint_ms >= 0);
    CHECK(st.cascade_elements >= 5);
    // A settled update still counts, and touches no stage.
    weva_document_update(doc.d, 0);
    weva_document_stats(doc.d, &st);
    CHECK(st.updates == 2);
    // The stages of an update that restyled: a class flip recascades and repaints.
    weva_document_add_css(doc.d, "#a { background: #456 }", 23);
    weva_document_update(doc.d, 0);
    weva_stats after{};
    weva_document_stats(doc.d, &after);
    CHECK(after.updates == 3);
    CHECK(after.cascade_elements > st.cascade_elements);

    const weva_element_t body = weva_document_query(doc.d, "body");
    const weva_element_t n = weva_document_query(doc.d, "#n");
    const weva_element_t h = weva_document_query(doc.d, "#h");
    // A click over #n lands on the body; over #h the same, since hidden
    // boxes take no hits. The inspector gets the element that is there.
    CHECK(weva_document_element_at(doc.d, 50, 60) == body);
    CHECK(weva_document_element_at_devtools(doc.d, 50, 60) == n);
    CHECK(weva_document_element_at(doc.d, 50, 100) == body);
    CHECK(weva_document_element_at_devtools(doc.d, 50, 100) == h);
    CHECK(weva_document_element_at_devtools(doc.d, 50, 20) == weva_document_query(doc.d, "#a"));
    CHECK(weva_document_element_at_devtools(nullptr, 50, 20) == WEVA_ELEMENT_NONE);
    weva_document_stats(nullptr, &st);
    CHECK(st.updates == 0 && st.boxes == 0);
}

// Minor 31: the box tree as a list -- what a devtools overlay draws from.
void test_abi_box_tree() {
    Doc doc("html, body { margin: 0 } #a { padding: 5px; border: 2px solid #000; margin: 3px; width: 100px }",
            "<div id=a><span id=s>hi</span></div>");
    weva_document_update(doc.d, 0);
    const size_t n = weva_document_boxes(doc.d, nullptr, 0);
    CHECK(n >= 5);   // html, body, #a, a line, #s, the text
    std::vector<weva_box> boxes(n);
    CHECK(weva_document_boxes(doc.d, boxes.data(), boxes.size()) == n);
    // Tree order: the root first, every parent before its child.
    CHECK(boxes[0].parent == WEVA_BOX_NONE);
    bool ordered = true;
    for (size_t i = 1; i < n; ++i) if (boxes[i].parent == WEVA_BOX_NONE || boxes[i].parent >= i) ordered = false;
    CHECK(ordered);
    const weva_element_t a = weva_document_query(doc.d, "#a"), s = weva_document_query(doc.d, "#s");
    size_t a_at = n, s_at = n, text_at = n, line_at = n;
    for (size_t i = 0; i < n; ++i) {
        if (boxes[i].element == a && boxes[i].kind == WEVA_BOX_BLOCK) a_at = i;
        if (boxes[i].element == s && boxes[i].kind == WEVA_BOX_INLINE) s_at = i;
        if (boxes[i].kind == WEVA_BOX_TEXT && boxes[i].text_length == 2 &&
            std::string_view(boxes[i].text, boxes[i].text_length) == "hi") text_at = i;
        if (boxes[i].kind == WEVA_BOX_LINE) line_at = i;
    }
    CHECK(a_at < n && s_at < n && text_at < n && line_at < n);
    // The geometry is the layout's: #a where bounds put it, with its edges.
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(doc.d, a, &x, &y, &w, &h);
    CHECK(a_at < n && boxes[a_at].x == x && boxes[a_at].y == y && boxes[a_at].width == w && boxes[a_at].height == h);
    CHECK(a_at < n && boxes[a_at].padding_left == 5 && boxes[a_at].border_top == 2 && boxes[a_at].margin_left == 3);
    // The text run and the span's inline box are siblings under the line,
    // as the layout tree keeps them; the line is under #a; the run names the
    // span as its element, the line names none.
    CHECK(boxes[text_at].parent == line_at && boxes[s_at].parent == line_at);
    CHECK(boxes[line_at].parent == a_at);
    CHECK(boxes[text_at].element == s && boxes[line_at].element == WEVA_ELEMENT_NONE);
    // A short buffer takes what fits and still reports the whole count.
    std::vector<weva_box> two(2);
    CHECK(weva_document_boxes(doc.d, two.data(), 2) == n);
    CHECK(two[0].parent == WEVA_BOX_NONE);
    CHECK(weva_document_boxes(nullptr, nullptr, 0) == 0);
}

// Minor 32: a hot reload keeps the live elements it can match -- handles,
// form values, focus and scroll -- and updates the rest in place.
void test_abi_reload_html() {
    Doc doc("html, body { margin: 0 } #s { overflow: auto; height: 50px } .row { height: 40px }"
            " input { display: block; width: 100px; height: 20px }",
            "<div id=a><input id=i type=text value=x><p id=p>one</p></div>"
            "<div id=b>two</div><p>plain</p>"
            "<div id=s><div class=row></div><div class=row></div><div class=row></div></div>");
    const weva_element_t a = weva_document_query(doc.d, "#a"), i = weva_document_query(doc.d, "#i");
    const weva_element_t p = weva_document_query(doc.d, "#p"), s = weva_document_query(doc.d, "#s");
    const weva_element_t plain = weva_document_query(doc.d, "p:not([id])");
    weva_document_set_focus(doc.d, i);
    place_caret_at_end(doc.d, i);
    weva_document_text_input(doc.d, "y");
    weva_element_set_scroll(doc.d, s, 0, 30);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#i") == "xy");

    const char* next = "<div id=a class=new><input id=i type=text value=x><p id=p>uno</p><p id=q>new</p></div>"
                       "<p>plain too</p>"
                       "<div id=s><div class=row></div><div class=row></div><div class=row></div></div>";
    CHECK(weva_document_reload_html(doc.d, next, std::strlen(next)) == WEVA_OK);
    weva_document_update(doc.d, 0);
    // The same handles answer for the elements the diff matched.
    CHECK(weva_document_query(doc.d, "#a") == a);
    CHECK(weva_document_query(doc.d, "#i") == i);
    CHECK(weva_document_query(doc.d, "#p") == p);
    CHECK(weva_document_query(doc.d, "#s") == s);
    CHECK(weva_document_query(doc.d, "p:not([id])") == plain);   // positional, no key
    // What the user did survives: the typed value, the focus, the scroll.
    CHECK(doc.value("#i") == "xy");
    CHECK(weva_document_focus(doc.d) == i);
    double sx = 0, sy = 0, mx = 0, my = 0;
    weva_element_scroll(doc.d, s, &sx, &sy, &mx, &my);
    CHECK(sy == 30);
    // What the markup changed took: an attribute, a text, a new element, one gone.
    char cls[16] = {0};
    weva_element_attribute(doc.d, a, "class", cls, sizeof cls);
    CHECK(std::string(cls) == "new");
    char text[32] = {0};
    weva_element_text(doc.d, p, text, sizeof text);
    CHECK(std::string(text) == "uno");
    weva_element_text(doc.d, plain, text, sizeof text);
    CHECK(std::string(text) == "plain too");
    CHECK(weva_document_query(doc.d, "#q") != WEVA_ELEMENT_NONE);
    CHECK(weva_document_query(doc.d, "#b") == WEVA_ELEMENT_NONE);
    // A reordered keyed element keeps its handle in its new place.
    const char* swapped = "<div id=s><div class=row></div><div class=row></div><div class=row></div></div>"
                          "<div id=a class=new><input id=i type=text value=x><p id=p>uno</p><p id=q>new</p></div>";
    CHECK(weva_document_reload_html(doc.d, swapped, std::strlen(swapped)) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_query(doc.d, "#a") == a && weva_document_query(doc.d, "#s") == s);
    double ax = 0, ay = 0, aw = 0, ah = 0, ssx = 0, ssy = 0, sw = 0, sh = 0;
    weva_element_bounds(doc.d, a, &ax, &ay, &aw, &ah);
    weva_element_bounds(doc.d, s, &ssx, &ssy, &sw, &sh);
    CHECK(ssy < ay);
    CHECK(doc.value("#i") == "xy");
}

// Minor 33: `mix-blend-mode` rides on every draw of the element's subtree,
// and a sibling painted after it is back to normal.
void test_abi_mix_blend_mode() {
    Doc doc("html, body { margin: 0 } div { width: 100px; height: 20px }"
            " #m { mix-blend-mode: multiply; background: rgb(255, 0, 0) }"
            " #m span { background: rgb(0, 255, 0) } #n { background: rgb(0, 0, 255) }"
            " #s { mix-blend-mode: SCREEN; background: rgb(1, 1, 1) }",
            "<div id=m><span>x</span></div><div id=n></div><div id=s></div>");
    weva_document_update(doc.d, 0);
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(doc.d, &count);
    int multiplied = 0, normal = 0, screened = 0;
    for (size_t i = 0; i < count; ++i) {
        if (draws[i].vertex_count < 3) continue;
        const weva_vertex& v = draws[i].vertices[0];
        const bool red = v.r == 1 && v.g == 0 && v.b == 0, green = v.g == 1 && v.r == 0 && v.b == 0;
        const bool blue = v.b == 1 && v.r == 0 && v.g == 0;
        if ((red || green || draws[i].texture_id != 0) && draws[i].blend_mode == WEVA_BLEND_MULTIPLY) ++multiplied;
        if (blue && draws[i].blend_mode == WEVA_BLEND_NORMAL) ++normal;
        if (draws[i].blend_mode == WEVA_BLEND_SCREEN) ++screened;
    }
    CHECK(multiplied >= 3);   // #m's fill, the span's fill, the span's glyphs
    CHECK(normal == 1);
    CHECK(screened >= 1);
}

// Minor 34: the parse errors a lenient load recovered from, with positions.
void test_abi_html_diagnostics() {
    Doc doc("html, body { margin: 0 }", "<div id=a>ok</div>");
    const auto diagnostics = [&]() {
        std::string out(weva_document_html_diagnostics(doc.d, nullptr, 0) + 1, '\0');
        weva_document_html_diagnostics(doc.d, out.data(), out.size());
        return std::string(out.c_str());
    };
    CHECK(diagnostics().empty());   // clean markup says nothing
    const char* messy = "<div><span>x</div>\n<br></br>\n</b>\n<section>open";
    CHECK(weva_document_load_html(doc.d, messy, std::strlen(messy)) == WEVA_OK);
    const std::string d = diagnostics();
    CHECK(d.find("1:") == 0 && d.find("closes 'span'") != std::string::npos);
    CHECK(d.find("2:") != std::string::npos && d.find("void element 'br'") != std::string::npos);
    CHECK(d.find("3:") != std::string::npos && d.find("Stray end tag 'b'") != std::string::npos);
    CHECK(d.find("Unclosed element 'section'") != std::string::npos);
    CHECK(d.find("Unclosed element 'body'") == std::string::npos);
    // The document still loaded, recovered the way a browser would.
    weva_document_update(doc.d, 0);
    CHECK(weva_document_query(doc.d, "section") != WEVA_ELEMENT_NONE);
    // A reload replaces the list.
    const char* clean = "<div id=a>fine</div>";
    CHECK(weva_document_reload_html(doc.d, clean, std::strlen(clean)) == WEVA_OK);
    CHECK(diagnostics().empty());
}

// A <textarea> keeps what it holds as its CONTENT, not in a `value`
// attribute -- the markup between the tags is the value, as it is in a
// browser. Typing used to write an attribute nothing displayed, so the box
// stayed empty while the keystrokes went somewhere invisible.
void test_abi_textarea_edits_its_content() {
    Doc doc("html, body { margin: 0 } textarea { display: block; width: 200px; height: 80px }",
            "<textarea id=t>hello</textarea>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    CHECK(doc.value("#t") == "hello");   // read from the content

    weva_document_set_focus(doc.d, t);
    place_caret_at_end(doc.d, t);
    weva_document_text_input(doc.d, "!");
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#t") == "hello!");
    // Markup retains the reset default while layout uses the live value.
    char buf[64] = {0};
    weva_element_text(doc.d, t, buf, sizeof(buf));
    CHECK(std::string(buf) == "hello");

    // A host setter changes the live value without replacing that default.
    CHECK(weva_element_set_value(doc.d, t, "typed by the game") == WEVA_OK);
    weva_document_update(doc.d, 0);
    weva_element_text(doc.d, t, buf, sizeof(buf));
    CHECK(std::string(buf) == "hello");
    CHECK(doc.value("#t") == "typed by the game");
}

// Enter is the one key that means something different in a box you can write
// paragraphs in.
void test_abi_textarea_newlines() {
    Doc doc("html, body { margin: 0 } textarea { display: block; width: 200px; height: 80px }"
            "input { display: block }",
            "<textarea id=t></textarea><input id=one type=text value=x>");
    weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t"));
    weva_document_text_input(doc.d, "a");
    CHECK(weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1) == 1);
    weva_document_text_input(doc.d, "b");
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#t") == "a\nb");

    // A one-line field does not take Enter: there is nowhere to put it, and a
    // host wants that key for whatever the form does.
    weva_document_set_focus(doc.d, weva_document_query(doc.d, "#one"));
    CHECK(weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1) == 0);
    CHECK(doc.value("#one") == "x");

    // More lines make it taller, which is the proof the value reached layout.
    Doc grow("html, body { margin: 0 }"
             "textarea { display: block; width: 200px; height: auto; padding: 0; border: 0 }",
             "<textarea id=t>one</textarea>");
    double x = 0, y = 0, w = 0, one_line = 0;
    weva_element_bounds(grow.d, weva_document_query(grow.d, "#t"), &x, &y, &w, &one_line);
    weva_document_set_focus(grow.d, weva_document_query(grow.d, "#t"));
    weva_document_key(grow.d, WEVA_KEY_ENTER, 0, 1);
    weva_document_text_input(grow.d, "two");
    weva_document_update(grow.d, 0);
    double two_lines = 0;
    weva_element_bounds(grow.d, weva_document_query(grow.d, "#t"), &x, &y, &w, &two_lines);
    CHECK(two_lines > one_line);
}

// Home, End and the up and down arrows work by LINE in a textarea, and the
// column is kept across a move -- which is what makes arrowing through a
// paragraph feel like a text box rather than a list.
void test_abi_textarea_line_keys() {
    Doc doc("html, body { margin: 0 } textarea { display: block; width: 300px; height: 80px }",
            "<textarea id=t>alpha\nbeta\ngamma</textarea>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);   // caret at the end, after "gamma"
    place_caret_at_end(doc.d, t);

    // Home is the start of THIS line, not of the whole value.
    CHECK(weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1) == 1);
    weva_document_text_input(doc.d, ">");
    CHECK(doc.value("#t") == "alpha\nbeta\n>gamma");

    // End is the end of this line.
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_END, 0, 1);
    weva_document_text_input(doc.d, "<");
    CHECK(doc.value("#t") == "alpha\nbeta\n>gamma<");

    // Up keeps the column: from column 1 of the third line to column 1 of the
    // second.
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_RIGHT, 0, 1);   // after the '>'
    CHECK(weva_document_key(doc.d, WEVA_KEY_UP, 0, 1) == 1);
    weva_document_text_input(doc.d, "*");
    CHECK(doc.value("#t") == "alpha\nb*eta\n>gamma<");

    // Down from a long line onto a short one lands at the end of the short
    // one rather than past it.
    Doc ragged("html, body { margin: 0 } textarea { display: block; width: 300px; height: 80px }",
               "<textarea id=t>longer line\nab</textarea>");
    weva_document_set_focus(ragged.d, weva_document_query(ragged.d, "#t"));
    weva_document_key(ragged.d, WEVA_KEY_HOME, 0, 1);   // start of "ab"
    weva_document_key(ragged.d, WEVA_KEY_UP, 0, 1);     // start of "longer line"
    weva_document_key(ragged.d, WEVA_KEY_END, 0, 1);    // after "longer line"
    weva_document_key(ragged.d, WEVA_KEY_DOWN, 0, 1);   // clamped to after "ab"
    weva_document_text_input(ragged.d, "!");
    CHECK(ragged.value("#t") == "longer line\nab!");

    // Up on the first line goes to the very start; down on the last goes to
    // the very end, rather than doing nothing.
    weva_document_key(ragged.d, WEVA_KEY_UP, 0, 1);
    weva_document_key(ragged.d, WEVA_KEY_UP, 0, 1);
    weva_document_text_input(ragged.d, "^");
    CHECK(ragged.value("#t") == "^longer line\nab!");
}

// The caret in a textarea. An <input> draws its own text, so paint knows where
// the cursor goes; a textarea's value is laid out as ordinary inline content,
// and the cursor has to be found among the runs.
void test_abi_textarea_caret() {
    // A tiny 1px bar is hard to find among draws, so it is located by what it
    // does to the frame: the leftmost and topmost ink of the thinnest draw.
    const auto caret_of = [](weva_document_t d) {
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        double best_w = 1e9, cx = -1, cy = -1;
        for (size_t i = 0; i < count; ++i) {
            double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
            for (size_t v = 0; v < draws[i].vertex_count; ++v) {
                const weva_vertex& p = draws[i].vertices[v];
                x0 = std::fmin(x0, p.x); x1 = std::fmax(x1, p.x);
                y0 = std::fmin(y0, p.y); y1 = std::fmax(y1, p.y);
            }
            // The caret is the one draw that is a pixel wide and taller than
            // it is broad.
            const double w = x1 - x0, h = y1 - y0;
            if (w > 1.5 || h < w || w >= best_w) continue;
            best_w = w;
            cx = x0;
            cy = y0;
        }
        return std::pair<double, double>(cx, cy);
    };

    Doc doc("html, body { margin: 0 }"
            "textarea { display: block; width: 300px; height: 90px; padding: 0; border: 0;"
            "           font-size: 16px }",
            "<textarea id=t>ab\ncd</textarea>");
    const weva_element_t t = weva_document_query(doc.d, "#t");

    // Nothing focused, no caret.
    CHECK(caret_of(doc.d).first < 0);

    // Explicitly positioned at the end, the caret sits on the second line,
    // two characters in.
    weva_document_set_focus(doc.d, t);
    place_caret_at_end(doc.d, t);
    weva_document_update(doc.d, 0);
    const std::pair<double, double> at_end = caret_of(doc.d);
    CHECK(at_end.first > 0);
    CHECK(at_end.second > 0);   // the second line, not the first

    // Home moves it to the start of that line: same line, hard left.
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_update(doc.d, 0);
    const std::pair<double, double> line_start = caret_of(doc.d);
    CHECK(line_start.first < at_end.first);
    CHECK(std::fabs(line_start.second - at_end.second) < 0.5);   // still line two

    // Up puts it on the first line, at the same column -- higher, and no
    // further right.
    weva_document_key(doc.d, WEVA_KEY_UP, 0, 1);
    weva_document_update(doc.d, 0);
    const std::pair<double, double> line_one = caret_of(doc.d);
    CHECK(line_one.second < line_start.second);

    // Typing moves it along: the cursor is where the next character goes.
    weva_document_key(doc.d, WEVA_KEY_END, 0, 1);
    weva_document_update(doc.d, 0);
    const std::pair<double, double> before = caret_of(doc.d);
    weva_document_text_input(doc.d, "xyz");
    weva_document_update(doc.d, 0);
    const std::pair<double, double> after = caret_of(doc.d);
    CHECK(after.first > before.first);
    CHECK(std::fabs(after.second - before.second) < 0.5);   // same line

    // And it blinks, like the one in an input.
    weva_document_update(doc.d, 0.6);
    CHECK(caret_of(doc.d).first < 0);
    weva_document_update(doc.d, 0.5);
    CHECK(caret_of(doc.d).first > 0);
}

// The view follows the cursor. Typing at the bottom of a textarea has to move
// what you can see, or the text goes on past the end of the box and you are
// writing blind.
void test_abi_textarea_scrolls_to_caret() {
    // Six lines of 20 in a box 50 tall: three fit, three do not.
    std::string many = "one\ntwo\nthree\nfour\nfive\nsix";
    const std::string html = "<textarea id=t>" + many + "</textarea>";
    Doc doc("html, body { margin: 0 }"
            "textarea { display: block; width: 300px; height: 50px; padding: 0; border: 0;"
            "           font-size: 14px; line-height: 20px }",
            html.c_str());
    const weva_element_t t = weva_document_query(doc.d, "#t");
    double y = 0, most = 0;
    weva_element_scroll(doc.d, t, nullptr, &y, nullptr, &most);
    CHECK(most > 0);   // there is more text than box
    CHECK(y == 0);

    // Focusing puts the cursor at the end, which is on the last line -- so the
    // view goes there with it.
    weva_document_set_focus(doc.d, t);
    place_caret_at_end(doc.d, t);
    weva_document_update(doc.d, 0);
    weva_element_scroll(doc.d, t, nullptr, &y, nullptr, &most);
    CHECK(y == most);

    // And back up when the cursor goes back up.
    for (int i = 0; i < 5; ++i) weva_document_key(doc.d, WEVA_KEY_UP, 0, 1);
    weva_document_update(doc.d, 0);
    weva_element_scroll(doc.d, t, nullptr, &y, nullptr, nullptr);
    CHECK(y == 0);

    // A reader who scrolls away stays there: the view follows the CURSOR, not
    // every frame.
    weva_element_set_scroll(doc.d, t, 0, most);
    weva_document_update(doc.d, 0);
    weva_document_update(doc.d, 0.016);
    weva_element_scroll(doc.d, t, nullptr, &y, nullptr, nullptr);
    CHECK(y == most);
}

namespace {

std::string selected(weva_document_t d) {
    char buf[256] = {0};
    weva_document_selected_text(d, buf, sizeof(buf));
    return buf;
}

}   // namespace

// Selecting with the keyboard: shift holds on to where the cursor started,
// and an unshifted move lets go.
void test_abi_selection_keys() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 30px }",
            "<input id=t type=text value=abcdef>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);   // cursor after "abcdef"
    place_caret_at_end(doc.d, t);
    CHECK(selected(doc.d).empty());

    // Shift+Left twice takes the last two characters.
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_SHIFT, 1);
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_SHIFT, 1);
    CHECK(selected(doc.d) == "ef");

    // Shift+Home reaches back to the start from the ANCHOR, not from where
    // the cursor happens to be: the selection is still the one that started
    // at the end of the value, so it now covers all of it.
    weva_document_key(doc.d, WEVA_KEY_HOME, WEVA_MOD_SHIFT, 1);
    CHECK(selected(doc.d) == "abcdef");

    // An unshifted move drops it and leaves a plain cursor.
    weva_document_key(doc.d, WEVA_KEY_RIGHT, 0, 1);
    CHECK(selected(doc.d).empty());

    // The reported range says which end the user started from, so a host
    // knows which way the selection runs.
    weva_document_key(doc.d, WEVA_KEY_END, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_SHIFT, 1);
    int start = 0, end = 0;
    CHECK(weva_element_selection(doc.d, t, &start, &end) == WEVA_OK);
    CHECK(start == 6);
    CHECK(end == 5);   // backwards, because it was dragged left

    // Select-all is the host's to trigger: the key enum has no letters, so
    // the document cannot see Ctrl+A for itself.
    CHECK(weva_document_select_all(doc.d) == 1);
    CHECK(selected(doc.d) == "abcdef");
    // And a host can set one outright, for its own selection UI.
    CHECK(weva_element_set_selection(doc.d, t, 1, 3) == WEVA_OK);
    CHECK(selected(doc.d) == "bc");
    CHECK(weva_element_set_selection(doc.d, t, 2, 2) == WEVA_OK);
    CHECK(selected(doc.d).empty());   // the two ends together is a cursor
}

// What editing does to a selection: replaces it.
void test_abi_selection_replaces() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px }",
            "<input id=t type=text value=hello>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);

    // Select-all then type is the commonest edit there is.
    weva_document_select_all(doc.d);
    weva_document_text_input(doc.d, "x");
    CHECK(doc.value("#t") == "x");
    CHECK(selected(doc.d).empty());

    // Backspace over a selection takes the selection, not one character.
    weva_element_set_value(doc.d, t, "abcdef");
    weva_element_set_selection(doc.d, t, 1, 4);
    CHECK(selected(doc.d) == "bcd");
    CHECK(weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1) == 1);
    CHECK(doc.value("#t") == "aef");
    CHECK(selected(doc.d).empty());

    // As does Delete.
    weva_element_set_value(doc.d, t, "abcdef");
    weva_element_set_selection(doc.d, t, 2, 5);
    CHECK(weva_document_key(doc.d, WEVA_KEY_DELETE, 0, 1) == 1);
    CHECK(doc.value("#t") == "abf");

    // Typing after a replacement carries on from where the text went in.
    weva_element_set_value(doc.d, t, "abcdef");
    weva_element_set_selection(doc.d, t, 0, 3);
    weva_document_text_input(doc.d, "X");
    weva_document_text_input(doc.d, "Y");
    CHECK(doc.value("#t") == "XYdef");

    // A selection in a textarea works the same, over lines.
    Doc area("html, body { margin: 0 } textarea { display: block; width: 300px; height: 90px }",
             "<textarea id=a>one\ntwo\nthree</textarea>");
    const weva_element_t a = weva_document_query(area.d, "#a");
    weva_document_set_focus(area.d, a);
    weva_element_set_selection(area.d, a, 4, 11);
    CHECK(selected(area.d) == "two\nthr");
    weva_document_text_input(area.d, "-");
    weva_document_update(area.d, 0);
    CHECK(area.value("#a") == "one\n-ee");
}

// A selection you cannot see is not one. The band goes behind the glyphs, in
// both kinds of field.
void test_abi_selection_is_drawn() {
    // The band is the widest blue-ish draw in the frame.
    const auto band_width = [](weva_document_t d) {
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        double widest = 0;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].vertex_count == 0) continue;
            const weva_vertex& v = draws[i].vertices[0];
            // The band, and not the focus ring, which is also blue: the band
            // is translucent by design so the glyphs behind it stay readable,
            // and the ring is opaque.
            if (!(v.b > 0.2f && v.b > v.r * 2 && v.a < 0.8f)) continue;
            double x0 = 1e9, x1 = -1e9;
            for (size_t k = 0; k < draws[i].vertex_count; ++k) {
                x0 = std::fmin(x0, draws[i].vertices[k].x);
                x1 = std::fmax(x1, draws[i].vertices[k].x);
            }
            widest = std::fmax(widest, x1 - x0);
        }
        return widest;
    };

    Doc doc("html, body { margin: 0 }"
            "input { display: block; width: 200px; height: 30px; font-size: 16px;"
            "        color: #000000; background: #ffffff }",
            "<input id=t type=text value=abcdef>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);
    weva_document_update(doc.d, 0);
    CHECK(band_width(doc.d) == 0);   // a cursor alone paints no band

    weva_element_set_selection(doc.d, t, 0, 2);
    weva_document_update(doc.d, 0);
    const double two = band_width(doc.d);
    CHECK(two > 0);

    // Wider selection, wider band.
    weva_element_set_selection(doc.d, t, 0, 5);
    weva_document_update(doc.d, 0);
    CHECK(band_width(doc.d) > two);

    // Dropping the selection takes the band with it.
    weva_element_set_selection(doc.d, t, 3, 3);
    weva_document_update(doc.d, 0);
    CHECK(band_width(doc.d) == 0);

    weva_element_set_selection(doc.d, t, 0, 5);
    weva_element_set_style(doc.d, t, "content-visibility", "hidden");
    weva_document_update(doc.d, 0);
    CHECK(band_width(doc.d) == 0);
    weva_element_set_style(doc.d, t, "content-visibility", "visible");
    weva_document_update(doc.d, 0);
    CHECK(band_width(doc.d) > two);

    // And in a textarea, where the value is laid out as runs.
    Doc area("html, body { margin: 0 }"
             "textarea { display: block; width: 300px; height: 90px; font-size: 16px;"
             "           color: #000000; background: #ffffff }",
             "<textarea id=a>alpha beta</textarea>");
    const weva_element_t a = weva_document_query(area.d, "#a");
    weva_document_set_focus(area.d, a);
    weva_document_update(area.d, 0);
    CHECK(band_width(area.d) == 0);
    weva_element_set_selection(area.d, a, 0, 5);
    weva_document_update(area.d, 0);
    CHECK(band_width(area.d) > 0);
}

// Clicking into a field puts the cursor where you clicked. Until this, a click
// focused the field and dropped the cursor at the end, so the middle of a
// value could not be reached with the mouse at all.
void test_abi_click_places_caret() {
    Doc doc("html, body { margin: 0 }"
            "input { display: block; width: 300px; height: 30px; font-size: 16px;"
            "        padding: 0; border: 0 }",
            "<input id=t type=text value=abcdefghij>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#t", &x, &y, &w, &h);

    // Hard left is before the first character.
    weva_document_set_pointer(doc.d, x + 1, y + h / 2, 1);
    weva_document_set_pointer(doc.d, x + 1, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    int start = 0, end = 0;
    weva_element_selection(doc.d, t, &start, &end);
    CHECK(end == 0);

    // Far to the right of the text is after the last character.
    weva_document_set_pointer(doc.d, x + w - 1, y + h / 2, 1);
    weva_document_set_pointer(doc.d, x + w - 1, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    weva_element_selection(doc.d, t, &start, &end);
    CHECK(end == 10);

    // And somewhere in the middle lands in the middle -- typing there goes
    // where the click was, which is the whole point.
    const double middle = x + (x + w - 1 - x) * 0.0;
    (void)middle;
    weva_document_set_pointer(doc.d, x + 40, y + h / 2, 1);
    weva_document_set_pointer(doc.d, x + 40, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    weva_element_selection(doc.d, t, &start, &end);
    CHECK(end > 0);
    CHECK(end < 10);
    const int clicked = end;
    weva_document_text_input(doc.d, "-");
    CHECK(doc.value("#t").substr(static_cast<size_t>(clicked), 1) == "-");

    // A click also drops whatever was selected.
    weva_document_select_all(doc.d);
    weva_document_set_pointer(doc.d, x + 40, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    weva_element_selection(doc.d, t, &start, &end);
    CHECK(start == end);
}

// Dragging from a press selects, and keeps selecting once the pointer has left
// the field.
void test_abi_drag_selects() {
    Doc doc("html, body { margin: 0 }"
            "input { display: block; width: 300px; height: 30px; font-size: 16px;"
            "        padding: 0; border: 0 }",
            "<input id=t type=text value=abcdefghij>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#t", &x, &y, &w, &h);

    weva_document_set_pointer(doc.d, x + 1, y + h / 2, 1);      // press at the start
    weva_document_set_pointer(doc.d, x + 40, y + h / 2, 1);     // drag right
    weva_document_update(doc.d, 0);
    int start = 0, end = 0;
    weva_element_selection(doc.d, t, &start, &end);
    CHECK(start == 0);
    CHECK(end > 0);
    const std::string dragged = selected(doc.d);
    CHECK(!dragged.empty());
    CHECK(doc.value("#t").rfind(dragged, 0) == 0);   // from the beginning

    // Further right takes more, and past the end takes everything.
    weva_document_set_pointer(doc.d, x + w + 200, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    CHECK(selected(doc.d) == "abcdefghij");

    // Releasing leaves the selection where it was; moving after that does not
    // extend it.
    weva_document_set_pointer(doc.d, x + w + 200, y + h / 2, 0);
    weva_document_set_pointer(doc.d, x + 20, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    CHECK(selected(doc.d) == "abcdefghij");
}

// A double click takes the word. The platform decides what a double click IS
// -- the document is never told the time -- so a host says when one happened.
void test_abi_double_click_selects_word() {
    Doc doc("html, body { margin: 0 }"
            "input { display: block; width: 300px; height: 30px; font-size: 16px;"
            "        padding: 0; border: 0 }",
            "<input id=t type=text value='hello brave world'>");
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#t", &x, &y, &w, &h);

    // Over the first word.
    CHECK(weva_document_select_word_at(doc.d, x + 10, y + h / 2) == 1);
    CHECK(selected(doc.d) == "hello");

    // Over a later one: the word, not the line.
    CHECK(weva_document_select_word_at(doc.d, x + 60, y + h / 2) == 1);
    const std::string word = selected(doc.d);
    CHECK(word == "brave" || word == "world");
    CHECK(word.find(' ') == std::string::npos);

    // Over nothing that takes text, nothing happens.
    Doc plain("html, body { margin: 0 } .box { width: 100px; height: 40px }",
              "<div id=b class=box></div>");
    CHECK(weva_document_select_word_at(plain.d, 20, 20) == 0);

    // In a textarea, a word on the second line is the word on that line.
    Doc area("html, body { margin: 0 }"
             "textarea { display: block; width: 300px; height: 90px; font-size: 16px;"
             "           padding: 0; border: 0; line-height: 20px }",
             "<textarea id=a>alpha beta\ngamma delta</textarea>");
    double ax = 0, ay = 0, aw = 0, ah = 0;
    area.bounds("#a", &ax, &ay, &aw, &ah);
    CHECK(weva_document_select_word_at(area.d, ax + 10, ay + 30) == 1);
    CHECK(selected(area.d) == "gamma");
}

namespace {

const char* kSelectCss =
    "html, body { margin: 0 }"
    "select { display: block; width: 160px; height: 28px; font-size: 14px }";
const char* kSelectHtml =
    "<select id=s>"
    "<option value=low>Low</option>"
    "<option value=med selected>Medium</option>"
    "<option value=high>High</option>"
    "</select>";

}   // namespace

// A <select> rendered its chosen option and a little caret bar, and clicking
// it did nothing -- so a settings screen could show a choice but never offer
// one. The list is not in the box tree: it covers whatever it opens over, so
// it is painted after everything and hit tested before everything.
void test_abi_select_opens_and_chooses() {
    {
        Doc grouped("select{width:auto;min-width:0;padding:0;border:0;appearance:none;font:20px monospace}",
                    "<select id=s><optgroup label='A much longer group heading'><option>Low</option></optgroup></select><select id=plain><option>Low</option></select>");
        double x = 0, y = 0, width = 0, height = 0, plain_width = 0;
        grouped.bounds("#s", &x, &y, &width, &height);
        grouped.bounds("#plain", &x, &y, &plain_width, &height);
        CHECK(width > plain_width + 20);
        const double original = width;
        const auto select = weva_document_query(grouped.d, "#s");
        weva_element_set_attribute(grouped.d, select, "style", "word-spacing:3px");
        weva_document_update(grouped.d, 0);
        grouped.bounds("#s", &x, &y, &width, &height);
        CHECK(std::abs(width - original - 9) < 1.01);
        weva_element_set_attribute(grouped.d, select, "style", "letter-spacing:2px");
        weva_document_update(grouped.d, 0);
        grouped.bounds("#s", &x, &y, &width, &height);
        CHECK(std::abs(width - original - 14) < 1.01);
    }
    for (const char* layout : {"", "display:flex", "display:grid;grid-template-columns:max-content"}) {
        const std::string html = std::string("<div style='") + layout + "'><select id=s><option>Low</option><option id=long>Very long quality</option></select></div>";
        Doc natural("select{width:auto;min-width:0;padding:0;border:0;appearance:none;font:20px monospace}", html.c_str());
        double x = 0, y = 0, width = 0, height = 0;
        natural.bounds("#s", &x, &y, &width, &height);
        CHECK(width > 100);
        const double original_width = width;
        const auto select = weva_document_query(natural.d, "#s");
        CHECK(weva_element_set_attribute(natural.d, select, "style", "letter-spacing:2px") == WEVA_OK);
        weva_document_update(natural.d, 0);
        natural.bounds("#s", &x, &y, &width, &height);
        CHECK(width > original_width + 30);
        CHECK(weva_element_set_attribute(natural.d, select, "style", "word-spacing:3px") == WEVA_OK);
        weva_document_update(natural.d, 0);
        natural.bounds("#s", &x, &y, &width, &height);
        CHECK(std::abs(width - original_width - 6) < 1.01);
        CHECK(weva_element_set_attribute(natural.d, select, "style", "display:block") == WEVA_OK);
        weva_document_update(natural.d, 0);
        natural.bounds("#s", &x, &y, &width, &height);
        CHECK(std::abs(width - original_width) < 0.01);
        const auto option = weva_document_query(natural.d, "#long");
        CHECK(weva_element_set_attribute(natural.d, option, "label", "Mid") == WEVA_OK);
        weva_document_update(natural.d, 0);
        natural.bounds("#s", &x, &y, &width, &height);
        CHECK(width > 0 && width < original_width);
    }
    // Settings controls must accept compact authored widths without requiring
    // authors to know and undo a separate minimum in the default stylesheet.
    for (const auto& sizing : std::vector<std::pair<std::string, double>>{
             {"width:90px", 90}, {"width:50%", 160},
             {"width:90px;min-width:110px", 110}, {"width:90px;max-width:75px", 75},
             {"", 218}}) {
        for (const char* mode : {"", " multiple size=3"}) {
            const std::string html = std::string("<div style='width:320px'><select id=s") + mode +
                "><option>Low</option><option>Medium</option></select></div>";
            const std::string css = "select{" + sizing.first + "}";
            Doc sized(css.c_str(), html.c_str());
            double x = 0, y = 0, width = 0, height = 0;
            sized.bounds("#s", &x, &y, &width, &height);
            CHECK(std::abs(width - sizing.second) < 0.01);
        }
    }
    // The overlay stays upright and anchors to the painted control bounds.
    for (bool above : {false, true}) {
        Doc moved(kSelectCss, kSelectHtml);
        const auto select = weva_document_query(moved.d, "#s");
        const double top = above ? 240 : 40;
        const std::string style = "transform-origin:0 0;transform:translate(100px," +
            std::to_string(top) + "px) scale(1.5)";
        weva_element_set_attribute(moved.d, select, "style", style.c_str());
        weva_document_update(moved.d, 0);
        weva_document_set_pointer(moved.d, 220, top + 20, 1);
        weva_document_set_pointer(moved.d, 220, top + 20, 0);
        CHECK(weva_document_open_select_element(moved.d) == select);
        // 14px stub font: three 28px rows, flipped above near the viewport edge.
        const double popup_top = above ? top - 84 : top + 42;
        weva_document_set_pointer(moved.d, 300, popup_top + 68, 1);
        weva_document_update(moved.d, 0);
        CHECK(moved.value("#s") == "high");
        CHECK(weva_document_open_select_element(moved.d) == WEVA_ELEMENT_NONE);
    }
    Doc doc(kSelectCss, kSelectHtml);
    const weva_element_t s = weva_document_query(doc.d, "#s");
    CHECK(doc.value("#s") == "med");   // the value is the chosen option's

    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#s", &x, &y, &w, &h);
    // Focused but closed, which is what it goes back to once a row is chosen:
    // comparing against the UNfocused frame would count the focus ring as the
    // list, and the list would look like it never went away.
    weva_document_set_focus(doc.d, s);
    weva_document_update(doc.d, 0);
    size_t closed_draws = 0;
    weva_document_draws(doc.d, &closed_draws);

    // Pressing it opens the list, and the list is drawn.
    weva_document_set_pointer(doc.d, x + w / 2, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_open_select_element(doc.d) == s);
    size_t open_draws = 0;
    weva_document_draws(doc.d, &open_draws);
    CHECK(open_draws > closed_draws);

    // The rows sit under the control. Clicking the third chooses it, and the
    // choice lands in the DOM as `selected` -- so the paint, a stylesheet and
    // a script all read the same thing.
    weva_document_set_pointer(doc.d, x + w / 2, y + h + 2, 0);          // release
    const double row = y + h + 12;                                       // first row
    weva_document_set_pointer(doc.d, x + w / 2, row + 28 * 2, 1);        // third row
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#s") == "high");
    CHECK(weva_document_open_select_element(doc.d) == WEVA_ELEMENT_NONE);   // and it closed

    // The list is gone from the frame with it.
    size_t after = 0;
    weva_document_draws(doc.d, &after);
    CHECK(after == closed_draws);

    // The change reaches the host, which is what a script binds to.
    bool announced = false;
    for (const weva_event& e : doc.drain()) {
        if (e.kind == WEVA_EVENT_VALUE_CHANGED && e.target == s) {
            announced = true;
            CHECK(std::string(e.text) == "high");
        }
    }
    CHECK(announced);
}

// Clicking away cancels, and Escape leaves the value alone -- the difference
// between cancelling and choosing.
void test_abi_select_cancels() {
    Doc doc(kSelectCss, kSelectHtml);
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#s", &x, &y, &w, &h);

    weva_document_set_pointer(doc.d, x + w / 2, y + h / 2, 1);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_open_select_element(doc.d) != WEVA_ELEMENT_NONE);

    // A press well away from the list closes it and changes nothing.
    weva_document_set_pointer(doc.d, x + w / 2, y + h / 2, 0);
    weva_document_set_pointer(doc.d, 350, 260, 1);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_open_select_element(doc.d) == WEVA_ELEMENT_NONE);
    CHECK(doc.value("#s") == "med");

    // Escape does the same from the keyboard.
    weva_document_set_pointer(doc.d, 350, 260, 0);
    weva_document_open_select(doc.d, weva_document_query(doc.d, "#s"));
    CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 1);
    CHECK(weva_document_open_select_element(doc.d) == WEVA_ELEMENT_NONE);
    CHECK(doc.value("#s") == "med");
}

// The keyboard drives it: a closed select changes value with the arrows, an
// open one walks its list and takes what is highlighted on Enter.
void test_abi_select_keys() {
    Doc doc(kSelectCss, kSelectHtml);
    const weva_element_t s = weva_document_query(doc.d, "#s");
    weva_document_set_focus(doc.d, s);

    // Closed: the arrows move through the options, as they do in a browser.
    CHECK(weva_document_key(doc.d, WEVA_KEY_DOWN, 0, 1) == 1);
    CHECK(doc.value("#s") == "high");
    CHECK(weva_document_key(doc.d, WEVA_KEY_UP, 0, 1) == 1);
    CHECK(doc.value("#s") == "med");
    // And stop at the ends rather than wrapping.
    weva_document_key(doc.d, WEVA_KEY_UP, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_UP, 0, 1);
    CHECK(doc.value("#s") == "low");

    // Enter opens it.
    CHECK(weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1) == 1);
    CHECK(weva_document_open_select_element(doc.d) == s);

    // Open: the arrows walk the list WITHOUT changing the value until Enter.
    weva_document_key(doc.d, WEVA_KEY_DOWN, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_DOWN, 0, 1);
    CHECK(doc.value("#s") == "low");
    CHECK(weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1) == 1);
    CHECK(doc.value("#s") == "high");
    CHECK(weva_document_open_select_element(doc.d) == WEVA_ELEMENT_NONE);

    // Home and End reach the ends of the list.
    weva_document_open_select(doc.d, s);
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    CHECK(doc.value("#s") == "low");
    weva_document_open_select(doc.d, s);
    weva_document_key(doc.d, WEVA_KEY_END, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    CHECK(doc.value("#s") == "high");
}

// What the document shows follows the choice, and options inside an
// <optgroup> are part of the same list.
void test_abi_select_reflects_choice() {
    Doc doc(kSelectCss,
            "<select id=s>"
            "<optgroup label=Speed><option value=slow>Slow</option>"
            "<option value=fast>Fast</option></optgroup>"
            "<option value=other selected>Other</option>"
            "</select>");
    CHECK(doc.value("#s") == "other");

    // Three options, the two in the group included: choosing the second by
    // index reaches into the group.
    weva_document_open_select(doc.d, weva_document_query(doc.d, "#s"));
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_DOWN, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#s") == "fast");

    // An option with no `value` reports its own text, which is how the markup
    // for a plain list of choices works.
    Doc plain(kSelectCss,
              "<select id=s><option>First</option><option selected>Second</option></select>");
    CHECK(plain.value("#s") == "Second");

    // A host can open and close it directly, for its own input routing.
    CHECK(weva_document_open_select(plain.d, weva_document_query(plain.d, "#s")) == 1);
    CHECK(weva_document_open_select_element(plain.d) != WEVA_ELEMENT_NONE);
    CHECK(weva_document_open_select(plain.d, WEVA_ELEMENT_NONE) == 1);
    CHECK(weva_document_open_select_element(plain.d) == WEVA_ELEMENT_NONE);
    // And asking to open something that is not a select is a no.
    Doc div(kSelectCss, "<div id=d></div>");
    CHECK(weva_document_open_select(div.d, weva_document_query(div.d, "#d")) == 0);
}

// A disabled control is not a target. It still occupies its space -- what is
// behind it is not hit either -- but nothing about it responds, which is what
// makes it look disabled rather than merely grey.
void test_abi_disabled_controls_are_inert() {
    Doc doc("html, body { margin: 0 }"
            "input, button { display: block; width: 120px; height: 24px }"
            "button:hover { background: #ff0000 }",
            "<button id=go disabled>Go</button>"
            "<input id=c type=checkbox disabled>"
            "<input id=t type=text value=abc disabled>"
            "<input id=live type=checkbox>");
    double x = 0, y = 0, w = 0, h = 0;

    // A click on it raises nothing and changes nothing.
    doc.bounds("#c", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#c").empty());
    for (const weva_event& e : doc.drain()) {
        CHECK(e.kind != WEVA_EVENT_CLICK);
    }

    // Nor is it hovered, so `:hover` cannot light it up.
    doc.bounds("#go", &x, &y, &w, &h);
    size_t before = 0;
    weva_document_draws(doc.d, &before);
    weva_document_set_pointer(doc.d, x + w / 2, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    size_t after = 0;
    weva_document_draws(doc.d, &after);
    CHECK(after == before);

    // And it cannot take focus, so a host cannot put the keyboard where the
    // user could not.
    CHECK(weva_document_set_focus(doc.d, weva_document_query(doc.d, "#t")) != WEVA_OK);
    weva_document_text_input(doc.d, "z");
    CHECK(doc.value("#t") == "abc");

    // The one beside it still works, so this is disabling and not breaking.
    doc.bounds("#live", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#live") == "on");
}

// A list longer than the cap scrolls. Without this its last options could be
// neither seen nor clicked -- they were simply not drawn.
void test_abi_select_long_list_scrolls() {
    std::string html = "<select id=s>";
    for (int i = 0; i < 30; ++i) {
        html += "<option value=v" + std::to_string(i) + ">Option " + std::to_string(i) +
                "</option>";
    }
    html += "</select>";
    Doc doc("html, body { margin: 0 }"
            "select { display: block; width: 160px; height: 28px; font-size: 14px }",
            html.c_str());
    const weva_element_t s = weva_document_query(doc.d, "#s");
    CHECK(doc.value("#s") == "v0");

    // The keyboard reaches the end of a list far longer than the box: End
    // takes the highlight there, and the list follows it.
    weva_document_open_select(doc.d, s);
    weva_document_key(doc.d, WEVA_KEY_END, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#s") == "v29");

    // And back to the top.
    weva_document_open_select(doc.d, s);
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#s") == "v0");

    // A wheel over the open list moves the list rather than the page, and
    // what is under the pointer afterwards is a LATER option -- which is the
    // proof the rows moved and not just a counter.
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#s", &x, &y, &w, &h);
    weva_document_open_select(doc.d, s);
    weva_document_update(doc.d, 0);
    const double first_row_y = y + h + 4;
    weva_document_set_pointer(doc.d, x + w / 2, first_row_y, 0);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_scroll(doc.d, x + w / 2, first_row_y, 0, 40) == 1);
    weva_document_update(doc.d, 0);
    weva_document_set_pointer(doc.d, x + w / 2, first_row_y, 1);
    weva_document_update(doc.d, 0);
    const std::string chosen = doc.value("#s");
    CHECK(chosen != "v0");   // the row at the top of the list is no longer the first

    // Reopening starts where the chosen option is, so a long list does not
    // open at the top with the selection out of sight.
    weva_document_open_select(doc.d, s);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#s") == chosen);
}

// A checked checkbox has a TICK on it, not just a coloured square. At 13px an
// accent-coloured box with nothing in it reads as "some state" rather than as
// checked -- there is nothing to tell it from an unchecked box that happens to
// be filled.
void test_abi_checkbox_draws_a_tick() {
    const auto ink = [](weva_document_t d, bool light) {
        // The lightest, or darkest, opaque vertex in the frame: the tick is
        // drawn to contrast with the accent under it.
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        double best = light ? 0.0 : 1.0;
        for (size_t i = 0; i < count; ++i) {
            for (size_t v = 0; v < draws[i].vertex_count; ++v) {
                const weva_vertex& p = draws[i].vertices[v];
                if (p.a < 0.9f) continue;
                const double l = 0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b;
                best = light ? std::fmax(best, l) : std::fmin(best, l);
            }
        }
        return best;
    };

    const char* css = "html, body { margin: 0; background: #202020 }"
                      "input { display: block; margin: 4px }"
                      "input:checked { accent-color: #2ea043 }";
    Doc off(css, "<input id=c type=checkbox>");
    Doc on(css, "<input id=c type=checkbox checked>");
    size_t off_draws = 0, on_draws = 0;
    weva_document_draws(off.d, &off_draws);
    weva_document_draws(on.d, &on_draws);
    CHECK(on_draws > off_draws);          // the fill and the tick
    CHECK(ink(on.d, true) > 0.8);         // a white tick on the green

    // The tick turns dark on a light accent, so it stays visible whatever
    // colour the page chose.
    Doc pale("html, body { margin: 0; background: #202020 }"
             "input { display: block; margin: 4px }"
             "input:checked { accent-color: #f5f5f5 }",
             "<input id=c type=checkbox checked>");
    CHECK(ink(pale.d, false) < 0.1);
}

// A list box -- a <select> with `size` or `multiple` -- lays its options out
// in flow instead of hiding them behind a closed control. They rendered and
// nothing more: a keybind list or a server list could be shown and never used.
void test_abi_list_box_selects() {
    Doc doc("html, body { margin: 0 }"
            "select { display: block; width: 180px; height: 90px; padding: 0; border: 0 }"
            "option { font-size: 14px }",
            "<select id=s size=4>"
            "<option value=a>Alpha</option>"
            "<option value=b selected>Bravo</option>"
            "<option value=c>Charlie</option>"
            "</select>");
    CHECK(doc.value("#s") == "b");

    // Clicking a row chooses it, and only it.
    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#s option:nth-child(3)", &x, &y, &w, &h);
    CHECK(w > 0);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#s") == "c");
    // Which the DOM holds, so `:checked` can style the chosen row.
    char buf[8] = {0};
    CHECK(weva_element_attribute(doc.d, weva_document_query(doc.d, "#s option:nth-child(2)"),
                                 "selected", buf, sizeof(buf)) == 0);

    // The change reaches the host against the SELECT, not the option: that is
    // the control a script binds to.
    doc.drain();
    doc.bounds("#s option:nth-child(1)", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    bool announced = false;
    for (const weva_event& e : doc.drain()) {
        if (e.kind == WEVA_EVENT_VALUE_CHANGED &&
            e.target == weva_document_query(doc.d, "#s")) {
            announced = true;
            CHECK(std::string(e.text) == "a");
        }
    }
    CHECK(announced);
}

// A plain click replaces selection. Modifier-aware toggle/range and drag
// behavior is covered in test_select_controls.cpp.
void test_abi_list_box_multiple() {
    Doc doc("html, body { margin: 0 }"
            "select { display: block; width: 180px; height: 90px; padding: 0; border: 0 }"
            "option { font-size: 14px }",
            "<select id=s multiple>"
            "<option value=a>Alpha</option>"
            "<option value=b>Bravo</option>"
            "<option value=c>Charlie</option>"
            "</select>");
    CHECK(doc.value("#s").empty());

    double x = 0, y = 0, w = 0, h = 0;
    doc.bounds("#s option:nth-child(1)", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#s") == "a");

    doc.bounds("#s option:nth-child(3)", &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#s") == "c");

    // Clicking the same choice again preserves it.
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#s") == "c");

    // A disabled list takes nothing at all, like every other disabled control.
    Doc off("html, body { margin: 0 }"
            "select { display: block; width: 180px; height: 90px; padding: 0; border: 0 }"
            "option { font-size: 14px }",
            "<select id=s multiple disabled><option value=a>Alpha</option></select>");
    off.bounds("#s option:nth-child(1)", &x, &y, &w, &h);
    off.click(x + w / 2, y + h / 2);
    CHECK(off.value("#s").empty());
}

// The cascade shares one element's match set with another that hashes the
// same, and an ANCESTOR's attributes are part of what makes two elements
// different: `select[size] option { display: block }` matches on the parent's
// attribute, so a plain <select> and a <select size> that fold alike hand
// their options the same rules.
//
// It was quiet and total: a list box AFTER a plain select showed nothing. Its
// options took `display: none` from the earlier select's option, computed
// under the same key, so the rows were not merely unstyled -- they generated
// no boxes at all.
//
// This lives here rather than beside the cascade's own tests because it only
// appears through a whole document: the fixture there computes an element at a
// time, which does not share a match set between two of them.
void test_abi_ancestor_attribute_reaches_the_cascade() {
    // No ids anywhere on the elements being compared, and none on their
    // parents: an id is folded into the key too, so giving the two selects
    // different ones would separate them for a reason that has nothing to do
    // with the attribute this is about -- which is exactly how the first
    // attempt at this test passed against the bug it was written for.
    Doc doc("html, body { margin: 0 }"
            "select { display: block; width: 200px; height: 80px; padding: 0; border: 0 }",
            "<select><option>Only</option></select>"
            "<select size=3><option>Alpha</option><option>Bravo</option></select>");
    double x = 0, y = 0, w = 0, h = 0;
    // The closed select's option is `display: none` and generates no box.
    CHECK(weva_element_bounds(doc.d,
                              weva_document_query(doc.d, "select:nth-of-type(1) option"),
                              &x, &y, &w, &h) == WEVA_ERR_NOT_FOUND);
    // The list box's are laid out in flow and generate one each -- which they
    // did not while they shared the closed select's option's match set.
    CHECK(weva_element_bounds(doc.d,
                              weva_document_query(doc.d, "select:nth-of-type(2) option"),
                              &x, &y, &w, &h) == WEVA_OK);
    CHECK(h > 0);

    // The other order, since a cache is only wrong one way at a time.
    Doc other("html, body { margin: 0 }"
              "select { display: block; width: 200px; height: 80px; padding: 0; border: 0 }",
              "<select size=3><option>Alpha</option></select>"
              "<select><option>Only</option></select>");
    CHECK(weva_element_bounds(other.d,
                              weva_document_query(other.d, "select:nth-of-type(1) option"),
                              &x, &y, &w, &h) == WEVA_OK);
    CHECK(weva_element_bounds(other.d,
                              weva_document_query(other.d, "select:nth-of-type(2) option"),
                              &x, &y, &w, &h) == WEVA_ERR_NOT_FOUND);

    // And the general shape, with nothing to do with form controls: two
    // identical children whose parents differ only by an attribute a rule
    // selects on.
    Doc generic("html, body { margin: 0 }"
                "span { display: block; height: 10px }"
                "div[data-open] span { height: 30px }",
                "<div><span></span></div><div data-open><span></span></div>");
    CHECK(generic.height("div:nth-of-type(1) span") == 10);
    CHECK(generic.height("div:nth-of-type(2) span") == 30);
}

// More rows than fit is the normal case for a keybind or a server list, so a
// list box scrolls: without it the rows past the edge can be neither seen nor
// clicked, which is the same hole the dropdown had.
void test_abi_list_box_scrolls() {
    Doc doc("html, body { margin: 0 }"
            "select { display: block; width: 200px; height: 60px; padding: 0; border: 0 }"
            "option { font-size: 14px }",
            "<select id=s size=3>"
            "<option value=a>Alpha</option><option value=b>Bravo</option>"
            "<option value=c>Charlie</option><option value=d>Delta</option>"
            "<option value=e>Echo</option></select>");
    double y = 0, most = 0;
    weva_element_scroll(doc.d, weva_document_query(doc.d, "#s"), nullptr, &y, nullptr, &most);
    CHECK(most > 0);   // five rows in a box that holds three

    // The wheel over it moves it, and the rows move with it.
    double x = 0, top = 0, w = 0, h = 0;
    doc.bounds("#s option:nth-child(1)", &x, &top, &w, &h);
    CHECK(weva_document_scroll(doc.d, 100, 30, 0, h) == 1);
    weva_document_update(doc.d, 0);
    double moved = 0;
    doc.bounds("#s option:nth-child(1)", &x, &moved, &w, &h);
    CHECK(moved < top);   // the first row has gone up out of the way

    // And a click still lands on the row you can SEE, not the one that used to
    // be there.
    doc.click(100, 30 + h * 0.5);
    CHECK(doc.value("#s") != "a");
}

// Ctrl turns every motion key into its word-sized version. Without it a user
// fixing a mistyped word has to hold Backspace and count.
void test_abi_word_motion_keys() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 300px; height: 30px }",
            "<input id=t type=text value='the quick brown fox'>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);   // cursor at the end, offset 19
    place_caret_at_end(doc.d, t);

    const auto caret = [&]() {
        int start = 0, end = 0;
        weva_element_selection(doc.d, t, &start, &end);
        return end;
    };

    // Ctrl+Left goes to the START of the word behind, and again to the one
    // before that -- one press per word, not one per character.
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_CTRL, 1);
    CHECK(caret() == 16);   // "fox"
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_CTRL, 1);
    CHECK(caret() == 10);   // "brown"

    // Ctrl+Right goes to the END of the word ahead.
    weva_document_key(doc.d, WEVA_KEY_RIGHT, WEVA_MOD_CTRL, 1);
    CHECK(caret() == 15);
    // Plain Left is still one character, so Ctrl is what changed it.
    weva_document_key(doc.d, WEVA_KEY_LEFT, 0, 1);
    CHECK(caret() == 14);

    // Shift+Ctrl selects by the word, which is how a word gets replaced.
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_CTRL | WEVA_MOD_SHIFT, 1);
    CHECK(selected(doc.d) == "brow");
    weva_document_key(doc.d, WEVA_KEY_LEFT, WEVA_MOD_CTRL | WEVA_MOD_SHIFT, 1);
    CHECK(selected(doc.d) == "quick brow");
}

// Ctrl+Backspace eats a word, which is the only way to correct one without
// holding the key down.
void test_abi_word_delete_keys() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 300px; height: 30px }",
            "<input id=t type=text value='alpha beta gamma'>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);
    place_caret_at_end(doc.d, t);

    weva_document_key(doc.d, WEVA_KEY_BACKSPACE, WEVA_MOD_CTRL, 1);
    CHECK(doc.value("#t") == "alpha beta ");
    weva_document_key(doc.d, WEVA_KEY_BACKSPACE, WEVA_MOD_CTRL, 1);
    CHECK(doc.value("#t") == "alpha ");
    // Plain Backspace is still one character.
    weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1);
    CHECK(doc.value("#t") == "alpha");

    // Forward, from the front.
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_DELETE, WEVA_MOD_CTRL, 1);
    CHECK(doc.value("#t").empty());

    // With something selected, Ctrl changes nothing: the selection goes, and
    // no more, or a user loses text they never highlighted.
    weva_element_set_value(doc.d, t, "one two three");
    weva_element_set_selection(doc.d, t, 4, 7);
    weva_document_key(doc.d, WEVA_KEY_BACKSPACE, WEVA_MOD_CTRL, 1);
    CHECK(doc.value("#t") == "one  three");
}

// Home and End address the line; Ctrl+Home and Ctrl+End address the field.
void test_abi_document_extent_keys() {
    Doc doc("html, body { margin: 0 } textarea { display: block; width: 300px; height: 90px }",
            "<textarea id=a>first line\nsecond line\nthird line</textarea>");
    const weva_element_t a = weva_document_query(doc.d, "#a");
    weva_document_set_focus(doc.d, a);
    place_caret_at_end(doc.d, a);
    const auto caret = [&]() {
        int start = 0, end = 0;
        weva_element_selection(doc.d, a, &start, &end);
        return end;
    };
    const int total = static_cast<int>(std::strlen("first line\nsecond line\nthird line"));
    CHECK(caret() == total);

    // Home stops at the start of the LINE the caret is on.
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    CHECK(caret() == 23);   // the start of the third line
    // Ctrl+Home carries on to the top of the field.
    weva_document_key(doc.d, WEVA_KEY_HOME, WEVA_MOD_CTRL, 1);
    CHECK(caret() == 0);

    weva_document_key(doc.d, WEVA_KEY_END, 0, 1);
    CHECK(caret() == 10);   // the end of "first line"
    weva_document_key(doc.d, WEVA_KEY_END, WEVA_MOD_CTRL, 1);
    CHECK(caret() == total);

    // Shift+Ctrl+Home from the end selects everything, which is how a field
    // gets cleared without a mouse.
    weva_document_key(doc.d, WEVA_KEY_HOME, WEVA_MOD_CTRL | WEVA_MOD_SHIFT, 1);
    CHECK(static_cast<int>(selected(doc.d).size()) == total);
}

// The word a double click takes, over text that is not English.
void test_abi_word_selection_over_cjk() {
    // Three characters, three bytes each. A double click takes ONE of them --
    // the old byte test took all nine, because every byte above ASCII counted
    // as a word character.
    Doc doc("html, body { margin: 0 } input { display: block; width: 300px; height: 30px;"
            " font-size: 20px }",
            "<input id=t type=text value='日本語'>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    CHECK(doc.value("#t").size() == 9);
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(doc.d, t, &x, &y, &w, &h);
    CHECK(weva_document_select_word_at(doc.d, x + 20, y + h / 2) == 1);
    CHECK(selected(doc.d).size() == 3);
}

// Undo, and the grouping that makes it usable: a run of typing is one step.
void test_abi_undo_groups_typing() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 300px; height: 30px }",
            "<input id=t type=text value=''>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);

    // Nothing edited, nothing to undo.
    CHECK(weva_document_undo(doc.d) == 0);

    weva_document_text_input(doc.d, "h");
    weva_document_text_input(doc.d, "e");
    weva_document_text_input(doc.d, "l");
    weva_document_text_input(doc.d, "l");
    weva_document_text_input(doc.d, "o");
    CHECK(doc.value("#t") == "hello");

    // ONE step, not five: undoing a word a letter at a time is not undo.
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#t").empty());
    CHECK(weva_document_undo(doc.d) == 0);

    // And redo puts it back whole.
    CHECK(weva_document_redo(doc.d) == 1);
    CHECK(doc.value("#t") == "hello");
    CHECK(weva_document_redo(doc.d) == 0);
}

// What breaks a typing run: anything that is not typing.
void test_abi_undo_breaks_on_other_edits() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 300px; height: 30px }",
            "<input id=t type=text value=''>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);

    weva_document_text_input(doc.d, "a");
    weva_document_text_input(doc.d, "b");
    weva_document_key(doc.d, WEVA_KEY_BACKSPACE, 0, 1);   // ends the run
    weva_document_text_input(doc.d, "c");
    weva_document_text_input(doc.d, "d");
    CHECK(doc.value("#t") == "acd");

    // Three steps back through three groups: "cd", the backspace, then "ab".
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#t") == "a");
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#t") == "ab");
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#t").empty());

    // Redo walks the same three forward.
    CHECK(weva_document_redo(doc.d) == 1);
    CHECK(doc.value("#t") == "ab");
    CHECK(weva_document_redo(doc.d) == 1);
    CHECK(doc.value("#t") == "a");
    CHECK(weva_document_redo(doc.d) == 1);
    CHECK(doc.value("#t") == "acd");
}

// The parts of undo other than the text.
void test_abi_undo_restores_cursor_and_forgets_scripted_writes() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 300px; height: 30px }",
            "<input id=t type=text value='hello world'>");
    const weva_element_t t = weva_document_query(doc.d, "#t");
    weva_document_set_focus(doc.d, t);

    // Delete a word from the middle, then undo it.
    weva_element_set_selection(doc.d, t, 5, 5);
    weva_document_key(doc.d, WEVA_KEY_DELETE, WEVA_MOD_CTRL, 1);
    CHECK(doc.value("#t") == "hello");
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#t") == "hello world");
    // The cursor is back where the edit happened, not parked at the end --
    // landing at the end after every Ctrl+Z is what makes undo unusable.
    int start = 0, end = 0;
    weva_element_selection(doc.d, t, &start, &end);
    CHECK(end == 5);

    // Typing over a selection is its own step: undoing it has to bring back
    // the text that was replaced, so it cannot join the run after it.
    weva_element_set_selection(doc.d, t, 0, 5);
    weva_document_text_input(doc.d, "X");
    weva_document_text_input(doc.d, "Y");
    CHECK(doc.value("#t") == "XY world");
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#t") == "X world");
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#t") == "hello world");

    // A script writing the value clears the history: the stack describes a
    // field that no longer holds what it described.
    weva_document_text_input(doc.d, "z");
    weva_element_set_value(doc.d, t, "something else");
    CHECK(weva_document_undo(doc.d) == 0);
    CHECK(doc.value("#t") == "something else");
}

// Two fields keep their own history, so undoing in one never reaches into the
// other.
void test_abi_undo_is_per_field() {
    Doc doc("html, body { margin: 0 } input { display: block; width: 200px; height: 30px }",
            "<input id=a type=text value=''><input id=b type=text value=''>");
    const weva_element_t a = weva_document_query(doc.d, "#a");
    const weva_element_t b = weva_document_query(doc.d, "#b");

    weva_document_set_focus(doc.d, a);
    weva_document_text_input(doc.d, "one");
    weva_document_set_focus(doc.d, b);
    weva_document_text_input(doc.d, "two");

    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#b").empty());
    CHECK(doc.value("#a") == "one");   // untouched
    CHECK(weva_document_undo(doc.d) == 0);

    weva_document_set_focus(doc.d, a);
    CHECK(weva_document_undo(doc.d) == 1);
    CHECK(doc.value("#a").empty());
}

// <details> was styled and never opened: the UA sheet hides a closed one's
// body and shows an open one's, but nothing toggled the attribute.
void test_abi_details_toggles() {
    Doc doc("html, body { margin: 0 } summary { height: 20px } p { height: 40px }",
            "<details id=d><summary id=s>More</summary><p id=body>Hidden</p></details>");
    const weva_element_t d = weva_document_query(doc.d, "#d");
    const weva_element_t body = weva_document_query(doc.d, "#body");

    // Closed: the body has no box at all, because the UA sheet says
    // `display: none` on a closed details' children.
    double x = 0, y = 0, w = 0, h = 0;
    CHECK(weva_element_bounds(doc.d, body, &x, &y, &w, &h) != WEVA_OK || h == 0);

    // A click on the summary opens it.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#s"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_query(doc.d, "#d[open]") == d);
    CHECK(weva_element_bounds(doc.d, body, &x, &y, &w, &h) == WEVA_OK);
    CHECK(h == 40);   // the body has a box now

    // And clicking it again closes it, which is the half that a
    // "set open when clicked" implementation gets wrong.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#s"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_query(doc.d, "#d[open]") == WEVA_ELEMENT_NONE);
}

// Which clicks count, and which must not.
void test_abi_details_only_its_own_summary() {
    Doc doc("html, body { margin: 0 } summary { height: 20px } p { height: 40px }"
            " details { display: block }",
            "<details id=outer open><summary id=os>Outer</summary>"
            "<p id=text>Body text</p>"
            "<details id=inner><summary id=is>Inner</summary><p id=ibody>Deep</p></details>"
            "</details>");
    const weva_element_t outer = weva_document_query(doc.d, "#outer");
    const weva_element_t inner = weva_document_query(doc.d, "#inner");
    double x = 0, y = 0, w = 0, h = 0;

    // A click in the open body is not a click on the summary, so it must not
    // collapse what the reader is reading.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#text"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_query(doc.d, "#outer[open]") == outer);

    // The inner summary toggles the INNER one. Its own <details> is the
    // nearest, not the outermost.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#is"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_query(doc.d, "#inner[open]") == inner);
    CHECK(weva_document_query(doc.d, "#outer[open]") == outer);   // untouched
}

// The event a script hangs "load this section the first time it opens" on.
void test_abi_details_reports_the_toggle() {
    Doc doc("html, body { margin: 0 } summary { height: 20px }",
            "<details id=d on-toggle=OnDisclose><summary id=s>More</summary>"
            "<p>Body</p></details>");
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#s"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);

    int toggles = 0;
    std::string handler;
    weva_element_t target = WEVA_ELEMENT_NONE;
    weva_event e{};
    while (weva_document_poll_event(doc.d, &e)) {
        if (e.kind != WEVA_EVENT_TOGGLE) continue;
        ++toggles;
        handler = e.handler;
        target = e.target;
    }
    CHECK(toggles == 1);
    CHECK(handler == "OnDisclose");
    CHECK(target == weva_document_query(doc.d, "#d"));   // the <details>, not the summary
}

// A modal <dialog> gets a `::backdrop` behind it. The UA sheet has carried a
// `::backdrop` rule all along and nothing ever built the box it styles, so the
// rule matched nothing and a modal dialog looked exactly like a non-modal one.
void test_abi_dialog_backdrop() {
    Doc doc("html, body { margin: 0; height: 600px }"
            " dialog { width: 200px; height: 100px; box-sizing: border-box }",
            "<dialog id=d><p>Are you sure?</p></dialog><div id=page>Behind</div>");
    const weva_element_t d = weva_document_query(doc.d, "#d");

    // Closed: no dialog box and no backdrop.
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 0);
    double x = 0, y = 0, w = 0, h = 0;
    CHECK(weva_element_bounds(doc.d, d, &x, &y, &w, &h) != WEVA_OK || h == 0);

    // Non-modal: the dialog shows and there is still no backdrop. That is the
    // whole difference between show() and showModal().
    CHECK(weva_element_show_dialog(doc.d, d, 0) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(weva_element_bounds(doc.d, d, &x, &y, &w, &h) == WEVA_OK);
    CHECK(h == 100);
    CHECK(doc.backdrops() == 0);

    // Modal: now there is one.
    CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_ERR_INVALID_STATE);
    CHECK(weva_element_close_dialog(doc.d, d) == WEVA_OK);
    CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 1);

    // Modality cannot change while open. Close before reopening non-modally.
    CHECK(weva_element_show_dialog(doc.d, d, 0) == WEVA_ERR_INVALID_STATE);
    CHECK(weva_element_close_dialog(doc.d, d) == WEVA_OK);
    CHECK(weva_element_show_dialog(doc.d, d, 0) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 0);

    // And closing puts everything back.
    CHECK(weva_element_close_dialog(doc.d, d) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 0);
    CHECK(weva_element_has_attribute(doc.d, d, "open") == 0);
    CHECK(weva_element_has_attribute(doc.d, d, "data-modal") == 0);

    // Only a <dialog> takes these.
    CHECK(weva_element_show_dialog(doc.d, weva_document_query(doc.d, "#page"), 1) ==
          WEVA_ERR_NOT_FOUND);
}

// An open popover is the other top-layer shape, and shares the machinery.
void test_abi_popover_backdrop() {
    Doc doc("html, body { margin: 0; height: 600px }"
            " [popover] { width: 120px; height: 60px }",
            "<div id=p popover>Menu</div>");
    const weva_element_t p = weva_document_query(doc.d, "#p");
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 0);

    // Authored data attributes are not browser popover state.
    CHECK(weva_element_set_attribute(doc.d, p, "data-popover-open", "") == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 0);
    CHECK(weva_element_show_popover(doc.d, p) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 1);

    CHECK(weva_element_hide_popover(doc.d, p) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 0);
}

namespace {

bool open_popover(weva_document_t d, const char* selector) {
    return weva_element_has_attribute(d, weva_document_query(d, selector),
                                      "data-popover-open") != 0;
}

}   // namespace

// A `<button popovertarget=menu>` works its popover with no script at all --
// which is the point of the attribute. The port recognised `data-popover-open`
// for the backdrop and had nothing that ever set it.
void test_abi_popover_trigger() {
    // Keep a replacement continuation and its captured parent alive across
    // handler mutations; discarded plans must never affect replacement DOM.
    for (int mutation = 0; mutation < 6; ++mutation) {
        Doc lifetime("", "<div id=parent popover><div id=child popover>Child</div></div><div id=target popover>New</div>");
        for (const char* id : {"#parent", "#child"})
            CHECK(weva_element_show_popover(lifetime.d, weva_document_query(lifetime.d, id)) == WEVA_OK);
        weva_event event{};
        while (weva_document_poll_event(lifetime.d, &event)) {}
        const auto target = weva_document_query(lifetime.d, "#target");
        CHECK(weva_element_request_show_popover(lifetime.d, target) == WEVA_OK);
        CHECK(weva_document_poll_event(lifetime.d, &event) == 1);
        CHECK(event.kind == WEVA_EVENT_BEFORE_TOGGLE && event.target == target);
        CHECK(weva_document_poll_event(lifetime.d, &event) == 1);
        CHECK(event.kind == WEVA_EVENT_BEFORE_TOGGLE && event.target == weva_document_query(lifetime.d, "#child"));
        if (mutation == 0) CHECK(weva_element_remove(lifetime.d, target) == WEVA_OK);
        if (mutation == 1) CHECK(weva_element_set_attribute(lifetime.d, target, "popover", "manual") == WEVA_OK);
        if (mutation == 2) CHECK(weva_element_remove(lifetime.d, weva_document_query(lifetime.d, "#parent")) == WEVA_OK);
        if (mutation == 3) {
            const char* fresh = "<div id=target popover>Replacement document</div>";
            CHECK(weva_document_load_html(lifetime.d, fresh, std::strlen(fresh)) == WEVA_OK);
        }
        if (mutation == 4) {
            weva_document_destroy(lifetime.d);
            lifetime.d = nullptr;
            continue;
        }
        int drained = 0;
        while (drained < 100 && weva_document_poll_event(lifetime.d, &event)) ++drained;
        CHECK(drained < 100);
        CHECK(open_popover(lifetime.d, "#target") == (mutation == 2 || mutation == 5));
        CHECK(!open_popover(lifetime.d, "#parent"));
        CHECK(!open_popover(lifetime.d, "#child"));
        weva_document_update(lifetime.d, 0);
    }
    {
        std::string html = "<div id=parent popover><div id=child popover>Child</div></div><div id=target popover>New</div><div id=pinned popover=manual>Pinned</div>";
        for (int i = 0; i < 256; ++i)
            html += "<div id=s" + std::to_string(i) + " popover=manual>Queued</div>";
        Doc saturated("", html.c_str());
        for (const char* id : {"#parent", "#child", "#pinned"})
            CHECK(weva_element_show_popover(saturated.d, weva_document_query(saturated.d, id)) == WEVA_OK);
        weva_event event{};
        while (weva_document_poll_event(saturated.d, &event)) {}
        const auto target = weva_document_query(saturated.d, "#target");
        CHECK(weva_element_request_show_popover(saturated.d, target) == WEVA_OK);
        CHECK(weva_document_poll_event(saturated.d, &event) == 1);
        CHECK(event.kind == WEVA_EVENT_BEFORE_TOGGLE && event.target == target);
        for (int i = 0; i < 256; ++i) {
            const auto selector = "#s" + std::to_string(i);
            CHECK(weva_element_request_show_popover(saturated.d, weva_document_query(saturated.d, selector.c_str())) == WEVA_OK);
        }
        int closing = 0, vetoed = 0;
        while (weva_document_poll_event(saturated.d, &event)) {
            if (event.kind != WEVA_EVENT_BEFORE_TOGGLE) continue;
            if (std::strcmp(event.text, "closed") == 0) {
                ++closing;
                CHECK(weva_element_has_attribute(saturated.d, event.target, "data-popover-open"));
                CHECK(weva_element_request_hide_popover(saturated.d, weva_document_query(saturated.d, "#pinned")) == WEVA_ERR_INVALID_STATE);
            } else {
                CHECK(event.target != target);
                CHECK(weva_document_prevent_default(saturated.d) == 1);
                ++vetoed;
            }
        }
        CHECK(closing == 2 && vetoed == 256);
        CHECK(open_popover(saturated.d, "#target"));
        CHECK(!open_popover(saturated.d, "#parent") && !open_popover(saturated.d, "#child"));
        CHECK(open_popover(saturated.d, "#pinned"));
    }
    {
        std::string html = "<div id=parent popover><div id=child popover>Child</div></div>";
        for (int i = 0; i < 255; ++i)
            html += "<div id=q" + std::to_string(i) + " popover=manual>Queued</div>";
        Doc pressure("", html.c_str());
        for (const char* id : {"#parent", "#child"})
            CHECK(weva_element_show_popover(pressure.d, weva_document_query(pressure.d, id)) == WEVA_OK);
        weva_event event{};
        while (weva_document_poll_event(pressure.d, &event)) {}
        for (int i = 0; i < 255; ++i) {
            const auto selector = "#q" + std::to_string(i);
            CHECK(weva_element_request_show_popover(pressure.d, weva_document_query(pressure.d, selector.c_str())) == WEVA_OK);
        }
        CHECK(weva_element_request_hide_popover(pressure.d, weva_document_query(pressure.d, "#parent")) == WEVA_ERR_INVALID_STATE);
        int closing = 0;
        while (weva_document_poll_event(pressure.d, &event)) {
            if (event.kind != WEVA_EVENT_BEFORE_TOGGLE) continue;
            if (std::strcmp(event.text, "closed") == 0) ++closing;
            else CHECK(weva_document_prevent_default(pressure.d) == 1);
        }
        CHECK(closing == 0);
        CHECK(open_popover(pressure.d, "#parent"));
        CHECK(open_popover(pressure.d, "#child"));
    }
    for (int close_path = 0; close_path < 4; ++close_path) {
        Doc dismiss("", "<div id=parent popover><div id=child popover>Child</div></div><div id=pinned popover=manual>Pinned</div>");
        weva_document_set_popover_request_events(dismiss.d, 1);
        for (const char* id : {"#parent", "#child", "#pinned"})
            CHECK(weva_element_show_popover(dismiss.d, weva_document_query(dismiss.d, id)) == WEVA_OK);
        weva_event event{};
        while (weva_document_poll_event(dismiss.d, &event)) {}
        const auto version = weva_document_transient_version(dismiss.d);
        if (close_path >= 2) {
            CHECK(weva_element_set_attribute(dismiss.d, weva_document_query(dismiss.d, "#parent"), "popover", close_path == 2 ? "manual" : nullptr) == WEVA_OK);
        } else if (close_path == 1) {
            CHECK(weva_element_request_hide_popover(dismiss.d, weva_document_query(dismiss.d, "#parent")) == WEVA_OK);
        } else {
            CHECK(weva_document_dismiss_transients(dismiss.d, version) == 1);
            CHECK(weva_document_dismiss_transients(dismiss.d, version) == 1);
        }
        CHECK(open_popover(dismiss.d, "#child"));
        int closes = 0;
        while (weva_document_poll_event(dismiss.d, &event)) {
            if (event.kind != WEVA_EVENT_BEFORE_TOGGLE) continue;
            CHECK(std::strcmp(event.text, "closed") == 0);
            CHECK(weva_element_has_attribute(dismiss.d, event.target, "data-popover-open"));
            CHECK(weva_document_prevent_default(dismiss.d) == 0);
            CHECK(event.target == weva_document_query(dismiss.d, closes == 0 ? "#child" : "#parent"));
            ++closes;
        }
        CHECK(closes == 2);
        CHECK(!open_popover(dismiss.d, "#child"));
        CHECK(!open_popover(dismiss.d, "#parent"));
        CHECK(open_popover(dismiss.d, "#pinned"));
    }
    // Requests own a default action and must survive notification pressure.
    {
        std::string html;
        for (int i = 0; i < 257; ++i)
            html += "<div id=p" + std::to_string(i) + " popover=manual>Menu</div>";
        Doc pressure("", html.c_str());
        for (int i = 0; i < 256; ++i) {
            const auto selector = "#p" + std::to_string(i);
            CHECK(weva_element_request_show_popover(pressure.d,
                weva_document_query(pressure.d, selector.c_str())) == WEVA_OK);
        }
        CHECK(weva_element_request_show_popover(pressure.d,
            weva_document_query(pressure.d, "#p256")) == WEVA_ERR_INVALID_STATE);
        // Immediate opens add ordinary toggle notifications to a full queue.
        CHECK(weva_element_show_popover(pressure.d,
            weva_document_query(pressure.d, "#p256")) == WEVA_OK);
        weva_event event{};
        int before = 0;
        while (weva_document_poll_event(pressure.d, &event)) {
            if (event.kind == WEVA_EVENT_BEFORE_TOGGLE) {
                ++before;
                CHECK(weva_document_prevent_default(pressure.d) == 1);
            }
        }
        CHECK(before == 256);
        CHECK(!open_popover(pressure.d, "#p0"));
        CHECK(!open_popover(pressure.d, "#p255"));
    }
    // Cancellable opening runs before stack dismissal or autofocus. Handlers
    // may mutate/remove/reload the document; draining must revalidate ownership.
    for (const auto mode : {"auto", "hint", "manual"}) for (int action = 0; action < 8; ++action) {
        const std::string html = std::string("<button id=outside>Outside</button><div id=other popover=") + mode +
            ">Existing</div><section on-beforetoggle=ancestor><div id=target popover=" + mode +
            "><input id=inside autofocus></div></section>";
        Doc request("", html.c_str());
        const auto outside = weva_document_query(request.d, "#outside");
        const auto other = weva_document_query(request.d, "#other");
        const auto target = weva_document_query(request.d, "#target");
        CHECK(weva_document_set_focus(request.d, outside) == WEVA_OK);
        CHECK(weva_element_show_popover(request.d, other) == WEVA_OK);
        weva_event event{};
        while (weva_document_poll_event(request.d, &event)) {}
        CHECK(weva_element_request_show_popover(request.d, target) == WEVA_OK);
        CHECK(weva_element_request_show_popover(request.d, target) == WEVA_OK);
        CHECK(!open_popover(request.d, "#target"));
        CHECK(weva_document_poll_event(request.d, &event) == 1);
        CHECK(event.kind == WEVA_EVENT_BEFORE_TOGGLE);
        CHECK(event.target == target && std::strcmp(event.text, "open") == 0);
        CHECK(event.handler[0] == 0); // non-bubbling
        CHECK(weva_document_focus(request.d) == outside);
        CHECK(open_popover(request.d, "#other"));
        CHECK(!open_popover(request.d, "#target"));
        if (action == 1) CHECK(weva_document_prevent_default(request.d) == 1);
        if (action == 2) CHECK(weva_element_remove(request.d, target) == WEVA_OK);
        if (action == 3) CHECK(weva_element_set_attribute(request.d, target, "popover", nullptr) == WEVA_OK);
        if (action == 4) {
            const char* fresh = "<div id=target popover>Replacement</div>";
            CHECK(weva_document_load_html(request.d, fresh, std::strlen(fresh)) == WEVA_OK);
        }
        if (action == 5) CHECK(weva_element_request_show_popover(request.d, target) == WEVA_OK);
        if (action == 6) {
            weva_document_update(request.d, 0);
            CHECK(!open_popover(request.d, "#target"));
            CHECK(weva_document_prevent_default(request.d) == 1);
        }
        if (action == 7) {
            CHECK(weva_element_show_popover(request.d, target) == WEVA_OK);
            CHECK(weva_element_hide_popover(request.d, target) == WEVA_OK);
            CHECK(weva_document_prevent_default(request.d) == 1);
        }
        int extra_before = 0;
        while (weva_document_poll_event(request.d, &event))
            if (event.kind == WEVA_EVENT_BEFORE_TOGGLE && std::strcmp(event.text, "open") == 0) ++extra_before;
        CHECK(extra_before == 0);
        const bool opened = action == 0 || action == 5;
        CHECK(open_popover(request.d, "#target") == opened);
        if (action != 4 && action != 7) {
            CHECK(open_popover(request.d, "#other") == (!opened || std::strcmp(mode, "manual") == 0));
            CHECK(weva_document_focus(request.d) ==
                (opened ? weva_document_query(request.d, "#inside") : outside));
        }
        CHECK(weva_document_prevent_default(request.d) == 0);
    }
    // Saved focus must not survive destruction/reload as a dangling pointer.
    for (int mutation = 0; mutation < 3; ++mutation) {
        Doc lifetime("", "<button id=outside>Open</button><div id=p popover><input id=field autofocus></div>");
        weva_document_update(lifetime.d, 0);
        const auto outside = weva_document_query(lifetime.d, "#outside");
        const auto popup = weva_document_query(lifetime.d, "#p");
        CHECK(weva_document_set_focus(lifetime.d, outside) == WEVA_OK);
        CHECK(weva_element_show_popover(lifetime.d, popup) == WEVA_OK);
        CHECK(weva_document_focus(lifetime.d) == weva_document_query(lifetime.d, "#field"));
        if (mutation == 0) {
            CHECK(weva_element_remove(lifetime.d, outside) == WEVA_OK);
            CHECK(weva_element_hide_popover(lifetime.d, popup) == WEVA_OK);
        } else if (mutation == 1) CHECK(weva_element_remove(lifetime.d, popup) == WEVA_OK);
        else {
            const char* html = "<button id=replacement>New</button>";
            CHECK(weva_document_load_html(lifetime.d, html, std::strlen(html)) == WEVA_OK);
        }
        weva_document_update(lifetime.d, 0);
        CHECK(weva_document_focus(lifetime.d) == WEVA_ELEMENT_NONE);
    }
    // Chrome sibling, DOM-child and invoker-linked stacks, all mode pairs.
    for (int action = 0; action < 4; ++action)
    for (int relation = 0; relation < 3; ++relation)
    for (const auto first : {"auto", "manual", "hint"})
    for (const auto second : {"auto", "manual", "hint"}) {
        const std::string child = std::string("<div id=child popover=") + second + ">Child</div>";
        const std::string html = std::string("<div id=parent popover=") + first +
            "><button id=trigger popovertarget=child>Open</button>" +
            (relation == 1 ? child : "") + "</div>" + (relation == 1 ? "" : child);
        Doc stack("", html.c_str());
        CHECK(weva_element_show_popover(stack.d, weva_document_query(stack.d, "#parent")) == WEVA_OK);
        if (relation == 2) {
            weva_document_update(stack.d, 0);
            CHECK(weva_document_set_focus(stack.d, weva_document_query(stack.d, "#trigger")) == WEVA_OK);
            CHECK(weva_document_key(stack.d, WEVA_KEY_ENTER, 0, 1) == 1);
        } else CHECK(weva_element_show_popover(stack.d, weva_document_query(stack.d, "#child")) == WEVA_OK);
        const bool dismiss = relation == 0 &&
            ((std::string(second) == "auto" && std::string(first) != "manual") ||
             (std::string(second) == "hint" && std::string(first) == "hint"));
        CHECK(open_popover(stack.d, "#parent") == !dismiss);
        CHECK(open_popover(stack.d, "#child"));
        if (action && relation != 0) {
            const auto parent = weva_document_query(stack.d, "#parent");
            if (action == 1) CHECK(weva_element_hide_popover(stack.d, parent) == WEVA_OK);
            else CHECK(weva_element_set_attribute(stack.d, parent, "popover", action == 2 ? nullptr : "manual") == WEVA_OK);
            const bool manual_parent = std::string(first) == "manual";
            CHECK(open_popover(stack.d, "#parent") == (manual_parent && action == 3));
            CHECK(open_popover(stack.d, "#child") == (manual_parent || std::string(second) == "manual"));
        }
    }
    Doc doc("html, body { margin: 0 } button { display: block; width: 100px; height: 30px }"
            " [popover] { width: 120px; height: 60px }",
            "<button id=b popovertarget=menu><span id=lbl>Open</span></button>"
            "<div id=menu popover>Menu</div>"
            "<div id=elsewhere>Page</div>");
    double x = 0, y = 0, w = 0, h = 0;
    CHECK(!open_popover(doc.d, "#menu"));

    // A click on the trigger opens it -- and a click on the LABEL inside the
    // trigger counts, since that is what a button's text is.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#lbl"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(open_popover(doc.d, "#menu"));

    // Clicking it again closes it: the default action is toggle.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#b"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(!open_popover(doc.d, "#menu"));
}

// `popovertargetaction` pins the direction, for a trigger that only ever
// opens (or only ever closes) rather than flipping.
void test_abi_popover_target_action() {
    Doc doc("html, body { margin: 0 } button { display: block; width: 100px; height: 30px }"
            " [popover] { width: 120px; height: 60px }",
            "<button id=open popovertarget=menu popovertargetaction=show>Open</button>"
            "<button id=shut popovertarget=menu popovertargetaction=hide>Close</button>"
            "<div id=menu popover>Menu</div>");
    double x = 0, y = 0, w = 0, h = 0;
    const auto press = [&](const char* sel) {
        weva_element_bounds(doc.d, weva_document_query(doc.d, sel), &x, &y, &w, &h);
        doc.click(x + w / 2, y + h / 2);
        weva_document_update(doc.d, 0);
    };

    press("#open");
    CHECK(open_popover(doc.d, "#menu"));
    press("#open");
    CHECK(open_popover(doc.d, "#menu"));   // show twice is still open, not closed
    press("#shut");
    CHECK(!open_popover(doc.d, "#menu"));
    press("#shut");
    CHECK(!open_popover(doc.d, "#menu"));
}

// The same light-dismiss question, but with the popover opened by its TRIGGER
// rather than by the ABI -- which is how a user opens one, and a different
// path through the click handler.
void test_abi_popover_light_dismiss_after_trigger() {
    Doc doc("html, body { margin: 0 } button { display: block; width: 100px; height: 30px }"
            " #page { height: 200px }"
            " [popover] { top: 100px; left: 150px; width: 120px; height: 60px }",
            "<button id=b popovertarget=menu>Open</button>"
            "<div id=menu popover><span id=item>Item</span></div>"
            "<div id=page>Page</div>");
    double x = 0, y = 0, w = 0, h = 0;
    const auto press = [&](const char* sel) {
        weva_element_bounds(doc.d, weva_document_query(doc.d, sel), &x, &y, &w, &h);
        doc.click(x + w / 2, y + h / 2);
        weva_document_update(doc.d, 0);
    };

    press("#b");
    CHECK(open_popover(doc.d, "#menu"));
    press("#item");
    CHECK(open_popover(doc.d, "#menu"));   // a click inside keeps it
}

// Light dismiss: the behaviour that makes a menu a menu.
void test_abi_popover_light_dismiss() {
    for (int gesture = 0; gesture < 5; ++gesture) {
        Doc boundary("body{margin:0}#page{height:450px}[popover]{margin:0;position:fixed;left:200px;top:100px;width:100px;height:60px;padding:0;border:0}",
                     "<div id=page><button id=outside>Outside</button></div><div id=p popover>Menu</div>");
        const auto popup = weva_document_query(boundary.d, "#p");
        CHECK(weva_element_show_popover(boundary.d, popup) == WEVA_OK);
        weva_document_update(boundary.d, 0);
        const bool from_inside = gesture == 1 || gesture == 3;
        const bool to_inside = gesture == 2 || gesture == 3;
        weva_document_set_pointer(boundary.d, from_inside ? 220 : 20, from_inside ? 120 : 10, WEVA_BUTTON_PRIMARY);
        if (gesture == 4) weva_document_clear_pointer(boundary.d);
        weva_document_set_pointer(boundary.d, to_inside ? 220 : 20, to_inside ? 120 : 350, 0);
        CHECK(open_popover(boundary.d, "#p") == (gesture != 0));
    }
    Doc doc("html, body { margin: 0 } #page { height: 200px }"
            " [popover] { top: 10px; left: 10px; width: 120px; height: 60px }",
            "<div id=page>Page</div>"
            "<div id=menu popover><span id=item>Item</span></div>"
            "<div id=manual popover=manual>Pinned</div>");
    const weva_element_t menu = weva_document_query(doc.d, "#menu");
    double x = 0, y = 0, w = 0, h = 0;

    CHECK(weva_element_show_popover(doc.d, menu) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(open_popover(doc.d, "#menu"));

    // A click INSIDE it leaves it open -- choosing from a menu must not
    // dismiss the menu before the choice lands.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#item"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(open_popover(doc.d, "#menu"));

    // A click outside closes it.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#page"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h - 5);
    weva_document_update(doc.d, 0);
    CHECK(!open_popover(doc.d, "#menu"));

    // A `manual` popover ignores all of that: it closes when asked and not
    // before, which is what a pinned panel needs.
    const weva_element_t manual = weva_document_query(doc.d, "#manual");
    CHECK(weva_element_show_popover(doc.d, manual) == WEVA_OK);
    weva_document_update(doc.d, 0);
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#page"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h - 5);
    weva_document_update(doc.d, 0);
    CHECK(open_popover(doc.d, "#manual"));
    CHECK(weva_element_hide_popover(doc.d, manual) == WEVA_OK);
    CHECK(!open_popover(doc.d, "#manual"));
}

// Escape closes ONE, and it nests: a submenu goes before the menu it came
// from, and a manual popover in between is stepped over rather than closed.
void test_abi_popover_escape_walks_the_stack() {
    Doc doc("html, body { margin: 0 } [popover] { width: 120px; height: 60px }",
            "<div id=menu popover>Menu<div id=submenu popover>Submenu</div></div>"
            "<div id=pinned popover=manual>Pinned</div>");
    weva_element_show_popover(doc.d, weva_document_query(doc.d, "#menu"));
    weva_element_show_popover(doc.d, weva_document_query(doc.d, "#pinned"));
    weva_element_show_popover(doc.d, weva_document_query(doc.d, "#submenu"));
    weva_document_update(doc.d, 0);

    // Innermost first.
    CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 1);
    CHECK(!open_popover(doc.d, "#submenu"));
    CHECK(open_popover(doc.d, "#pinned"));
    CHECK(open_popover(doc.d, "#menu"));

    // The manual one is skipped, not closed.
    CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 1);
    CHECK(open_popover(doc.d, "#pinned"));
    CHECK(!open_popover(doc.d, "#menu"));

    // With no auto popover left, Escape is not ours to take.
    CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 0);
    CHECK(open_popover(doc.d, "#pinned"));
}

// An open popover is a top-layer host, so it gets the same backdrop a modal
// dialog does -- one mechanism, two shapes.
void test_abi_popover_joins_the_top_layer() {
    Doc doc("html, body { margin: 0; height: 300px }"
            " [popover] { width: 120px; height: 60px }",
            "<div id=menu popover>Menu</div>");
    CHECK(doc.backdrops() == 0);
    weva_element_show_popover(doc.d, weva_document_query(doc.d, "#menu"));
    CHECK(doc.backdrops() == 1);
    weva_element_hide_popover(doc.d, weva_document_query(doc.d, "#menu"));
    CHECK(doc.backdrops() == 0);
}

// Clicking the word beside a checkbox toggles it. Every UI works this way, and
// the port had no <label> handling at all -- so only a hit on the 13px box
// itself did anything, including in this repo's own Godot demo.
void test_abi_label_activates_its_control() {
    Doc doc("html, body { margin: 0 } label { display: block; height: 30px }"
            " input { width: 13px; height: 13px }",
            "<label id=wrap><input id=cb type=checkbox> Shield</label>"
            "<label id=named for=other>Sound</label>"
            "<input id=other type=checkbox>");
    double x = 0, y = 0, w = 0, h = 0;
    const auto checked = [&](const char* sel) {
        return weva_document_query(doc.d, (std::string(sel) + ":checked").c_str()) != WEVA_ELEMENT_NONE;
    };

    // A label WRAPPING a control owns the first one inside it. Click near the
    // right edge, well past the 13px box, on the text.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#wrap"), &x, &y, &w, &h);
    CHECK(!checked("#cb"));
    doc.click(x + w - 5, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(checked("#cb"));
    doc.click(x + w - 5, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(!checked("#cb"));   // and back, so it is a toggle and not a set

    // `for` names one by id, and the control need not be inside the label.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#named"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(checked("#other"));

    // The label also moves the focus, so the keyboard follows the click.
    CHECK(weva_document_focus(doc.d) == weva_document_query(doc.d, "#other"));
}

// The cases a naive forwarding gets wrong.
void test_abi_label_forwarding_edge_cases() {
    Doc doc("html, body { margin: 0 } label { display: block; height: 30px }"
            " input { width: 13px; height: 13px }",
            "<label id=direct><input id=cb type=checkbox> Shield</label>"
            "<label id=off for=disabled>Off</label>"
            "<input id=disabled type=checkbox disabled>"
            "<label id=empty>Nothing here</label>"
            "<label id=slider for=vol>Volume</label>"
            "<input id=vol type=range min=0 max=100 value=40>");
    double x = 0, y = 0, w = 0, h = 0;
    const auto checked = [&](const char* sel) {
        return weva_document_query(doc.d, (std::string(sel) + ":checked").c_str()) != WEVA_ELEMENT_NONE;
    };

    // A click ON the control inside a label is the activation. Forwarding a
    // second one to the same control would toggle it straight back off.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#cb"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(checked("#cb"));

    // A disabled control is not activated by its label either.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#off"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(!checked("#disabled"));

    // A label with no control and no `for` does nothing at all.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#empty"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);   // no crash, nothing changed

    // A slider does NOT move when its label is clicked -- the value comes from
    // where along the track the pointer landed, and it landed on the label.
    // The focus still follows.
    weva_element_bounds(doc.d, weva_document_query(doc.d, "#slider"), &x, &y, &w, &h);
    doc.click(x + w / 2, y + h / 2);
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#vol") == "40");
    CHECK(weva_document_focus(doc.d) == weva_document_query(doc.d, "#vol"));
}

void test_abi_dialog_close_notification() {
    for (int modal : {0, 1}) {
        Doc doc("", "<div on-close=ancestor><dialog id=d on-close=closed><button>OK</button></dialog></div>");
        const auto d = weva_document_query(doc.d, "#d");
        weva_event event{};
        auto drain = [&] { while (weva_document_poll_event(doc.d, &event)) {} };
        CHECK(weva_element_show_dialog(doc.d, d, modal) == WEVA_OK);
        drain();
        CHECK(weva_element_close_dialog(doc.d, d) == WEVA_OK);
        int closed = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind != WEVA_EVENT_CLOSE) continue;
            ++closed;
            CHECK(event.target == d);
            CHECK(std::strcmp(event.handler, "closed") == 0);
        }
        CHECK(closed == 1);
        CHECK(weva_element_close_dialog(doc.d, d) == WEVA_OK);
        CHECK(!weva_document_poll_event(doc.d, &event));
        CHECK(weva_element_set_attribute(doc.d, d, "on-close", nullptr) == WEVA_OK);
        CHECK(weva_element_show_dialog(doc.d, d, modal) == WEVA_OK);
        drain();
        CHECK(weva_element_close_dialog(doc.d, d) == WEVA_OK);
        closed = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind != WEVA_EVENT_CLOSE) continue;
            ++closed;
            CHECK(event.handler[0] == 0); // Does not reach the ancestor handler.
        }
        CHECK(closed == 1);
        CHECK(weva_element_show_dialog(doc.d, d, modal) == WEVA_OK);
        drain();
        CHECK(weva_element_set_attribute(doc.d, d, "open", nullptr) == WEVA_OK);
        CHECK(weva_document_update(doc.d, 0) == WEVA_OK);
        while (weva_document_poll_event(doc.d, &event)) CHECK(event.kind != WEVA_EVENT_CLOSE);
    }
}

void test_abi_dialog_cancel_request() {
    for (int modal : {0, 1}) for (int action = 0; action < 7; ++action) {
        Doc doc("", "<div on-cancel=ancestor><dialog id=d on-cancel=cancelled><button>OK</button></dialog></div>");
        auto d = weva_document_query(doc.d, "#d");
        weva_event event{};
        auto drain = [&] { while (weva_document_poll_event(doc.d, &event)) {} };
        CHECK(weva_document_prevent_default(doc.d) == 0);
        CHECK(weva_element_show_dialog(doc.d, d, modal) == WEVA_OK);
        drain();
        CHECK(weva_element_request_close_dialog(doc.d, d) == WEVA_OK);
        CHECK(weva_element_has_attribute(doc.d, d, "open"));
        CHECK(weva_document_poll_event(doc.d, &event));
        CHECK(event.kind == WEVA_EVENT_CANCEL);
        CHECK(std::strcmp(event.handler, "cancelled") == 0);
        CHECK(weva_element_has_attribute(doc.d, d, "open"));
        if (action == 1) CHECK(weva_document_prevent_default(doc.d) == 1);
        if (action == 2 || action == 3) {
            CHECK(weva_element_close_dialog(doc.d, d) == WEVA_OK);
            if (action == 3) CHECK(weva_element_show_dialog(doc.d, d, modal) == WEVA_OK);
        }
        if (action == 4) CHECK(weva_element_remove(doc.d, d) == WEVA_OK);
        if (action == 6) {
            CHECK(weva_element_set_attribute(doc.d, d, "open", nullptr) == WEVA_OK);
            CHECK(weva_element_set_attribute(doc.d, d, "open", "") == WEVA_OK);
        }
        if (action == 5) {
            const char* replacement = "<dialog id=d open>Replacement</dialog>";
            CHECK(weva_document_load_html(doc.d, replacement, std::strlen(replacement)) == WEVA_OK);
            d = weva_document_query(doc.d, "#d");
        }
        int closes = 0;
        while (weva_document_poll_event(doc.d, &event)) if (event.kind == WEVA_EVENT_CLOSE) ++closes;
        CHECK(closes == ((action == 0 || action == 2 || action == 3) ? 1 : 0));
        if (action != 4)
            CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == (action == 1 || action == 3 || action == 5 || action == 6));
        CHECK(weva_document_prevent_default(doc.d) == 0);
    }
    Doc doc("", "<div on-cancel=ancestor><dialog id=d>Hi</dialog></div>");
    auto d = weva_document_query(doc.d, "#d");
    weva_event event{};
    CHECK(weva_element_request_close_dialog(doc.d, d) == WEVA_OK);
    CHECK(!weva_document_poll_event(doc.d, &event));
    CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_OK);
    while (weva_document_poll_event(doc.d, &event)) {}
    // A queue full of pending actions explicitly rejects additional requests.
    for (int i = 0; i < 300; ++i)
        CHECK(weva_element_request_close_dialog(doc.d, d) == (i < 256 ? WEVA_OK : WEVA_ERR_INVALID_STATE));
    CHECK(weva_document_poll_event(doc.d, &event));
    CHECK(event.kind == WEVA_EVENT_CANCEL && event.handler[0] == 0);
    int closes = 0;
    while (weva_document_poll_event(doc.d, &event)) if (event.kind == WEVA_EVENT_CLOSE) ++closes;
    CHECK(closes == 1);
    CHECK(!weva_element_has_attribute(doc.d, d, "open"));
}

void test_abi_dialog_cancel_survives_notifications() {
    for (bool veto : {false, true}) {
        Doc doc("", "<dialog id=d><input id=i value=abc></dialog>");
        auto d = weva_document_query(doc.d, "#d");
        weva_event event{};
        CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_OK);
        while (weva_document_poll_event(doc.d, &event)) {}
        CHECK(weva_element_request_close_dialog(doc.d, d) == WEVA_OK);
        for (int i = 0; i < 600; ++i) weva_document_key(doc.d, WEVA_KEY_OTHER, 0, i % 2);
        CHECK(weva_document_poll_event(doc.d, &event));
        CHECK(event.kind == WEVA_EVENT_CANCEL);
        CHECK(weva_element_has_attribute(doc.d, d, "open"));
        if (veto) CHECK(weva_document_prevent_default(doc.d));
        int closes = 0;
        while (weva_document_poll_event(doc.d, &event)) if (event.kind == WEVA_EVENT_CLOSE) ++closes;
        CHECK(closes == (veto ? 0 : 1));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == veto);
    }
}

void test_abi_dialog_escape_policy() {
    for (bool modal : {false, true}) for (bool lower : {false, true})
    for (const char* policy : {"", "none", "closerequest", "any", "invalid", "NoNe", "CloseRequest"})
    for (bool veto : {false, true}) {
        Doc doc("", "<dialog id=a><button>A</button></dialog><dialog id=b><button>B</button></dialog>");
        const auto a = weva_document_query(doc.d, "#a"), b = weva_document_query(doc.d, "#b");
        CHECK(weva_element_set_attribute(doc.d, b, "closedby", policy) == WEVA_OK);
        if (lower) CHECK(weva_element_show_dialog(doc.d, a, 1) == WEVA_OK);
        CHECK(weva_element_show_dialog(doc.d, b, modal) == WEVA_OK);
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {}
        const bool none = std::strcmp(policy, "none") == 0 || std::strcmp(policy, "NoNe") == 0;
        const bool requested = !none && (modal || std::strcmp(policy, "any") == 0 ||
            std::strcmp(policy, "closerequest") == 0 || std::strcmp(policy, "CloseRequest") == 0);
        CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == int(requested));
        int cancels = 0, closes = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_CANCEL) {
                ++cancels;
                CHECK(event.target == b);
                if (veto) CHECK(weva_document_prevent_default(doc.d));
            }
            if (event.kind == WEVA_EVENT_CLOSE) ++closes;
        }
        CHECK(cancels == int(requested));
        CHECK(closes == int(requested && !veto));
        CHECK(bool(weva_element_has_attribute(doc.d, a, "open")) == lower);
        CHECK(bool(weva_element_has_attribute(doc.d, b, "open")) == (!requested || veto));
    }
}

void test_abi_dialog_attribute_open_order() {
    for (bool markup : {false, true}) for (const char* policy : {"none", "any", "closerequest", "invalid"}) {
        std::string html = "<dialog id=d closedby=" + std::string(policy) + (markup ? " open" : "") + ">Hi</dialog>";
        Doc doc("", html.c_str());
        const auto d = weva_document_query(doc.d, "#d");
        if (!markup) CHECK(weva_element_set_attribute(doc.d, d, "open", "") == WEVA_OK);
        const bool closes = std::strcmp(policy, "any") == 0 || std::strcmp(policy, "closerequest") == 0;
        CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == int(closes));
        weva_event event{};
        int cancels = 0;
        while (weva_document_poll_event(doc.d, &event)) if (event.kind == WEVA_EVENT_CANCEL) ++cancels;
        CHECK(cancels == int(closes));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == !closes);
    }
    Doc doc("", "<div id=host><dialog id=a closedby=any>A</dialog><dialog id=b closedby=any>B</dialog></div>");
    auto a = weva_document_query(doc.d, "#a"), b = weva_document_query(doc.d, "#b");
    const auto escape = [&](weva_element_t expected, bool veto) {
        CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 1);
        weva_event event{};
        int cancels = 0;
        while (weva_document_poll_event(doc.d, &event)) if (event.kind == WEVA_EVENT_CANCEL) {
            ++cancels;
            CHECK(event.target == expected);
            if (veto) CHECK(weva_document_prevent_default(doc.d));
        }
        CHECK(cancels == 1);
    };
    CHECK(weva_element_set_attribute(doc.d, b, "open", "") == WEVA_OK);
    CHECK(weva_element_set_attribute(doc.d, a, "open", "") == WEVA_OK);
    CHECK(weva_element_set_attribute(doc.d, b, "open", "other") == WEVA_OK);
    escape(a, true); // Changing an existing boolean attribute does not reopen.
    CHECK(weva_element_set_attribute(doc.d, b, "open", nullptr) == WEVA_OK);
    CHECK(weva_element_set_attribute(doc.d, b, "open", "") == WEVA_OK);
    escape(b, false);
    escape(a, false);
    const char* added = "<dialog id=c open closedby=any>Inserted</dialog>";
    auto c = weva_element_append_html(doc.d, weva_document_query(doc.d, "#host"), added, std::strlen(added));
    CHECK(c != WEVA_ELEMENT_NONE);
    escape(c, false);
    CHECK(weva_element_set_attribute(doc.d, a, "open", "") == WEVA_OK);
    CHECK(weva_element_remove(doc.d, a) == WEVA_OK);
    const char* replacement = "<dialog id=z open closedby=any>Reloaded</dialog>";
    CHECK(weva_document_load_html(doc.d, replacement, std::strlen(replacement)) == WEVA_OK);
    escape(weva_document_query(doc.d, "#z"), false);
}

void test_abi_dialog_return_values() {
    struct Case { const char* action; const char* argument; const char* handler; bool open; const char* value; };
    // Captured from Chrome/152.0.7977.77, dialog-result-chrome.json.
    const Case cases[] = {
        {"close", "omit", "allow", false, "initial"},
        {"close", "omit", "set", false, "initial"},
        {"close", "omit", "prevent", false, "initial"},
        {"close", "omit", "close", false, "initial"},
        {"close", "empty", "allow", false, ""},
        {"close", "empty", "set", false, ""},
        {"close", "empty", "prevent", false, ""},
        {"close", "empty", "close", false, ""},
        {"close", "value", "allow", false, "accepted"},
        {"close", "value", "set", false, "accepted"},
        {"close", "value", "prevent", false, "accepted"},
        {"close", "value", "close", false, "accepted"},
        {"request", "omit", "allow", false, "initial"},
        {"request", "omit", "set", false, "handler"},
        {"request", "omit", "prevent", true, "initial"},
        {"request", "omit", "close", false, "handler-close"},
        {"request", "empty", "allow", false, ""},
        {"request", "empty", "set", false, ""},
        {"request", "empty", "prevent", true, "initial"},
        {"request", "empty", "close", false, "handler-close"},
        {"request", "value", "allow", false, "accepted"},
        {"request", "value", "set", false, "accepted"},
        {"request", "value", "prevent", true, "initial"},
        {"request", "value", "close", false, "handler-close"},
    };
    for (const auto& row : cases) {
        Doc doc("", "<dialog id=d>Hi</dialog>");
        const auto d = weva_document_query(doc.d, "#d");
        CHECK(weva_element_dialog_return_value(doc.d, d, nullptr, 0) == 0);
        CHECK(weva_element_set_dialog_return_value(doc.d, d, "initial") == WEVA_OK);
        CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_OK);
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {}
        const char* value = std::strcmp(row.argument, "omit") == 0 ? nullptr :
            std::strcmp(row.argument, "empty") == 0 ? "" : "accepted";
        if (std::strcmp(row.action, "close") == 0) {
            CHECK(weva_element_close_dialog_with_value(doc.d, d, value) == WEVA_OK);
        } else CHECK(weva_element_request_close_dialog_with_value(doc.d, d, value) == WEVA_OK);
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind != WEVA_EVENT_CANCEL) continue;
            if (std::strcmp(row.handler, "set") == 0)
                CHECK(weva_element_set_dialog_return_value(doc.d, d, "handler") == WEVA_OK);
            if (std::strcmp(row.handler, "prevent") == 0) CHECK(weva_document_prevent_default(doc.d));
            if (std::strcmp(row.handler, "close") == 0)
                CHECK(weva_element_close_dialog_with_value(doc.d, d, "handler-close") == WEVA_OK);
        }
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == row.open);
        char result[64]{};
        CHECK(weva_element_dialog_return_value(doc.d, d, result, sizeof(result)) == std::strlen(row.value));
        CHECK(std::strcmp(result, row.value) == 0);
        if (!row.open) {
            CHECK(weva_element_close_dialog_with_value(doc.d, d, "ignored") == WEVA_OK);
            weva_element_dialog_return_value(doc.d, d, result, sizeof(result));
            CHECK(std::strcmp(result, row.value) == 0);
        }
        CHECK(!weva_element_has_attribute(doc.d, d, "returnValue"));
        CHECK(!weva_element_has_attribute(doc.d, d, "value"));
    }
    Doc doc("", "<dialog id=d>Hi</dialog>");
    auto d = weva_document_query(doc.d, "#d");
    const char* unicode = "\xF0\x9F\x98\x80" "x";
    CHECK(weva_element_set_dialog_return_value(doc.d, d, unicode) == WEVA_OK);
    char small[4] = {'x','x','x','x'};
    CHECK(weva_element_dialog_return_value(doc.d, d, small, sizeof(small)) == 5);
    CHECK(small[0] == 0); // No partial UTF-8 codepoint.
    char complete[8]{};
    CHECK(weva_element_dialog_return_value(doc.d, d, complete, sizeof(complete)) == 5);
    CHECK(std::strcmp(complete, unicode) == 0);
    CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_OK);
    weva_event event{};
    while (weva_document_poll_event(doc.d, &event)) {}
    std::string copied(1000, 'x');
    CHECK(weva_element_request_close_dialog_with_value(doc.d, d, copied.c_str()) == WEVA_OK);
    copied = "changed after request";
    while (weva_document_poll_event(doc.d, &event)) {}
    CHECK(weva_element_dialog_return_value(doc.d, d, nullptr, 0) == 1000);
    CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_OK);
    CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 1);
    while (weva_document_poll_event(doc.d, &event)) {}
    CHECK(weva_element_dialog_return_value(doc.d, d, nullptr, 0) == 0);
    CHECK(weva_element_set_dialog_return_value(doc.d, d, "must not survive reload") == WEVA_OK);
    const char* replacement = "<dialog id=d>New</dialog>";
    CHECK(weva_document_load_html(doc.d, replacement, std::strlen(replacement)) == WEVA_OK);
    d = weva_document_query(doc.d, "#d");
    CHECK(weva_element_dialog_return_value(doc.d, d, nullptr, 0) == 0);
}

void test_abi_custom_validity() {
    for (const char* tag : {"input", "textarea", "select", "button"}) for (int action = 0; action < 4; ++action) {
        const std::string html = std::string("<dialog id=d><form id=f method=dialog><") + tag + " id=c>" +
            (std::strcmp(tag, "select") == 0 ? "<option>Yes</option>" : "") + "</" + tag + "><button id=s>OK</button></form></dialog>";
        Doc doc("", html.c_str());
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c"), s = weva_document_query(doc.d, "#s");
        CHECK(weva_element_set_custom_validity(doc.d, c, "Reserved name") == WEVA_OK);
        if (action == 1) CHECK(weva_element_set_custom_validity(doc.d, c, "") == WEVA_OK);
        if (action == 2) weva_document_reset_form(doc.d, weva_document_query(doc.d, "#f"));
        if (action == 3) weva_element_set_attribute(doc.d, c, "disabled", "");
        char message[64]{};
        weva_element_custom_validity(doc.d, c, message, sizeof(message));
        CHECK(std::strcmp(message, action == 1 ? "" : "Reserved name") == 0);
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, s);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_event event{};
        int invalid = 0, submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
            if (event.kind == WEVA_EVENT_INVALID) { ++invalid; CHECK(event.target == c); CHECK(weva_document_prevent_default(doc.d)); }
        }
        const bool rejected = action == 0 || action == 2;
        CHECK(submits == (rejected ? 0 : 1));
        CHECK(invalid == (rejected ? 1 : 0));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == rejected);
        CHECK(!weva_element_has_attribute(doc.d, c, "customValidity"));
    }
    Doc doc("", "<input id=c><div id=x></div>");
    const auto c = weva_document_query(doc.d, "#c");
    const std::string message = std::string("\xF0\x9F\x90\x8E") + std::string(1000, 'x');
    CHECK(weva_element_set_custom_validity(doc.d, c, message.c_str()) == WEVA_OK);
    char small[4]{};
    CHECK(weva_element_custom_validity(doc.d, c, small, sizeof(small)) == message.size());
    CHECK(small[0] == 0);
    std::vector<char> full(message.size() + 1);
    weva_element_custom_validity(doc.d, c, full.data(), full.size());
    CHECK(std::string(full.data()) == message);
    CHECK(weva_element_set_custom_validity(doc.d, c, nullptr) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_set_custom_validity(doc.d, weva_document_query(doc.d, "#x"), "bad") == WEVA_ERR_NOT_FOUND);
}

void test_abi_invalid_reporting_lifecycle() {
    for (int action = 0; action < 6; ++action) {
        Doc doc("", "<dialog id=d><form method=dialog><input id=a required><input id=b required><button id=s>OK</button></form></dialog>");
        auto d = weva_document_query(doc.d, "#d");
        const auto a = weva_document_query(doc.d, "#a"), b = weva_document_query(doc.d, "#b");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_event event{};
        int invalid = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            CHECK(event.kind != WEVA_EVENT_SUBMIT);
            if (event.kind != WEVA_EVENT_INVALID) continue;
            ++invalid;
            if (event.target != a) continue;
            if (action == 0) weva_element_remove(doc.d, a);
            if (action == 1) weva_element_remove(doc.d, b);
            if (action == 2) weva_element_set_attribute(doc.d, b, "disabled", "");
            if (action == 3) weva_element_set_value(doc.d, b, "fixed");
            if (action == 4) {
                const char* replacement = "<dialog id=d open>New document</dialog>";
                weva_document_load_html(doc.d, replacement, std::strlen(replacement));
                d = weva_document_query(doc.d, "#d");
            }
            if (action == 5) for (int i = 0; i < 600; ++i) weva_document_key(doc.d, WEVA_KEY_OTHER, 0, i % 2);
        }
        CHECK(invalid == (action == 0 || action == 5 ? 2 : 1));
        CHECK(weva_element_has_attribute(doc.d, d, "open"));
        if (action != 4) CHECK(weva_document_focus(doc.d) == (action == 0 ? b : a));
        CHECK(!weva_document_prevent_default(doc.d));
    }
}

void test_abi_number_submission() {
    for (const char* value : {"", "-1", "1", "2", "11"}) for (bool bypass : {false, true}) {
        Doc doc("", "<dialog id=d><form method=dialog><input id=c type=number min=0 max=10 step=2><button id=s value=accepted>OK</button></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c"), s = weva_document_query(doc.d, "#s");
        weva_element_set_value(doc.d, c, value);
        if (bypass) weva_element_set_attribute(doc.d, s, "formnovalidate", "");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, s);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_event event{};
        int invalid = 0, submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
            if (event.kind == WEVA_EVENT_INVALID) { ++invalid; CHECK(event.target == c); }
        }
        const bool rejected = !bypass && *value && std::strcmp(value, "2") != 0;
        CHECK(invalid == (rejected ? 1 : 0));
        CHECK(submits == (rejected ? 0 : 1));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == rejected);
    }
}

void test_abi_required_submission() {
    for (bool fill : {false, true}) {
        Doc doc("", "<dialog id=d><form method=dialog><input id=c required><button id=s>OK</button></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c"), s = weva_document_query(doc.d, "#s");
        weva_element_set_value(doc.d, c, fill ? "" : "initial");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, s);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        weva_event event{};
        int submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_CLICK) weva_element_set_value(doc.d, c, fill ? "filled by click handler" : "");
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
        }
        CHECK(submits == (fill ? 1 : 0));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == !fill);
    }
    for (const char* value : {"", "yes"}) for (int bypass = 0; bypass < 3; ++bypass) {
        Doc doc("", "<dialog id=d><form id=f method=dialog on-invalid=ancestor><input id=c required on-invalid=invalid><button id=s value=accepted>OK</button></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c"), s = weva_document_query(doc.d, "#s");
        weva_element_set_value(doc.d, c, value);
        if (bypass == 1) weva_element_set_attribute(doc.d, weva_document_query(doc.d, "#f"), "novalidate", "");
        if (bypass == 2) weva_element_set_attribute(doc.d, s, "formnovalidate", "");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, s);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        weva_event event{};
        int invalid = 0, submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
            if (event.kind == WEVA_EVENT_INVALID) {
                ++invalid;
                CHECK(event.target == c && std::strcmp(event.handler, "invalid") == 0);
                CHECK(weva_document_focus(doc.d) == s);
            }
        }
        const bool rejected = !*value && !bypass;
        CHECK(invalid == (rejected ? 1 : 0));
        CHECK(submits == (rejected ? 0 : 1));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == rejected);
        if (rejected) CHECK(weva_document_focus(doc.d) == c);
    }
    for (int veto = 0; veto < 3; ++veto) {
        Doc doc("", "<dialog id=d><form method=dialog on-invalid=ancestor><input id=a required><input id=b required><button id=s>OK</button></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), a = weva_document_query(doc.d, "#a"), b = weva_document_query(doc.d, "#b"), s = weva_document_query(doc.d, "#s");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, s);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_event event{};
        int invalid = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            CHECK(event.kind != WEVA_EVENT_SUBMIT);
            if (event.kind != WEVA_EVENT_INVALID) continue;
            ++invalid;
            CHECK(event.handler[0] == 0); // Invalid does not bubble to form handler.
            CHECK(weva_document_focus(doc.d) == s); // All handlers run before focus reporting.
            if (veto == 2 || (veto == 1 && event.target == a)) CHECK(weva_document_prevent_default(doc.d));
            // Validation is part of submission: reentry from an invalid handler
            // must not enqueue another validation/submission cycle.
            weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
            weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
            weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        }
        CHECK(invalid == 2);
        CHECK(weva_element_has_attribute(doc.d, d, "open"));
        CHECK(weva_document_focus(doc.d) == (veto == 2 ? s : veto == 1 ? b : a));
        CHECK(!weva_document_prevent_default(doc.d));
    }
}

void test_abi_dialog_submission_lifecycle() {
    const char* html = "<dialog id=d><form id=f method=dialog><button id=s value=yes>OK</button></form></dialog>";
    for (bool during_handler : {false, true}) for (bool reload : {false, true}) {
        Doc doc("", html);
        auto d = weva_document_query(doc.d, "#d");
        const auto f = weva_document_query(doc.d, "#f");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {}
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        if (during_handler) {
            bool found = false;
            while (weva_document_poll_event(doc.d, &event)) {
                if (event.kind == WEVA_EVENT_SUBMIT) { found = true; break; }
            }
            CHECK(found);
        }
        if (reload) {
            const char* replacement = "<dialog id=d open>Replacement</dialog>";
            CHECK(weva_document_load_html(doc.d, replacement, std::strlen(replacement)) == WEVA_OK);
            d = weva_document_query(doc.d, "#d");
        } else CHECK(weva_element_remove(doc.d, f) == WEVA_OK);
        while (weva_document_poll_event(doc.d, &event)) {
            CHECK(event.kind != WEVA_EVENT_SUBMIT);
            CHECK(event.kind != WEVA_EVENT_CLOSE);
        }
        CHECK(weva_element_has_attribute(doc.d, d, "open"));
        CHECK(!weva_document_prevent_default(doc.d));
    }
    for (bool veto : {false, true}) {
        Doc doc("", html);
        const auto d = weva_document_query(doc.d, "#d");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {}
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        for (int i = 0; i < 600; ++i) weva_document_key(doc.d, WEVA_KEY_OTHER, 0, i % 2);
        int submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind != WEVA_EVENT_SUBMIT) continue;
            ++submits;
            if (veto) CHECK(weva_document_prevent_default(doc.d));
            // Recursive activation must not produce another submission.
            weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
            weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        }
        CHECK(submits == 1);
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == veto);
    }
    Doc doc("", html);
    const auto d = weva_document_query(doc.d, "#d");
    weva_element_show_dialog(doc.d, d, 1);
    weva_document_update(doc.d, 0);
    weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
    weva_event event{};
    while (weva_document_poll_event(doc.d, &event)) {}
    for (int i = 0; i < 300; ++i) {
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
    }
    int submits = 0;
    while (weva_document_poll_event(doc.d, &event)) {
        if (event.kind == WEVA_EVENT_SUBMIT) { ++submits; CHECK(weva_document_prevent_default(doc.d)); }
    }
    CHECK(submits == 256);
    CHECK(weva_element_has_attribute(doc.d, d, "open"));
    // Draining restores capacity and leaves no stale veto on the next request.
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
    weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
    while (weva_document_poll_event(doc.d, &event)) {}
    CHECK(!weva_element_has_attribute(doc.d, d, "open"));
}

void test_abi_dialog_submission_handler_mutations() {
    for (const char* action : {"owner-get", "owner-dialog", "owner-missing", "remove-button", "type-button", "form-get", "override-get", "override-dialog"}) {
        Doc doc("", "<dialog id=d><form id=f method=dialog><button id=s value=yes>OK</button></form></dialog><form id=g method=get></form>");
        const auto d = weva_document_query(doc.d, "#d"), f = weva_document_query(doc.d, "#f"), s = weva_document_query(doc.d, "#s");
        weva_element_set_dialog_return_value(doc.d, d, "initial");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, s);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        weva_event event{};
        int submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind != WEVA_EVENT_SUBMIT) continue;
            ++submits;
            if (std::strncmp(action, "owner-", 6) == 0) {
                if (std::strcmp(action, "owner-dialog") == 0)
                    weva_element_set_attribute(doc.d, weva_document_query(doc.d, "#g"), "method", "dialog");
                weva_element_set_attribute(doc.d, s, "form", std::strcmp(action, "owner-missing") == 0 ? "missing" : "g");
            }
            if (std::strcmp(action, "remove-button") == 0) weva_element_remove(doc.d, s);
            if (std::strcmp(action, "type-button") == 0) weva_element_set_attribute(doc.d, s, "type", "button");
            if (std::strcmp(action, "form-get") == 0 || std::strcmp(action, "override-dialog") == 0)
                weva_element_set_attribute(doc.d, f, "method", "get");
            if (std::strcmp(action, "override-get") == 0) weva_element_set_attribute(doc.d, s, "formmethod", "get");
            if (std::strcmp(action, "override-dialog") == 0) weva_element_set_attribute(doc.d, s, "formmethod", "dialog");
        }
        const bool open = std::strcmp(action, "form-get") == 0 || std::strcmp(action, "override-get") == 0;
        CHECK(submits == 1);
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == open);
        char result[64]{};
        weva_element_dialog_return_value(doc.d, d, result, sizeof(result));
        CHECK(std::strcmp(result, open ? "initial" : "yes") == 0);
    }
}

void test_abi_dialog_form_submission_overrides() {
    for (const char* method : {"dialog", "DiAlOg", "get", "invalid"}) {
        for (const char* override_method : {static_cast<const char*>(nullptr), "dialog", "get", ""}) {
            std::string html = std::string("<dialog id=d><form method='") + method + "'><button id=s value=yes";
            if (override_method) html += std::string(" formmethod='") + override_method + "'";
            html += ">OK</button></form></dialog>";
            Doc doc("", html.c_str());
            const auto d = weva_document_query(doc.d, "#d");
            weva_element_set_dialog_return_value(doc.d, d, "initial");
            weva_element_show_dialog(doc.d, d, 1);
            weva_document_update(doc.d, 0);
            weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
            weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
            weva_event event{};
            while (weva_document_poll_event(doc.d, &event)) {}
            const bool closes = override_method ? std::strcmp(override_method, "dialog") == 0 :
                std::strcmp(method, "dialog") == 0 || std::strcmp(method, "DiAlOg") == 0;
            CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == !closes);
            char result[64]{};
            weva_element_dialog_return_value(doc.d, d, result, sizeof(result));
            CHECK(std::strcmp(result, closes ? "yes" : "initial") == 0);
        }
    }
    for (int border : {0, 5}) for (int padding : {0, 7}) for (bool pointer : {false, true}) {
        const std::string css = "input {width:100px;height:40px;border:" + std::to_string(border) +
            "px solid;padding:" + std::to_string(padding) + "px}";
        Doc doc(css.c_str(), "<dialog id=d><form method=dialog><input id=s type=image></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), s = weva_document_query(doc.d, "#s");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, s);
        if (pointer) {
            double x, y, w, h;
            CHECK(weva_element_bounds(doc.d, s, &x, &y, &w, &h) == WEVA_OK);
            doc.click(x + 20.8, y + 18.6);
        } else weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {}
        CHECK(!weva_element_has_attribute(doc.d, d, "open"));
        char result[64]{};
        weva_element_dialog_return_value(doc.d, d, result, sizeof(result));
        CHECK(std::strcmp(result, !pointer ? "0,0" : border ? "16,14" : "21,19") == 0);
    }
}

// Chrome152: submission handlers run before dialog method/value resolution.
void test_abi_dialog_form_submission() {
    struct Case { const char* kind; const char* value; const char* handler; bool open; const char* result; };
    const Case cases[] = {
        {"button", nullptr, "allow", false, "initial"},
        {"button", nullptr, "prevent", true, "initial"},
        {"button", nullptr, "set-value", false, "handler-value"},
        {"button", nullptr, "close", false, "handler-close"},
        {"button", nullptr, "reopen", false, "handler-close"},
        {"button", nullptr, "remove", true, "initial"},
        {"button", "", "allow", false, ""},
        {"button", "", "prevent", true, "initial"},
        {"button", "", "set-value", false, "handler-value"},
        {"button", "", "close", false, "handler-close"},
        {"button", "", "reopen", false, ""},
        {"button", "", "remove", true, "initial"},
        {"button", "accepted", "allow", false, "accepted"},
        {"button", "accepted", "prevent", true, "initial"},
        {"button", "accepted", "set-value", false, "handler-value"},
        {"button", "accepted", "close", false, "handler-close"},
        {"button", "accepted", "reopen", false, "accepted"},
        {"button", "accepted", "remove", true, "initial"},
        {"input", nullptr, "allow", false, "initial"},
        {"input", nullptr, "prevent", true, "initial"},
        {"input", nullptr, "set-value", false, "handler-value"},
        {"input", nullptr, "close", false, "handler-close"},
        {"input", nullptr, "reopen", false, "handler-close"},
        {"input", nullptr, "remove", true, "initial"},
        {"input", "", "allow", false, ""},
        {"input", "", "prevent", true, "initial"},
        {"input", "", "set-value", false, "handler-value"},
        {"input", "", "close", false, "handler-close"},
        {"input", "", "reopen", false, ""},
        {"input", "", "remove", true, "initial"},
        {"input", "accepted", "allow", false, "accepted"},
        {"input", "accepted", "prevent", true, "initial"},
        {"input", "accepted", "set-value", false, "handler-value"},
        {"input", "accepted", "close", false, "handler-close"},
        {"input", "accepted", "reopen", false, "accepted"},
        {"input", "accepted", "remove", true, "initial"},
        {"implicit", nullptr, "allow", false, ""},
        {"implicit", nullptr, "prevent", true, "initial"},
        {"implicit", nullptr, "set-value", false, ""},
        {"implicit", nullptr, "close", false, "handler-close"},
        {"implicit", nullptr, "reopen", false, ""},
        {"implicit", nullptr, "remove", true, "initial"},
        {"implicit", "", "allow", false, ""},
        {"implicit", "", "prevent", true, "initial"},
        {"implicit", "", "set-value", false, ""},
        {"implicit", "", "close", false, "handler-close"},
        {"implicit", "", "reopen", false, ""},
        {"implicit", "", "remove", true, "initial"},
        {"implicit", "accepted", "allow", false, ""},
        {"implicit", "accepted", "prevent", true, "initial"},
        {"implicit", "accepted", "set-value", false, ""},
        {"implicit", "accepted", "close", false, "handler-close"},
        {"implicit", "accepted", "reopen", false, ""},
        {"implicit", "accepted", "remove", true, "initial"},
    };
    for (const auto& row : cases) {
        const bool implicit = std::strcmp(row.kind, "implicit") == 0;
        std::string html = "<dialog id=d><form id=f method=dialog><input id=field>";
        if (!implicit) {
            html += std::string("<") + row.kind + " id=submitter type=submit";
            if (row.value) html += std::string(" value='") + row.value + "'";
            html += ">";
            if (std::strcmp(row.kind, "button") == 0) html += "Submit</button>";
        }
        html += "</form></dialog>";
        Doc doc("", html.c_str());
        auto d = weva_document_query(doc.d, "#d");
        auto f = weva_document_query(doc.d, "#f");
        auto button = weva_document_query(doc.d, "#submitter");
        weva_element_set_dialog_return_value(doc.d, d, "initial");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, implicit ? weva_document_query(doc.d, "#field") : button);
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {}
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 0);
        int submits = 0, cancels = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_CANCEL) ++cancels;
            if (event.kind != WEVA_EVENT_SUBMIT) continue;
            ++submits;
            CHECK(event.target == f);
            CHECK(weva_element_has_attribute(doc.d, d, "open"));
            if (std::strcmp(row.handler, "prevent") == 0) CHECK(weva_document_prevent_default(doc.d));
            if (std::strcmp(row.handler, "set-value") == 0 && !implicit)
                weva_element_set_value(doc.d, button, "handler-value");
            if (std::strcmp(row.handler, "close") == 0 || std::strcmp(row.handler, "reopen") == 0)
                weva_element_close_dialog_with_value(doc.d, d, "handler-close");
            if (std::strcmp(row.handler, "reopen") == 0) weva_element_show_dialog(doc.d, d, 1);
            if (std::strcmp(row.handler, "remove") == 0) weva_element_remove(doc.d, f);
        }
        CHECK(submits == 1 && cancels == 0);
        CHECK(!weva_document_prevent_default(doc.d));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == row.open);
        char result[64]{};
        weva_element_dialog_return_value(doc.d, d, result, sizeof(result));
        CHECK(std::strcmp(result, row.result) == 0);
    }
}

void test_abi_email_validation() {
    struct Case { const char* raw; const char* sanitized; bool multiple, mismatch; };
    const Case cases[] = {
        {"", "", false, false},
        {"a", "a", false, true},
        {"a@b", "a@b", false, false},
        {"a@b.c", "a@b.c", false, false},
        {"a@-b", "a@-b", false, true},
        {"a@b-", "a@b-", false, true},
        {"a@b_c", "a@b_c", false, true},
        {"a@b..c", "a@b..c", false, true},
        {"a@b.", "a@b.", false, true},
        {".a@b", ".a@b", false, false},
        {"a..b@c", "a..b@c", false, false},
        {"a.@b", "a.@b", false, false},
        {"!#$%&'*+-/=?^_`{|}~@b", "!#$%&'*+-/=?^_`{|}~@b", false, false},
        {"a b@c", "a b@c", false, true},
        {"a@@b", "a@@b", false, true},
        {"a@bücher.de", "a@bücher.de", false, true},
        {"a@xn--bcher-kva.de", "a@xn--bcher-kva.de", false, false},
        {"é@b", "é@b", false, true},
        {"a@[127.0.0.1]", "a@[127.0.0.1]", false, true},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false, false},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false, true},
        {"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", false, false},
        {"a@b,c@d", "a@b,c@d", false, true},
        {"a@b,", "a@b,", false, true},
        {" , ", ",", false, true},
        {" a@b \n", "a@b", false, false},
        {"a@b, c@d", "a@b, c@d", false, true},
        {"a@b,,c@d", "a@b,,c@d", false, true},
        {"", "", true, false},
        {"a", "a", true, true},
        {"a@b", "a@b", true, false},
        {"a@b.c", "a@b.c", true, false},
        {"a@-b", "a@-b", true, true},
        {"a@b-", "a@b-", true, true},
        {"a@b_c", "a@b_c", true, true},
        {"a@b..c", "a@b..c", true, true},
        {"a@b.", "a@b.", true, true},
        {".a@b", ".a@b", true, false},
        {"a..b@c", "a..b@c", true, false},
        {"a.@b", "a.@b", true, false},
        {"!#$%&'*+-/=?^_`{|}~@b", "!#$%&'*+-/=?^_`{|}~@b", true, false},
        {"a b@c", "a b@c", true, true},
        {"a@@b", "a@@b", true, true},
        {"a@bücher.de", "a@bücher.de", true, true},
        {"a@xn--bcher-kva.de", "a@xn--bcher-kva.de", true, false},
        {"é@b", "é@b", true, true},
        {"a@[127.0.0.1]", "a@[127.0.0.1]", true, true},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, false},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, true},
        {"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", true, false},
        {"a@b,c@d", "a@b,c@d", true, false},
        {"a@b,", "a@b,", true, true},
        {" , ", ",", true, true},
        {" a@b \n", "a@b", true, false},
        {"a@b, c@d", "a@b,c@d", true, false},
        {"a@b,,c@d", "a@b,,c@d", true, true},
    };
    for (const auto& row : cases) for (bool bypass : {false, true}) {
        Doc doc("", "<dialog id=d><form id=f method=dialog><input id=c type=email><button id=s>OK</button></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c");
        if (row.multiple) weva_element_set_attribute(doc.d, c, "multiple", "");
        if (bypass) weva_element_set_attribute(doc.d, weva_document_query(doc.d, "#f"), "novalidate", "");
        weva_element_set_value(doc.d, c, row.raw);
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        int invalid = 0, submits = 0;
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_INVALID) { ++invalid; CHECK(event.target == c); }
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
        }
        const bool rejected = row.mismatch && !bypass;
        CHECK(invalid == (rejected ? 1 : 0));
        CHECK(submits == (rejected ? 0 : 1));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == rejected);
    }
}

void test_abi_url_validation() {
    struct Case { std::string raw, sanitized; bool mismatch; };
    const Case cases[] = {
        {{"", 0}, {"", 0}, false},
        {{"example.com", 11}, {"example.com", 11}, true},
        {{"/path", 5}, {"/path", 5}, true},
        {{"//example.com", 13}, {"//example.com", 13}, true},
        {{"https://example.com", 19}, {"https://example.com", 19}, false},
        {{"http:example.com", 16}, {"http:example.com", 16}, false},
        {{"https:/example.com", 18}, {"https:/example.com", 18}, false},
        {{"https:", 6}, {"https:", 6}, true},
        {{"http://", 7}, {"http://", 7}, true},
        {{"https://x:65535", 15}, {"https://x:65535", 15}, false},
        {{"https://x:65536", 15}, {"https://x:65536", 15}, true},
        {{"https://x:-1", 12}, {"https://x:-1", 12}, true},
        {{"https://x:abc", 13}, {"https://x:abc", 13}, true},
        {{"https://[::1]/", 14}, {"https://[::1]/", 14}, false},
        {{"https://[::1", 12}, {"https://[::1", 12}, true},
        {{"https://[gg::1]/", 16}, {"https://[gg::1]/", 16}, true},
        {{"http://127.1", 12}, {"http://127.1", 12}, false},
        {{"http://256.1.1.1", 16}, {"http://256.1.1.1", 16}, true},
        {{"http://0x7f000001", 17}, {"http://0x7f000001", 17}, false},
        {{"http://1.2.3.999", 16}, {"http://1.2.3.999", 16}, true},
        {{"http://b\303\274cher.de", 17}, {"http://b\303\274cher.de", 17}, false},
        {{"http://\342\230\203.net", 14}, {"http://\342\230\203.net", 14}, false},
        // URL Standard forbids domain spaces; Chrome 152 accepts this case.
        {{"http://a b", 10}, {"http://a b", 10}, true},
        {{"http://a%20b", 12}, {"http://a%20b", 12}, true},
        {{"http://%41.com", 14}, {"http://%41.com", 14}, false},
        {{"http://%zz.com", 14}, {"http://%zz.com", 14}, true},
        {{"http://x/%zz", 12}, {"http://x/%zz", 12}, false},
        {{"http://x/a b", 12}, {"http://x/a b", 12}, false},
        {{"https://user:pass@x/", 20}, {"https://user:pass@x/", 20}, false},
        {{"https://@/", 10}, {"https://@/", 10}, true},
        {{"file:///C:/game", 15}, {"file:///C:/game", 15}, false},
        {{"file://server/path", 18}, {"file://server/path", 18}, false},
        {{"file://", 7}, {"file://", 7}, false},
        {{"mailto:player@example.com", 25}, {"mailto:player@example.com", 25}, false},
        {{"urn:game:server:1", 17}, {"urn:game:server:1", 17}, false},
        {{"steam://connect/127.0.0.1", 25}, {"steam://connect/127.0.0.1", 25}, false},
        {{"data:text/plain,hello world", 27}, {"data:text/plain,hello world", 27}, false},
        {{"about:blank", 11}, {"about:blank", 11}, false},
        {{"javascript:alert(1)", 19}, {"javascript:alert(1)", 19}, false},
        {{"foo:", 4}, {"foo:", 4}, false},
        {{"foo:hello world", 15}, {"foo:hello world", 15}, false},
        {{"foo://x:99999", 13}, {"foo://x:99999", 13}, true},
        {{"foo://[bad]", 11}, {"foo://[bad]", 11}, true},
        {{"1foo:bar", 8}, {"1foo:bar", 8}, true},
        {{"a+b.c-d:foo", 11}, {"a+b.c-d:foo", 11}, false},
        {{" https://x \012", 12}, {"https://x", 9}, false},
        {{"ht\011tp://x", 9}, {"ht\011tp://x", 9}, false},
        {{"http:\134\134x", 8}, {"http:\134\134x", 8}, false},
        {{"https://x\000", 10}, {"https://x\000", 10}, false},
        {{"https://x\001", 10}, {"https://x\001", 10}, false},
        {{"http://x.", 9}, {"http://x.", 9}, false},
        {{"http://-x", 9}, {"http://-x", 9}, false},
        {{"http://a_b", 10}, {"http://a_b", 10}, false},
        {{"http://a..b", 11}, {"http://a..b", 11}, false},
    };
    for (const auto& row : cases) for (bool bypass : {false, true}) {
        // The C setter takes a terminated string; embedded NUL is covered by the core helper.
        if (row.raw.find('\0') != std::string::npos) continue;
        Doc doc("", "<dialog id=d><form id=f method=dialog><input id=c type=url><button id=s>OK</button></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c");
        if (bypass) weva_element_set_attribute(doc.d, weva_document_query(doc.d, "#f"), "novalidate", "");
        weva_element_set_value(doc.d, c, row.raw.c_str());
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        int invalid = 0, submits = 0;
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_INVALID) { ++invalid; CHECK(event.target == c); }
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
        }
        const bool rejected = row.mismatch && !bypass;
        CHECK(invalid == (rejected ? 1 : 0));
        CHECK(submits == (rejected ? 0 : 1));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == rejected);
    }
}

void test_abi_length_validation() {
    struct Case { const char *tag, *minimum, *maximum, *value; bool short_value, long_value; };
    const Case cases[] = {
        {"input", "5", nullptr, "ab", true, false},
        {"input", "2", nullptr, "🐎", false, false},
        {"input", "3", nullptr, "🐎", true, false},
        {"input", "3", nullptr, "é", true, false},
        {"input", "1", nullptr, "", false, false},
        {"input", nullptr, "1", "ab", false, true},
        {"input", nullptr, "1", "🐎", false, true},
        {"input", "  +3x", nullptr, "ab", true, false},
        {"input", "-0", nullptr, "ab", false, false},
        {"input", "2147483648", nullptr, "ab", false, false},
        {"textarea", "5", nullptr, "ab", true, false},
        {"textarea", "2", nullptr, "🐎", false, false},
        {"textarea", "3", nullptr, "🐎", true, false},
        {"textarea", "3", nullptr, "é", true, false},
        {"textarea", "1", nullptr, "", false, false},
        {"textarea", nullptr, "1", "ab", false, true},
        {"textarea", nullptr, "1", "🐎", false, true},
        {"textarea", "  +3x", nullptr, "ab", true, false},
        {"textarea", "-0", nullptr, "ab", false, false},
        {"textarea", "2147483648", nullptr, "ab", false, false},
    };
    for (const auto& row : cases) for (bool user : {false, true}) for (bool bypass : {false, true}) {
        const std::string html = std::string("<dialog id=d><form id=f method=dialog><") + row.tag +
            " id=c></" + row.tag + "><button id=s>OK</button></form></dialog>";
        Doc doc("", html.c_str());
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        if (user) {
            weva_document_set_focus(doc.d, c);
            if (*row.value) CHECK(weva_document_try_text_input(doc.d, row.value));
        } else weva_element_set_value(doc.d, c, row.value);
        if (row.minimum) weva_element_set_attribute(doc.d, c, "minlength", row.minimum);
        if (row.maximum) weva_element_set_attribute(doc.d, c, "maxlength", row.maximum);
        if (bypass) weva_element_set_attribute(doc.d, weva_document_query(doc.d, "#f"), "novalidate", "");
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_event event{}; int invalid = 0, submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_INVALID) { ++invalid; CHECK(event.target == c); }
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
        }
        const bool rejected = user && !bypass && (row.short_value || row.long_value);
        CHECK(invalid == (rejected ? 1 : 0));
        CHECK(submits == (rejected ? 0 : 1));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == rejected);
    }
}

void test_abi_temporal_constraints() {
    struct Case { const char *type, *value, *minimum, *maximum, *step, *initial; bool underflow, overflow, mismatch; };
    const Case cases[] = {
        {"date", "1970-01-01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "any", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "any", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "2", nullptr, false, false, true},
        {"date", "1970-01-03", nullptr, nullptr, "2", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"date", "1970-01-03", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "any", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "any", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "2", nullptr, false, false, true},
        {"month", "1970-03", nullptr, nullptr, "2", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"month", "1970-03", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "any", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "any", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "2", nullptr, false, false, true},
        {"week", "1970-W03", nullptr, nullptr, "2", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"week", "1970-W03", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, nullptr, nullptr, false, false, true},
        {"time", "00:01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "any", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"time", "00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "2", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "2", nullptr, false, false, true},
        {"time", "00:01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"time", "00:01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, nullptr, nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "2", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "2", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"time", "05:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"time", "12:00", "22:00", "05:00", nullptr, nullptr, true, true, false},
        {"time", "22:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"time", "23:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-01", "1970-01-02", nullptr, "2", nullptr, true, false, true},
        {"date", "1970-01-02", "1970-01-02", nullptr, "2", nullptr, false, false, false},
        {"date", "1970-01-03", "1970-01-02", nullptr, "2", nullptr, false, false, true},
        {"date", "1970-01-01", nullptr, nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-02", nullptr, nullptr, "2", "1970-01-02", false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-01", "bad", nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-02", "bad", nullptr, "2", "1970-01-02", false, false, false},
        {"date", "1970-01-03", "bad", nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-01", "1970-01-03", "1970-01-01", "any", nullptr, true, false, false},
        {"date", "1970-01-02", "1970-01-03", "1970-01-01", "any", nullptr, true, true, false},
        {"date", "1970-01-03", "1970-01-03", "1970-01-01", "any", nullptr, false, true, false},
        {"month", "1970-01", nullptr, nullptr, "0", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-01", "1970-02", nullptr, "2", nullptr, true, false, true},
        {"month", "1970-02", "1970-02", nullptr, "2", nullptr, false, false, false},
        {"month", "1970-03", "1970-02", nullptr, "2", nullptr, false, false, true},
        {"month", "1970-01", nullptr, nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-02", nullptr, nullptr, "2", "1970-02", false, false, false},
        {"month", "1970-03", nullptr, nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-01", "bad", nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-02", "bad", nullptr, "2", "1970-02", false, false, false},
        {"month", "1970-03", "bad", nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-01", "1970-03", "1970-01", "any", nullptr, true, false, false},
        {"month", "1970-02", "1970-03", "1970-01", "any", nullptr, true, true, false},
        {"month", "1970-03", "1970-03", "1970-01", "any", nullptr, false, true, false},
        {"week", "1970-W01", nullptr, nullptr, "0", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W01", "1970-W02", nullptr, "2", nullptr, true, false, true},
        {"week", "1970-W02", "1970-W02", nullptr, "2", nullptr, false, false, false},
        {"week", "1970-W03", "1970-W02", nullptr, "2", nullptr, false, false, true},
        {"week", "1970-W01", nullptr, nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W02", nullptr, nullptr, "2", "1970-W02", false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W01", "bad", nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W02", "bad", nullptr, "2", "1970-W02", false, false, false},
        {"week", "1970-W03", "bad", nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W01", "1970-W03", "1970-W01", "any", nullptr, true, false, false},
        {"week", "1970-W02", "1970-W03", "1970-W01", "any", nullptr, true, true, false},
        {"week", "1970-W03", "1970-W03", "1970-W01", "any", nullptr, false, true, false},
        {"time", "00:00", nullptr, nullptr, "0", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"time", "00:00:00.002", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.0015", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00:00.002", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00", "00:00:00.001", nullptr, "0.002", nullptr, true, false, true},
        {"time", "00:00:00.001", "00:00:00.001", nullptr, "0.002", nullptr, false, false, false},
        {"time", "00:00:00.002", "00:00:00.001", nullptr, "0.002", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00:00.001", nullptr, nullptr, "0.002", "00:00:00.001", false, false, false},
        {"time", "00:00:00.002", nullptr, nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00", "bad", nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00:00.001", "bad", nullptr, "0.002", "00:00:00.001", false, false, false},
        {"time", "00:00:00.002", "bad", nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00", "00:00:00.002", "00:00", "any", nullptr, false, false, false},
        {"time", "00:00:00.001", "00:00:00.002", "00:00", "any", nullptr, true, true, false},
        {"time", "00:00:00.002", "00:00:00.002", "00:00", "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.0015", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", "1970-01-01T00:00:00.001", nullptr, "0.002", nullptr, true, false, true},
        {"datetime-local", "1970-01-01T00:00:00.001", "1970-01-01T00:00:00.001", nullptr, "0.002", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", "1970-01-01T00:00:00.001", nullptr, "0.002", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00", "bad", nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.001", "bad", nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", "bad", nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00", "1970-01-01T00:00:00.002", "1970-01-01T00:00", "any", nullptr, true, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", "1970-01-01T00:00:00.002", "1970-01-01T00:00", "any", nullptr, true, true, false},
        {"datetime-local", "1970-01-01T00:00:00.002", "1970-01-01T00:00:00.002", "1970-01-01T00:00", "any", nullptr, false, true, false},
    };
    for (const auto& row : cases) for (bool bypass : {false, true}) {
        Doc doc("", "<dialog id=d><form id=f method=dialog><input id=c><button id=s>OK</button></form></dialog>");
        const auto d = weva_document_query(doc.d, "#d"), c = weva_document_query(doc.d, "#c");
        weva_element_set_attribute(doc.d, c, "type", row.type);
        if (row.minimum) weva_element_set_attribute(doc.d, c, "min", row.minimum);
        if (row.maximum) weva_element_set_attribute(doc.d, c, "max", row.maximum);
        if (row.step) weva_element_set_attribute(doc.d, c, "step", row.step);
        if (row.initial) weva_element_set_attribute(doc.d, c, "value", row.initial);
        weva_element_set_value(doc.d, c, row.value);
        if (bypass) weva_element_set_attribute(doc.d, weva_document_query(doc.d, "#f"), "novalidate", "");
        weva_element_show_dialog(doc.d, d, 1);
        weva_document_update(doc.d, 0);
        weva_document_set_focus(doc.d, weva_document_query(doc.d, "#s"));
        weva_document_key(doc.d, WEVA_KEY_ENTER, 0, 1);
        weva_event event{}; int invalid = 0, submits = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_INVALID) { ++invalid; CHECK(event.target == c); }
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
        }
        const bool rejected = !bypass && (row.underflow || row.overflow || row.mismatch);
        CHECK(invalid == (rejected ? 1 : 0));
        CHECK(submits == (rejected ? 0 : 1));
        CHECK(bool(weva_element_has_attribute(doc.d, d, "open")) == rejected);
    }
}

void test_abi_validity_snapshot() {
    struct Case { const char* html; bool custom; uint32_t flags; int will; };
    const Case cases[] = {
        {"<input id=\"c\" name=\"n\" required type=\"text\"  ></input>", false, 1, 1},
        {"<input id=\"c\" name=\"n\" required type=\"text\" disabled ></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"text\"  readonly></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"text\"  ></input>", true, 513, 1},
        {"<input id=\"c\" name=\"n\" required type=\"number\"  ></input>", false, 1, 1},
        {"<input id=\"c\" name=\"n\" required type=\"number\" disabled ></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"number\"  readonly></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"number\"  ></input>", true, 513, 1},
        {"<input id=\"c\" name=\"n\" required type=\"email\"  ></input>", false, 1, 1},
        {"<input id=\"c\" name=\"n\" required type=\"email\" disabled ></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"email\"  readonly></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"email\"  ></input>", true, 513, 1},
        {"<input id=\"c\" name=\"n\" required type=\"url\"  ></input>", false, 1, 1},
        {"<input id=\"c\" name=\"n\" required type=\"url\" disabled ></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"url\"  readonly></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"url\"  ></input>", true, 513, 1},
        {"<input id=\"c\" name=\"n\" required type=\"checkbox\"  ></input>", false, 1, 1},
        {"<input id=\"c\" name=\"n\" required type=\"checkbox\" disabled ></input>", false, 1, 0},
        {"<input id=\"c\" name=\"n\" required type=\"checkbox\"  readonly></input>", false, 1, 0},
        {"<input id=\"c\" name=\"n\" required type=\"checkbox\"  ></input>", true, 513, 1},
        {"<input id=\"c\" name=\"n\" required type=\"radio\"  ></input>", false, 1, 1},
        {"<input id=\"c\" name=\"n\" required type=\"radio\" disabled ></input>", false, 1, 0},
        {"<input id=\"c\" name=\"n\" required type=\"radio\"  readonly></input>", false, 1, 0},
        {"<input id=\"c\" name=\"n\" required type=\"radio\"  ></input>", true, 513, 1},
        {"<input id=\"c\" name=\"n\" required type=\"hidden\"  ></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"hidden\" disabled ></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"hidden\"  readonly></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"hidden\"  ></input>", true, 512, 0},
        {"<input id=\"c\" name=\"n\" required type=\"submit\"  ></input>", false, 0, 1},
        {"<input id=\"c\" name=\"n\" required type=\"submit\" disabled ></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"submit\"  readonly></input>", false, 0, 0},
        {"<input id=\"c\" name=\"n\" required type=\"submit\"  ></input>", true, 512, 1},
        {"<textarea id=\"c\" name=\"n\" required   ></textarea>", false, 1, 1},
        {"<textarea id=\"c\" name=\"n\" required  disabled ></textarea>", false, 0, 0},
        {"<textarea id=\"c\" name=\"n\" required   readonly></textarea>", false, 0, 0},
        {"<textarea id=\"c\" name=\"n\" required   ></textarea>", true, 513, 1},
        {"<select id=\"c\" name=\"n\" required   ></select>", false, 1, 1},
        {"<select id=\"c\" name=\"n\" required  disabled ></select>", false, 1, 0},
        {"<select id=\"c\" name=\"n\" required   readonly></select>", false, 1, 1},
        {"<select id=\"c\" name=\"n\" required   ></select>", true, 513, 1},
        {"<button id=\"c\" name=\"n\" required   ></button>", false, 0, 1},
        {"<button id=\"c\" name=\"n\" required  disabled ></button>", false, 0, 0},
        {"<button id=\"c\" name=\"n\" required   readonly></button>", false, 0, 1},
        {"<button id=\"c\" name=\"n\" required   ></button>", true, 512, 1},

    };
    for (const auto& row : cases) {
        Doc doc("", row.html);
        const auto c = weva_document_query(doc.d, "#c");
        if (row.custom) CHECK(weva_element_set_custom_validity(doc.d, c, "Reserved") == WEVA_OK);
        uint32_t errors = 0xffffffff;
        int will = -1;
        CHECK(weva_element_validity(doc.d, c, &errors, &will) == WEVA_OK);
        CHECK(errors == row.flags);
        CHECK(will == row.will);
        weva_event event{};
        CHECK(!weva_document_poll_event(doc.d, &event));
    }
    Doc doc("", "<input id='c' type='number' min='10' max='2' step='2' value='5'><div id='x'></div>");
    const auto c = weva_document_query(doc.d, "#c");
    uint32_t errors = 0;
    int will = 0;
    CHECK(weva_element_validity(doc.d, c, &errors, &will) == WEVA_OK);
    CHECK(errors == (WEVA_VALIDITY_RANGE_UNDERFLOW | WEVA_VALIDITY_RANGE_OVERFLOW | WEVA_VALIDITY_STEP_MISMATCH));
    CHECK(weva_element_set_value(doc.d, c, "12") == WEVA_OK);
    CHECK(weva_element_validity(doc.d, c, &errors, &will) == WEVA_OK);
    CHECK(errors == WEVA_VALIDITY_RANGE_OVERFLOW);
    CHECK(weva_element_validity(doc.d, weva_document_query(doc.d, "#x"), &errors, &will) == WEVA_ERR_NOT_FOUND);
    CHECK(errors == 0 && will == 0);
    CHECK(weva_element_validity(nullptr, c, &errors, &will) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_validity(doc.d, c, nullptr, &will) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_validity(doc.d, c, &errors, nullptr) == WEVA_ERR_INVALID_ARGUMENT);
    for (const auto* type : {"email", "url"}) {
        const std::string html = std::string("<input id='c' type='") + type + "' value='invalid'>";
        Doc invalid("", html.c_str());
        CHECK(weva_element_validity(invalid.d, weva_document_query(invalid.d, "#c"), &errors, &will) == WEVA_OK);
        CHECK(errors == WEVA_VALIDITY_TYPE_MISMATCH);
    }
    Doc editing("", "<textarea id='c' minlength='3'></textarea>");
    const auto text = weva_document_query(editing.d, "#c");
    weva_document_set_focus(editing.d, text);
    weva_document_text_input(editing.d, "ab");
    CHECK(weva_element_validity(editing.d, text, &errors, &will) == WEVA_OK);
    CHECK(errors == WEVA_VALIDITY_TOO_SHORT);
    CHECK(weva_element_set_attribute(editing.d, text, "maxlength", "1") == WEVA_OK);
    CHECK(weva_element_validity(editing.d, text, &errors, &will) == WEVA_OK);
    CHECK(errors == (WEVA_VALIDITY_TOO_SHORT | WEVA_VALIDITY_TOO_LONG));
    Doc bad("", "<input id='c' type='number'>");
    const auto number = weva_document_query(bad.d, "#c");
    weva_document_set_focus(bad.d, number);
    weva_document_text_input(bad.d, "-");
    CHECK(weva_element_validity(bad.d, number, &errors, &will) == WEVA_OK);
    CHECK(errors == WEVA_VALIDITY_BAD_INPUT);
    Doc pattern("", "<input id='p' pattern='[a-z]+' value='abc'>");
    CHECK(weva_element_validity(pattern.d, weva_document_query(pattern.d, "#p"), &errors, &will) == WEVA_ERR_UNSUPPORTED);
    CHECK(errors == 0 && will == 0);
}

void test_abi_explicit_validity() {
    struct Case { bool report; const char *scope, *action, *events, *focus; };
    const Case cases[] = {
        {false, "f", "none", "external,a,b", "save"},
        {false, "f", "cancel", "external,a,b", "save"},
        {false, "f", "fix", "external,a", "save"},
        {false, "f", "disable", "external,a", "save"},
        {false, "f", "remove", "external,a", "save"},
        {false, "f", "reassociate", "external,a", "save"},
        {false, "a", "none", "a", "save"},
        {false, "a", "cancel", "a", "save"},
        {false, "a", "fix", "a", "save"},
        {false, "a", "disable", "a", "save"},
        {false, "a", "remove", "a", "save"},
        {false, "a", "reassociate", "a", "save"},
        {true, "f", "none", "external,a,b", "external"},
        {true, "f", "cancel", "external,a,b", "save"},
        {true, "f", "fix", "external,a", "external"},
        {true, "f", "disable", "external,a", "external"},
        {true, "f", "remove", "external,a", "external"},
        {true, "f", "reassociate", "external,a", "external"},
        {true, "a", "none", "a", "a"},
        {true, "a", "cancel", "a", "save"},
        {true, "a", "fix", "a", "a"},
        {true, "a", "disable", "a", "a"},
        {true, "a", "remove", "a", "a"},
        {true, "a", "reassociate", "a", "a"},

    };
    for (const auto& row : cases) {
        Doc doc("", "<input id='external' form='f' required><form id='f' novalidate><input id='a' required><input id='b' required><button id='save'>Save</button></form><form id='other'></form>");
        const auto a = weva_document_query(doc.d, "#a"), b = weva_document_query(doc.d, "#b");
        const auto save = weva_document_query(doc.d, "#save");
        weva_document_set_focus(doc.d, save);
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {}
        int valid = -1;
        const auto target = weva_document_query(doc.d, (std::string("#") + row.scope).c_str());
        CHECK((row.report ? weva_element_report_validity(doc.d, target, &valid) : weva_element_check_validity(doc.d, target, &valid)) == WEVA_OK);
        CHECK(valid == 0);
        std::string seen;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind != WEVA_EVENT_INVALID) { CHECK(event.kind != WEVA_EVENT_SUBMIT); continue; }
            char id[32]{};
            weva_element_attribute(doc.d, event.target, "id", id, sizeof(id));
            if (!seen.empty()) seen += ',';
            seen += id;
            if (std::string(row.action) == "cancel") CHECK(weva_document_prevent_default(doc.d) == 1);
            if (event.target == a) {
                if (std::string(row.action) == "fix") weva_element_set_value(doc.d, b, "ok");
                if (std::string(row.action) == "disable") weva_element_set_attribute(doc.d, b, "disabled", "");
                if (std::string(row.action) == "remove") weva_element_remove(doc.d, b);
                if (std::string(row.action) == "reassociate") weva_element_set_attribute(doc.d, b, "form", "other");
                int nested = -1;
                CHECK(weva_element_check_validity(doc.d, target, &nested) == WEVA_ERR_INVALID_STATE);
                CHECK(nested == 0);
            }
        }
        CHECK(seen == row.events);
        CHECK(weva_document_focus(doc.d) == weva_document_query(doc.d, (std::string("#") + row.focus).c_str()));
    }
    Doc doc("", "<form id='f'><input required disabled><input id='c' required value='ok'></form><div id='x'></div>");
    int valid = -1;
    CHECK(weva_element_check_validity(doc.d, weva_document_query(doc.d, "#f"), &valid) == WEVA_OK);
    CHECK(valid == 1);
    weva_event event{};
    CHECK(!weva_document_poll_event(doc.d, &event));
    CHECK(weva_element_report_validity(doc.d, weva_document_query(doc.d, "#x"), &valid) == WEVA_ERR_NOT_FOUND);
    CHECK(valid == 0);
    CHECK(weva_element_check_validity(nullptr, 0, &valid) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_check_validity(doc.d, 0, nullptr) == WEVA_ERR_INVALID_ARGUMENT);
    Doc pattern("", "<form id='f'><input required><input value='abc' pattern='[a-z]+'></form>");
    CHECK(weva_element_check_validity(pattern.d, weva_document_query(pattern.d, "#f"), &valid) == WEVA_ERR_UNSUPPORTED);
    CHECK(valid == 0);
    CHECK(!weva_document_poll_event(pattern.d, &event));
    std::string crowded_html = "<form id='f'>";
    for (int i = 0; i < 257; ++i) crowded_html += "<input required>";
    crowded_html += "</form>";
    Doc crowded("", crowded_html.c_str());
    CHECK(weva_element_check_validity(crowded.d, weva_document_query(crowded.d, "#f"), &valid) == WEVA_ERR_INTERNAL);
    CHECK(valid == 0);
    CHECK(!weva_document_poll_event(crowded.d, &event));
}

void test_abi_interaction_version() {
    CHECK(weva_document_interaction_version(nullptr) == 0);
    Doc doc("", "<button id='a'>A</button><button id='b'>B</button>");
    const auto a = weva_document_query(doc.d, "#a"), b = weva_document_query(doc.d, "#b");
    const auto initial = weva_document_interaction_version(doc.d);
    CHECK(weva_document_interaction_version(doc.d) == initial);
    CHECK(weva_document_set_focus(doc.d, a) == WEVA_OK);
    const auto focused = weva_document_interaction_version(doc.d);
    CHECK(focused != initial);
    CHECK(weva_document_set_focus(doc.d, a) == WEVA_OK);
    CHECK(weva_document_interaction_version(doc.d) == focused);
    CHECK(weva_document_update(doc.d, 0) == WEVA_OK);
    CHECK(weva_document_interaction_version(doc.d) == focused);
    CHECK(weva_document_set_focus(doc.d, b) == WEVA_OK);
    CHECK(weva_document_set_focus(doc.d, a) == WEVA_OK);
    CHECK(weva_document_interaction_version(doc.d) != focused);
    CHECK(weva_document_focus(doc.d) == a);
}
