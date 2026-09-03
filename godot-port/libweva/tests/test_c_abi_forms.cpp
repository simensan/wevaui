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

    // Focus puts the cursor after what the field holds, where a user expects
    // to carry on typing.
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
    weva_document_text_input(doc.d, "!");
    weva_document_update(doc.d, 0);
    CHECK(doc.value("#t") == "hello!");
    // And it is the TEXT that changed, which is what gets laid out: an
    // attribute nobody reads would leave the box showing the old string.
    char buf[64] = {0};
    weva_element_text(doc.d, t, buf, sizeof(buf));
    CHECK(std::string(buf) == "hello!");

    // A host setting it writes to the same place.
    CHECK(weva_element_set_value(doc.d, t, "typed by the game") == WEVA_OK);
    weva_document_update(doc.d, 0);
    weva_element_text(doc.d, t, buf, sizeof(buf));
    CHECK(std::string(buf) == "typed by the game");
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

    // Focused, the caret sits after the last character: on the second line,
    // two characters in.
    weva_document_set_focus(doc.d, t);
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

// A `multiple` list toggles, and reports everything chosen. The ABI carries
// buttons and not modifiers, so there is no Ctrl+click to tell a toggle from a
// replace -- and toggling is what a settings list wants anyway.
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
    CHECK(doc.value("#s") == "a,c");     // both, in document order

    // And clicking one again lets it go.
    doc.click(x + w / 2, y + h / 2);
    CHECK(doc.value("#s") == "a");

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
    CHECK(weva_element_show_dialog(doc.d, d, 1) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 1);

    // Reopening non-modally takes it away again -- the backdrop must not
    // outlive the modality that asked for it.
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

    // The attribute pair is the whole state: no controller, no stored flag.
    CHECK(weva_element_set_attribute(doc.d, p, "data-popover-open", "") == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.backdrops() == 1);

    CHECK(weva_element_set_attribute(doc.d, p, "data-popover-open", nullptr) == WEVA_OK);
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
            "<div id=menu popover>Menu</div>"
            "<div id=pinned popover=manual>Pinned</div>"
            "<div id=submenu popover>Submenu</div>");
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
