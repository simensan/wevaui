// :hover, :active and :focus, driven the way a host drives them.
//
// The cascade could always match these -- ElementState carries the bits and
// the matcher takes a provider -- but the document handed it the null provider,
// so every such rule matched nothing and no test noticed, because there was
// nothing to point at them with. These go through the ABI: move a pointer,
// press a button, set focus, and check the DRAWS change accordingly.
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

// Every distinct solid vertex colour in the frame, so a restyle shows up as a
// colour appearing or leaving without depending on draw order.
std::vector<uint32_t> colours(weva_document_t d) {
    std::vector<uint32_t> out;
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(d, &count);
    for (size_t i = 0; i < count; ++i) {
        for (size_t v = 0; v < draws[i].vertex_count; ++v) {
            const weva_vertex& vert = draws[i].vertices[v];
            if (vert.a < 0.5f) continue;
            // Vertex colours are LINEAR light; CSS is written in sRGB. Convert
            // on the way out so the expectations below read as the hex the
            // stylesheet states.
            const auto q = [](float f) {
                const float v = std::fmin(std::fmax(f, 0.0f), 1.0f);
                const float srgb =
                    v <= 0.0031308f ? v * 12.92f : 1.055f * std::pow(v, 1.0f / 2.4f) - 0.055f;
                return static_cast<uint32_t>(std::lround(srgb * 255));
            };
            const uint32_t key = (q(vert.r) << 16) | (q(vert.g) << 8) | q(vert.b);
            bool seen = false;
            for (uint32_t k : out) {
                if (k == key) { seen = true; break; }
            }
            if (!seen) out.push_back(key);
        }
    }
    return out;
}

bool has_colour(const std::vector<uint32_t>& set, uint32_t rgb) {
    for (uint32_t k : set) {
        // The engine works in linear light and the vertex colours come back
        // converted, so an exact match is not the question -- presence is.
        const int dr = static_cast<int>((k >> 16) & 0xff) - static_cast<int>((rgb >> 16) & 0xff);
        const int dg = static_cast<int>((k >> 8) & 0xff) - static_cast<int>((rgb >> 8) & 0xff);
        const int db = static_cast<int>(k & 0xff) - static_cast<int>(rgb & 0xff);
        if (std::abs(dr) <= 2 && std::abs(dg) <= 2 && std::abs(db) <= 2) return true;
    }
    return false;
}

struct Doc {
    weva_document_t d = nullptr;
    explicit Doc(const char* css, const char* html) {
        weva_config c = config();
        d = weva_document_create(&c);
        weva_document_add_css(d, css, std::strlen(css));
        weva_document_load_html(d, html, std::strlen(html));
        weva_document_update(d, 0);
    }
    ~Doc() { weva_document_destroy(d); }
    Doc(const Doc&) = delete;
    Doc& operator=(const Doc&) = delete;
};

// 100x60 at (0,0), then a second below it, with a child in the first.
const char* kHtml =
    "<div id=a><span id=inner>x</span></div>"
    "<div id=b>y</div>";
const char* kCss =
    "html, body { margin: 0 } "
    "div { width: 100px; height: 60px; background: #202020 } "
    "span { display: block; width: 40px; height: 20px; background: #303030 }";

}   // namespace

