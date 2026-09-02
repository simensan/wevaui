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
    CHECK(focused > unfocused);       // the bar is an extra draw

    // A focused field keeps the document animating, or a host that stops
    // handing over time freezes the cursor mid-blink.
    CHECK(weva_document_is_animating(doc.d) == 1);

    // Half a second on, half off.
    weva_document_update(doc.d, 0.6);
    size_t dark = 0;
    weva_document_draws(doc.d, &dark);
    CHECK(dark == unfocused);
    weva_document_update(doc.d, 0.5);
    size_t lit = 0;
    weva_document_draws(doc.d, &lit);
    CHECK(lit == focused);

    // Moving it restarts the blink: a cursor that winks out while you are
    // moving it is worse than none.
    weva_document_update(doc.d, 0.6);
    weva_document_draws(doc.d, &dark);
    CHECK(dark == unfocused);
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