void test_abi_hit_testing() {
    Doc doc(kCss, kHtml);
    const weva_element_t a = weva_document_query(doc.d, "#a");
    const weva_element_t b = weva_document_query(doc.d, "#b");
    const weva_element_t inner = weva_document_query(doc.d, "#inner");
    CHECK(a != WEVA_ELEMENT_NONE && b != WEVA_ELEMENT_NONE && inner != WEVA_ELEMENT_NONE);

    // Over the child: the child, not the parent it sits in. The deepest box
    // wins, which is what makes hovering a control hover the control.
    CHECK(weva_document_element_at(doc.d, 10, 10) == inner);
    // Over the parent but past the child.
    CHECK(weva_document_element_at(doc.d, 80, 40) == a);
    // The second block, below the first.
    CHECK(weva_document_element_at(doc.d, 50, 80) == b);
    // Off the elements entirely. The body still covers the viewport, so this
    // is not NONE -- it is simply not one of the divs.
    const weva_element_t far = weva_document_element_at(doc.d, 380, 280);
    CHECK(far != a && far != b && far != inner);
    // Outside the document.
    CHECK(weva_document_element_at(doc.d, -5, -5) == WEVA_ELEMENT_NONE);

    // Text hands the hit to the element it is set in, not to a text box.
    CHECK(weva_document_element_at(doc.d, 4, 8) == inner);

    // Pointer coordinates follow the painted transform, including ancestors.
    for (const char* transform : {"translate(120px,80px)", "translate(30vw,80px)"}) {
        weva_element_set_attribute(doc.d, a, "style", (std::string("transform:") + transform).c_str());
        weva_document_update(doc.d, 0);
        CHECK(weva_document_element_at(doc.d, 130, 90) == inner);
        CHECK(weva_document_element_at(doc.d, 10, 10) != inner);
    }
    weva_element_set_attribute(doc.d, inner, "style", "transform-origin:0 0;transform:scale(2)");
    weva_document_update(doc.d, 0);
    CHECK(weva_document_element_at(doc.d, 190, 110) == inner);
    weva_element_set_attribute(doc.d, a, "style", "transform-origin:0 0;transform:translate(120px,80px) rotate(90deg)");
    weva_document_update(doc.d, 0);
    CHECK(weva_document_element_at(doc.d, 90, 150) == inner);
    CHECK(weva_document_element_at(doc.d, 130, 90) != inner);
    weva_element_set_attribute(doc.d, a, "style", "transform:scale(0)");
    weva_document_update(doc.d, 0);
    CHECK(weva_document_element_at(doc.d, 10, 10) != inner);
    // Overflow clipping is evaluated in the transformed container's frame.
    weva_element_set_attribute(doc.d, a, "style", "transform:translate(120px,80px);overflow:hidden;width:30px");
    weva_document_update(doc.d, 0);
    CHECK(weva_document_element_at(doc.d, 140, 90) == inner);
    CHECK(weva_document_element_at(doc.d, 160, 90) != inner);

    Doc slider("html,body{margin:0}input{display:block;margin:0;padding:0;border:0;width:100px;height:20px;"
               "transform-origin:0 0;transform:translate(150px,80px) rotate(90deg)}",
               "<input id=r type=range min=0 max=100 value=0>");
    const auto range = weva_document_query(slider.d, "#r");
    weva_document_set_pointer(slider.d, 140, 105, WEVA_BUTTON_PRIMARY);
    char value[32]{};
    weva_element_value(slider.d, range, value, sizeof(value));
    // Thumb-centre travel is 86px (100px track minus the 14px thumb).
    // Chrome with the same thumb geometry returns round((25-7)/86*100) = 21.
    CHECK_EQ(std::string(value), "21");
    // Captured dragging follows the rotated track even beyond its end.
    weva_document_set_pointer(slider.d, 140, 200, WEVA_BUTTON_PRIMARY);
    weva_document_set_pointer(slider.d, 140, 200, 0);
    weva_element_value(slider.d, range, value, sizeof(value));
    CHECK_EQ(std::string(value), "100");
}

void test_abi_hover_active_focus() {
    {
        Doc slider("html,body{margin:0}input{display:block;width:100px;height:30px;padding:0;border:0}",
                   "<input id=r type=range value=10>");
        const auto range = weva_document_query(slider.d, "#r");
        weva_document_set_pointer(slider.d, 20, 15, WEVA_BUTTON_PRIMARY);
        char before[32]{}, after[32]{};
        weva_element_value(slider.d, range, before, sizeof(before));
        weva_element_set_attribute(slider.d, range, "disabled", "");
        weva_document_update(slider.d, 0);
        weva_document_set_pointer(slider.d, 90, 15, WEVA_BUTTON_PRIMARY);
        weva_document_set_pointer(slider.d, 90, 15, 0);
        weva_element_value(slider.d, range, after, sizeof(after));
        CHECK(std::string(before) == after);
    }
    {
        Doc doc("html,body{margin:0}button{display:block;width:100px;height:60px;background:#202020}"
                "button:hover{background:#cc0000}button:active{background:#00cc00}",
                "<button id=off disabled on-click=forbidden>Unavailable</button>");
        weva_document_set_pointer(doc.d, 50, 30, 0);
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0xcc0000));
        weva_document_set_pointer(doc.d, 50, 30, WEVA_BUTTON_PRIMARY);
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0x00cc00));
        weva_document_set_pointer(doc.d, 50, 30, 0);
        weva_document_update(doc.d, 0);
        CHECK(weva_document_focus(doc.d) == WEVA_ELEMENT_NONE);
        int enter = 0, down = 0, up = 0;
        weva_event event{};
        while (weva_document_poll_event(doc.d, &event)) {
            CHECK(event.kind != WEVA_EVENT_CLICK);
            enter += event.kind == WEVA_EVENT_POINTER_ENTER;
            down += event.kind == WEVA_EVENT_POINTER_DOWN;
            up += event.kind == WEVA_EVENT_POINTER_UP;
        }
        CHECK(enter == 1 && down == 1 && up == 1);
    }
    // ---- :hover on the element under the pointer, and on its ancestors
    {
        Doc doc("html, body { margin: 0 }"
                "div { width: 100px; height: 60px; background: #202020 }"
                "span { display: block; width: 40px; height: 20px; background: #303030 }"
                "#a:hover { background: #cc0000 }"
                "#a:hover span { background: #00cc00 }",
                kHtml);
        CHECK(!has_colour(colours(doc.d), 0xcc0000));

        // Pointer over the CHILD. The parent is hovered too, so both rules
        // apply -- the ancestor one is the whole reason hover is a chain.
        weva_document_set_pointer(doc.d, 10, 10, 0);
        weva_document_update(doc.d, 0);
        std::vector<uint32_t> c = colours(doc.d);
        CHECK(has_colour(c, 0xcc0000));
        CHECK(has_colour(c, 0x00cc00));

        // Moving to the other block drops both.
        weva_document_set_pointer(doc.d, 50, 80, 0);
        weva_document_update(doc.d, 0);
        c = colours(doc.d);
        CHECK(!has_colour(c, 0xcc0000));
        CHECK(!has_colour(c, 0x00cc00));

        // Back on, then off the surface entirely.
        weva_document_set_pointer(doc.d, 80, 40, 0);
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0xcc0000));
        weva_document_clear_pointer(doc.d);
        weva_document_update(doc.d, 0);
        CHECK(!has_colour(colours(doc.d), 0xcc0000));
    }

    // ---- :active follows the button, and latches to what was pressed
    {
        Doc doc("html, body { margin: 0 }"
                "div { width: 100px; height: 60px; background: #202020 }"
                "#a:active { background: #0000cc }",
                kHtml);
        weva_document_set_pointer(doc.d, 50, 30, 0);
        weva_document_update(doc.d, 0);
        CHECK(!has_colour(colours(doc.d), 0x0000cc));

        weva_document_set_pointer(doc.d, 50, 30, 1);
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0x0000cc));

        // Dragging off it while held keeps it pressed, which is what a button
        // does -- the press belongs to where it started.
        weva_document_set_pointer(doc.d, 50, 90, 1);
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0x0000cc));

        // Release.
        weva_document_set_pointer(doc.d, 50, 90, 0);
        weva_document_update(doc.d, 0);
        CHECK(!has_colour(colours(doc.d), 0x0000cc));
    }

    // ---- :focus on the element, :focus-within on its ancestors
    {
        Doc doc("html, body { margin: 0 }"
                "div { width: 100px; height: 60px; background: #202020 }"
                "span { display: block; width: 40px; height: 20px; background: #303030 }"
                "#inner:focus { background: #cccc00 }"
                "#a:focus-within { background: #00cccc }",
                kHtml);
        CHECK(!has_colour(colours(doc.d), 0xcccc00));

        const weva_element_t inner = weva_document_query(doc.d, "#inner");
        CHECK(weva_document_set_focus(doc.d, inner) == WEVA_OK);
        weva_document_update(doc.d, 0);
        std::vector<uint32_t> c = colours(doc.d);
        CHECK(has_colour(c, 0xcccc00));
        CHECK(has_colour(c, 0x00cccc));

        CHECK(weva_document_set_focus(doc.d, WEVA_ELEMENT_NONE) == WEVA_OK);
        weva_document_update(doc.d, 0);
        c = colours(doc.d);
        CHECK(!has_colour(c, 0xcccc00));
        CHECK(!has_colour(c, 0x00cccc));
    }

    // ---- a hover rule that changes LAYOUT, not just colour
    //
    // The incremental update classifies a change by which properties moved, so
    // this has to come out the same as a document that was never hovered but
    // has the rule applied outright.
    {
        Doc doc("html, body { margin: 0 }"
                "div { width: 100px; height: 60px; background: #202020 }"
                "#a:hover { width: 250px }",
                kHtml);
        double x = 0, y = 0, w = 0, h = 0;
        const weva_element_t a = weva_document_query(doc.d, "#a");
        CHECK(weva_element_bounds(doc.d, a, &x, &y, &w, &h) == WEVA_OK);
        CHECK(w == 100);

        weva_document_set_pointer(doc.d, 50, 30, 0);
        weva_document_update(doc.d, 0);
        CHECK(weva_element_bounds(doc.d, a, &x, &y, &w, &h) == WEVA_OK);
        CHECK(w == 250);

        // And the second block moved with it? No -- it is below, not beside.
        // What matters is that leaving puts it back.
        weva_document_clear_pointer(doc.d);
        weva_document_update(doc.d, 0);
        CHECK(weva_element_bounds(doc.d, a, &x, &y, &w, &h) == WEVA_OK);
        CHECK(w == 100);
    }

    // ---- a sibling selector, which reaches PAST the hovered element
    //
    // The scoped restyle only walks what a change can reach, and this is the
    // case that would be missed if hover were treated as element-local.
    {
        Doc doc("html, body { margin: 0 }"
                "div { width: 100px; height: 60px; background: #202020 }"
                "#a:hover + div { background: #cc00cc }",
                kHtml);
        CHECK(!has_colour(colours(doc.d), 0xcc00cc));
        weva_document_set_pointer(doc.d, 50, 30, 0);
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0xcc00cc));
        weva_document_clear_pointer(doc.d);
        weva_document_update(doc.d, 0);
        CHECK(!has_colour(colours(doc.d), 0xcc00cc));
    }

    // ---- a pointer move that changes nothing must not cost a repaint
    {
        Doc doc(kCss, kHtml);
        weva_document_set_pointer(doc.d, 50, 30, 0);
        weva_document_update(doc.d, 0);
        size_t before = 0;
        weva_document_draws(doc.d, &before);
        // Two more moves within the same element, no hover rules anywhere.
        weva_document_set_pointer(doc.d, 55, 35, 0);
        weva_document_update(doc.d, 0);
        weva_document_set_pointer(doc.d, 60, 40, 0);
        weva_document_update(doc.d, 0);
        size_t after = 0;
        weva_document_draws(doc.d, &after);
        CHECK(before == after);
        CHECK(after > 0);
    }
}

// `pointer-events: none` and `visibility: hidden` are not hit targets.
void test_abi_hit_testing_opt_out() {
    {
        Doc doc("html, body { margin: 0 }"
                "div { width: 100px; height: 60px; background: #202020 }"
                "span { display: block; width: 40px; height: 20px; background: #303030;"
                "       pointer-events: none }",
                kHtml);
        // The child declines the hit, so it falls through to the parent.
        CHECK(weva_document_element_at(doc.d, 10, 10) == weva_document_query(doc.d, "#a"));
    }
    {
        Doc doc("html, body { margin: 0 }"
                "div { width: 100px; height: 60px; background: #202020 }"
                "span { display: block; width: 40px; height: 20px; visibility: hidden }",
                kHtml);
        CHECK(weva_document_element_at(doc.d, 10, 10) == weva_document_query(doc.d, "#a"));
    }
    {
        // A clipping box confines the hit to what it does not clip away.
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 30px; overflow: hidden }"
                "span { display: block; width: 40px; height: 200px }",
                kHtml);
        const weva_element_t a = weva_document_query(doc.d, "#a");
        CHECK(weva_document_element_at(doc.d, 10, 10) == weva_document_query(doc.d, "#inner"));
        // Below the clip: the overflowing child is not drawn there, so it is
        // not hit there either.
        const weva_element_t below = weva_document_element_at(doc.d, 10, 100);
        CHECK(below != weva_document_query(doc.d, "#inner"));
        CHECK(below != a);
    }
}


namespace {

// The injected tooltip, if one is up. It carries a marker attribute so a host
// -- and this test -- can tell it from the author's own content.
weva_element_t tooltip_of(weva_document_t d) {
    return weva_document_query(d, "[data-weva-tooltip]");
}

std::string tooltip_text(weva_document_t d) {
    const weva_element_t e = tooltip_of(d);
    if (e == WEVA_ELEMENT_NONE) return "";
    char buf[128] = {0};
    weva_element_text(d, e, buf, sizeof(buf));
    return buf;
}

}   // namespace

// `title="..."` renders as a tooltip after the pointer rests on the element.
// The UA stylesheet has styled `.ui-tooltip` all along and nothing ever made
// one, so `title` was inert.
void test_abi_title_shows_a_tooltip() {
    {
        Doc disabled("html,body{margin:0}button{width:100px;height:40px}",
                     "<button disabled title='Requires a workbench'>Craft</button>");
        weva_document_set_pointer(disabled.d, 50, 20, 0);
        weva_document_update(disabled.d, .7);
        CHECK(tooltip_text(disabled.d) == "Requires a workbench");
    }
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 } div { width: 100px; height: 40px }";
    const char* html = "<div id=a title='Save the file'>Save</div>"
                       "<div id=b title='Discard it'>Cancel</div>"
                       "<div id=c>Plain</div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    // Resting on it is not enough on its own: the delay is the whole point,
    // or a tooltip flashes up every time the pointer crosses the screen.
    weva_document_set_pointer(d, 50, 20, 0);
    weva_document_update(d, 0.1);
    CHECK(tooltip_of(d) == WEVA_ELEMENT_NONE);

    // After the wait, it appears with the title's text.
    weva_document_update(d, 0.6);
    CHECK(tooltip_text(d) == "Save the file");

    // Moving to a DIFFERENT titled element restarts the wait rather than
    // showing the old text at the new place.
    weva_document_set_pointer(d, 50, 60, 0);
    weva_document_update(d, 0.1);
    CHECK(tooltip_of(d) == WEVA_ELEMENT_NONE);
    weva_document_update(d, 0.6);
    CHECK(tooltip_text(d) == "Discard it");

    // Moving onto something with no title takes it away.
    weva_document_set_pointer(d, 50, 100, 0);
    weva_document_update(d, 0.6);
    CHECK(tooltip_of(d) == WEVA_ELEMENT_NONE);
    weva_document_destroy(d);
}

// What dismisses one, and what a tooltip must not do to the document.
void test_abi_tooltip_dismissal() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 } #a { width: 100px; height: 40px }"
                      " span { display: block }";
    const char* html = "<div id=a title='Save the file'><span id=inner>Save</span></div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    // A title on an ancestor covers what is inside it: the closest one wins,
    // and here the only one is the parent.
    weva_document_set_pointer(d, 20, 10, 0);
    weva_document_update(d, 0.7);
    CHECK(tooltip_text(d) == "Save the file");

    // Moving within the SAME element keeps it up and moves it along.
    weva_document_set_pointer(d, 60, 20, 0);
    weva_document_update(d, 0.016);
    CHECK(tooltip_text(d) == "Save the file");

    // Pressing dismisses it: a tooltip over the button you are clicking is in
    // the way of what you came to do.
    weva_document_set_pointer(d, 60, 20, 1);
    weva_document_update(d, 0.016);
    CHECK(tooltip_of(d) == WEVA_ELEMENT_NONE);

    // It does not capture the pointer either -- the UA rule gives it
    // `pointer-events: none`, so what is UNDER it is still what gets hit.
    // Probed inside the tooltip's own rectangle, which is the only place the
    // question means anything: it sits at the pointer plus (12, 18), so with
    // the pointer at (60, 20) it starts at (72, 38), still inside the 100x40
    // div underneath.
    weva_document_set_pointer(d, 60, 20, 0);
    weva_document_update(d, 0.7);
    const weva_element_t tip = tooltip_of(d);
    CHECK(tip != WEVA_ELEMENT_NONE);
    double tx = 0, ty = 0, tw = 0, th = 0;
    CHECK(weva_element_bounds(d, tip, &tx, &ty, &tw, &th) == WEVA_OK);
    CHECK(tx == 72 && ty == 38);   // beside the cursor, not under it
    const weva_element_t under = weva_document_element_at(d, tx + 2, ty + 1);
    CHECK(under != tip);
    CHECK(under == weva_document_query(d, "#a"));
    weva_document_destroy(d);
}

// A right-click is not a click. The engine treated any held button as a
// press, so the secondary button toggled checkboxes, submitted forms, opened
// <details> and worked popovers -- none of which it does in a browser.
void test_abi_secondary_button_does_not_activate() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 } input, button, summary { display: block;"
                      " width: 120px; height: 30px }";
    const char* html = "<input id=cb type=checkbox>"
                       "<details id=dd><summary id=sum>More</summary><p>Body</p></details>"
                       "<form id=f on-submit=OnSubmit><button id=go>Go</button></form>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    const auto press_release = [&](const char* sel, uint32_t button) {
        double x = 0, y = 0, w = 0, h = 0;
        weva_element_bounds(d, weva_document_query(d, sel), &x, &y, &w, &h);
        weva_document_set_pointer(d, x + w / 2, y + h / 2, 0);
        weva_document_set_pointer(d, x + w / 2, y + h / 2, button);
        weva_document_set_pointer(d, x + w / 2, y + h / 2, 0);
        weva_document_update(d, 0);
    };
    const auto has = [&](const char* sel, const char* attr) {
        return weva_element_has_attribute(d, weva_document_query(d, sel), attr) != 0;
    };
    const auto count_kind = [&](int kind) {
        int n = 0;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) {
            if (e.kind == kind) ++n;
        }
        return n;
    };

    // The secondary button changes nothing.
    press_release("#cb", WEVA_BUTTON_SECONDARY);
    CHECK(!has("#cb", "checked"));
    press_release("#sum", WEVA_BUTTON_SECONDARY);
    CHECK(!has("#dd", "open"));
    count_kind(WEVA_EVENT_SUBMIT);
    press_release("#go", WEVA_BUTTON_SECONDARY);
    CHECK(count_kind(WEVA_EVENT_SUBMIT) == 0);

    // The primary button does all three, so the difference is the button and
    // not the test failing to reach anything.
    press_release("#cb", WEVA_BUTTON_PRIMARY);
    CHECK(weva_document_query(d, "#cb:checked") != WEVA_ELEMENT_NONE);
    CHECK(!has("#cb", "checked")); // The reset default remains unchecked.
    press_release("#sum", WEVA_BUTTON_PRIMARY);
    CHECK(has("#dd", "open"));
    count_kind(WEVA_EVENT_SUBMIT);
    press_release("#go", WEVA_BUTTON_PRIMARY);
    CHECK(count_kind(WEVA_EVENT_SUBMIT) == 1);
    weva_document_destroy(d);
}

// What the secondary button DOES do: ask for a context menu, once per press.
void test_abi_context_menu_event() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 } #row { width: 200px; height: 40px }";
    const char* html = "<div id=panel on-contextmenu=OnMenu><div id=row>Item</div></div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    const auto menus = [&]() {
        std::vector<std::pair<std::string, double>> out;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) {
            if (e.kind == WEVA_EVENT_CONTEXT_MENU) out.emplace_back(e.handler, e.x);
        }
        return out;
    };

    weva_document_set_pointer(d, 50, 20, 0);
    menus();
    weva_document_set_pointer(d, 50, 20, WEVA_BUTTON_SECONDARY);
    const auto asked = menus();
    CHECK(asked.size() == 1);
    CHECK(asked[0].first == "OnMenu");   // found on the ancestor, as handlers are
    CHECK(asked[0].second == 50);        // where the menu should open

    // Holding it while moving does not ask again: it is the press that asks.
    weva_document_set_pointer(d, 60, 24, WEVA_BUTTON_SECONDARY);
    CHECK(menus().empty());

    // Releasing and pressing again does.
    weva_document_set_pointer(d, 60, 24, 0);
    weva_document_set_pointer(d, 60, 24, WEVA_BUTTON_SECONDARY);
    CHECK(menus().size() == 1);

    // The primary button never asks.
    weva_document_set_pointer(d, 60, 24, 0);
    weva_document_set_pointer(d, 60, 24, WEVA_BUTTON_PRIMARY);
    CHECK(menus().empty());
    weva_document_destroy(d);
}

// Moving the pointer WITHIN a hovered ancestor must leave that ancestor's
// rules alone.
//
// The cascade is told which elements changed state, and it used to be told
// the whole hover chain -- every ancestor up to <body> -- on every move, which
// meant a walk of the entire document. It is told only the elements that
// actually flipped now, and the risk of that is exactly this case: an
// ancestor hovered before AND after must keep its descendant rules applied,
// even though it is no longer in the changed set.
void test_abi_hover_within_an_ancestor_keeps_its_rules() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0 }"
                      " #panel { width: 200px; height: 120px; background: #111 }"
                      " .row { height: 40px }"
                      // A rule reaching from the hovered ancestor into a
                      // descendant, and one reaching a sibling.
                      " #panel:hover .mark { background: #f00; height: 40px }"
                      " #a:hover + #b { background: #0f0 }";
    const char* html = "<div id=panel><div id=a class=row></div><div id=b class=row></div>"
                       "<div id=m class='row mark'></div></div>"
                       "<div id=outside>away</div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    // How many red rects the document draws: the descendant rule's mark.
    const auto marks = [&]() {
        weva_document_update(d, 0);
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        int red = 0;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].texture_id != 0 || draws[i].vertex_count == 0) continue;
            const weva_vertex& v = draws[i].vertices[0];
            if (v.r > 0.5f && v.g < 0.1f && v.b < 0.1f) ++red;
        }
        return red;
    };

    CHECK(marks() == 0);   // nothing hovered

    // Into the panel, over its first row.
    weva_document_set_pointer(d, 100, 20, 0);
    CHECK(marks() == 1);

    // MOVE WITHIN the panel, to the second row. The panel is hovered before
    // and after, so it is not in the changed set -- and its descendant rule
    // must still hold.
    weva_document_set_pointer(d, 100, 60, 0);
    CHECK(marks() == 1);

    // A third move, still inside.
    weva_document_set_pointer(d, 100, 100, 0);
    CHECK(marks() == 1);

    // Out of the panel entirely: now it flips, and the rule goes with it.
    weva_document_set_pointer(d, 100, 200, 0);
    CHECK(marks() == 0);

    // And back in, to prove the flip works in both directions.
    weva_document_set_pointer(d, 100, 20, 0);
    CHECK(marks() == 1);
    weva_document_destroy(d);
}

// The reach filter: a pointer move only restyles what a rule could match.
//
// This is a SKIP, and the failure mode of a skip is silence -- the element
// keeps its old style and nothing complains. So each form of `:hover` a sheet
// can take gets a case, and each checks that the colour actually moves.
//
// The one that motivated the filter is the last: the user-agent sheet contains
// `.ui-menu-item:hover`, so "does any rule mention :hover" is true for every
// document ever loaded, and a document-wide flag would never skip anything.

void test_abi_hover_reach_inside_has() {
    // `:hover` under `:has()` reaches beyond the hovered element's ancestors,
    // so the keys of the compound it sits on are the wrong answer and the
    // filter has to give up.
    //
    // An ancestor subject is the easy half: `#wrap:has(#a:hover)` is keyed on
    // `#wrap`, which is in the chain anyway.
    {
        Doc doc("html, body { margin: 0 } "
                "div { width: 100px; height: 60px; background: #202020 } "
                "#wrap { height: 120px; background: #101010 } "
                "#wrap:has(#a:hover) { background: #ff8800 }",
                "<div id=wrap><div id=a>x</div><div id=b>y</div></div>");
        CHECK(!has_colour(colours(doc.d), 0xff8800));
        weva_document_set_pointer(doc.d, 50, 30, 0);
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0xff8800));
    }

    // A SIBLING subject: `#a` is neither the hovered element nor one of its
    // ancestors, so nothing about it is in the chain the filter is handed.
    // This passes with the `inside_has` guard removed too -- a sheet with
    // `:has()` in it takes the unscoped walk regardless -- but it is the case
    // that would break first if that ever changed.
    {
        Doc doc("html, body { margin: 0 } "
                "div { width: 100px; height: 60px; background: #202020 } "
                "#a:has(~ #b:hover) { background: #ff8800 }",
                "<div id=a>x</div><div id=b>y</div>");
        CHECK(!has_colour(colours(doc.d), 0xff8800));
        weva_document_set_pointer(doc.d, 50, 90, 0);   // onto #b
        weva_document_update(doc.d, 0);
        CHECK(has_colour(colours(doc.d), 0xff8800));
    }
}

// Captured Chrome152 direction/endpoints; repeat after a live style change and
// with padding/borders to exercise the same public input path as game controls.
void test_abi_range_directions() {
    struct Case { const char* mode; const char* direction; int key; double fraction; int value; };
    const Case cases[] = {
        {"horizontal-tb", "ltr", WEVA_KEY_LEFT, -1, 49},
        {"horizontal-tb", "ltr", WEVA_KEY_RIGHT, -1, 51},
        {"horizontal-tb", "ltr", WEVA_KEY_UP, -1, 51},
        {"horizontal-tb", "ltr", WEVA_KEY_DOWN, -1, 49},
        {"horizontal-tb", "ltr", WEVA_KEY_HOME, -1, 0},
        {"horizontal-tb", "ltr", WEVA_KEY_END, -1, 100},
        {"horizontal-tb", "ltr", WEVA_KEY_PAGE_UP, -1, 60},
        {"horizontal-tb", "ltr", WEVA_KEY_PAGE_DOWN, -1, 40},
        {"horizontal-tb", "ltr", 0, 0, 0},
        {"horizontal-tb", "ltr", 0, 0.5, 50},
        {"horizontal-tb", "ltr", 0, 1, 100},
        {"horizontal-tb", "rtl", WEVA_KEY_LEFT, -1, 51},
        {"horizontal-tb", "rtl", WEVA_KEY_RIGHT, -1, 49},
        {"horizontal-tb", "rtl", WEVA_KEY_UP, -1, 51},
        {"horizontal-tb", "rtl", WEVA_KEY_DOWN, -1, 49},
        {"horizontal-tb", "rtl", WEVA_KEY_HOME, -1, 0},
        {"horizontal-tb", "rtl", WEVA_KEY_END, -1, 100},
        {"horizontal-tb", "rtl", WEVA_KEY_PAGE_UP, -1, 60},
        {"horizontal-tb", "rtl", WEVA_KEY_PAGE_DOWN, -1, 40},
        {"horizontal-tb", "rtl", 0, 0, 100},
        {"horizontal-tb", "rtl", 0, 0.5, 50},
        {"horizontal-tb", "rtl", 0, 1, 0},
        {"vertical-rl", "ltr", WEVA_KEY_LEFT, -1, 49},
        {"vertical-rl", "ltr", WEVA_KEY_RIGHT, -1, 51},
        {"vertical-rl", "ltr", WEVA_KEY_UP, -1, 49},
        {"vertical-rl", "ltr", WEVA_KEY_DOWN, -1, 51},
        {"vertical-rl", "ltr", WEVA_KEY_HOME, -1, 0},
        {"vertical-rl", "ltr", WEVA_KEY_END, -1, 100},
        {"vertical-rl", "ltr", WEVA_KEY_PAGE_UP, -1, 60},
        {"vertical-rl", "ltr", WEVA_KEY_PAGE_DOWN, -1, 40},
        {"vertical-rl", "ltr", 0, 0, 0},
        {"vertical-rl", "ltr", 0, 0.5, 50},
        {"vertical-rl", "ltr", 0, 1, 100},
        {"vertical-rl", "rtl", WEVA_KEY_LEFT, -1, 49},
        {"vertical-rl", "rtl", WEVA_KEY_RIGHT, -1, 51},
        {"vertical-rl", "rtl", WEVA_KEY_UP, -1, 51},
        {"vertical-rl", "rtl", WEVA_KEY_DOWN, -1, 49},
        {"vertical-rl", "rtl", WEVA_KEY_HOME, -1, 0},
        {"vertical-rl", "rtl", WEVA_KEY_END, -1, 100},
        {"vertical-rl", "rtl", WEVA_KEY_PAGE_UP, -1, 60},
        {"vertical-rl", "rtl", WEVA_KEY_PAGE_DOWN, -1, 40},
        {"vertical-rl", "rtl", 0, 0, 100},
        {"vertical-rl", "rtl", 0, 0.5, 50},
        {"vertical-rl", "rtl", 0, 1, 0},
        {"vertical-lr", "ltr", WEVA_KEY_LEFT, -1, 49},
        {"vertical-lr", "ltr", WEVA_KEY_RIGHT, -1, 51},
        {"vertical-lr", "ltr", WEVA_KEY_UP, -1, 49},
        {"vertical-lr", "ltr", WEVA_KEY_DOWN, -1, 51},
        {"vertical-lr", "ltr", WEVA_KEY_HOME, -1, 0},
        {"vertical-lr", "ltr", WEVA_KEY_END, -1, 100},
        {"vertical-lr", "ltr", WEVA_KEY_PAGE_UP, -1, 60},
        {"vertical-lr", "ltr", WEVA_KEY_PAGE_DOWN, -1, 40},
        {"vertical-lr", "ltr", 0, 0, 0},
        {"vertical-lr", "ltr", 0, 0.5, 50},
        {"vertical-lr", "ltr", 0, 1, 100},
        {"vertical-lr", "rtl", WEVA_KEY_LEFT, -1, 49},
        {"vertical-lr", "rtl", WEVA_KEY_RIGHT, -1, 51},
        {"vertical-lr", "rtl", WEVA_KEY_UP, -1, 51},
        {"vertical-lr", "rtl", WEVA_KEY_DOWN, -1, 49},
        {"vertical-lr", "rtl", WEVA_KEY_HOME, -1, 0},
        {"vertical-lr", "rtl", WEVA_KEY_END, -1, 100},
        {"vertical-lr", "rtl", WEVA_KEY_PAGE_UP, -1, 60},
        {"vertical-lr", "rtl", WEVA_KEY_PAGE_DOWN, -1, 40},
        {"vertical-lr", "rtl", 0, 0, 100},
        {"vertical-lr", "rtl", 0, 0.5, 50},
        {"vertical-lr", "rtl", 0, 1, 0},
    };
    for (int variant = 0; variant < 3; ++variant) for (const auto& row : cases) {
        const bool vertical = std::string(row.mode) != "horizontal-tb";
        const std::string style = std::string("position:absolute;left:20px;top:20px;width:") +
            (vertical ? "30px;height:200px;" : "200px;height:30px;") +
            "writing-mode:" + row.mode + ";direction:" + row.direction +
            (variant == 2 ? ";padding:4px;border:2px solid black" : ";padding:0;border:0");
        const std::string css = "#r{" + (variant == 1 ? std::string("width:200px;height:30px") : style) + "}";
        Doc doc(css.c_str(), "<input id=r type=range min=0 max=100 value=50>");
        const auto r = weva_document_query(doc.d, "#r");
        if (variant == 1) {
            CHECK(weva_element_set_attribute(doc.d, r, "style", style.c_str()) == WEVA_OK);
            CHECK(weva_document_update(doc.d, 0) == WEVA_OK);
        }
        CHECK(weva_document_set_focus(doc.d, r) == WEVA_OK);
        if (row.key) {
            CHECK(weva_document_key(doc.d, row.key, 0, 1) != 0);
        } else {
            double x=0,y=0,w=0,h=0;
            CHECK(weva_element_bounds(doc.d,r,&x,&y,&w,&h) == WEVA_OK);
            const double along = std::fmax(1.0, std::fmin((vertical ? h : w)-1, (vertical ? h : w)*row.fraction));
            x += vertical ? w*.5 : along;
            y += vertical ? along : h*.5;
            weva_document_set_pointer(doc.d,x,y,WEVA_BUTTON_PRIMARY);
            weva_document_set_pointer(doc.d,x,y,0);
        }
        char value[32]{};
        weva_element_value(doc.d,r,value,sizeof(value));
        CHECK_EQ(std::string(value),std::to_string(row.value));
    }
}
