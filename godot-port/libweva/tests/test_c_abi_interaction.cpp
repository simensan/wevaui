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
}

void test_abi_hover_active_focus() {
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


// Hit testing through PADDED ancestors.
//
// Box x/y are relative to the parent's BORDER-BOX origin -- layout has already
// baked the padding into them, which is what absolute_position assumes when it
// sums the chain and what paint assumes when it passes its own origin down
// untouched. Adding the padding again in the hit test double-counted it, and
// every test here used `margin: 0` markup with no padding, so nothing caught
// it until a demo panel with `padding: 20px` returned the wrong element for a
// click on its button.
void test_abi_hit_testing_through_padding() {
    Doc doc("html, body { margin: 0 }"
            "#outer { padding: 40px; border: 5px solid #333 }"
            "#inner { padding: 30px }"
            "#target { width: 60px; height: 20px; background: #0f0 }",
            "<div id=outer><div id=inner><div id=target>x</div></div></div>");

    const weva_element_t target = weva_document_query(doc.d, "#target");
    CHECK(target != WEVA_ELEMENT_NONE);

    // Wherever layout put it, that is where the hit test must find it. Asking
    // the document for the bounds and then asking what is at their centre is
    // the whole invariant, and it holds for any nesting.
    double x = 0, y = 0, w = 0, h = 0;
    CHECK(weva_element_bounds(doc.d, target, &x, &y, &w, &h) == WEVA_OK);
    CHECK(x > 70 && y > 70);   // pushed in by two paddings and a border
    CHECK(weva_document_element_at(doc.d, x + w / 2, y + h / 2) == target);

    // Just outside it on each side is NOT the target.
    CHECK(weva_document_element_at(doc.d, x - 2, y + h / 2) != target);
    CHECK(weva_document_element_at(doc.d, x + w + 2, y + h / 2) != target);
    CHECK(weva_document_element_at(doc.d, x + w / 2, y - 2) != target);

    // The same invariant on a flex row inside a padded panel, which is how the
    // failure was actually found.
    Doc row("html, body { margin: 0 }"
            "#panel { margin: 24px; padding: 20px; width: 420px }"
            ".row { display: flex; gap: 10px }"
            "button { padding: 7px 14px }",
            "<div id=panel><div class=row>"
            "<button id=a>one</button><button id=b>two</button></div></div>");
    for (const char* id : {"#a", "#b"}) {
        const weva_element_t e = weva_document_query(row.d, id);
        CHECK(weva_element_bounds(row.d, e, &x, &y, &w, &h) == WEVA_OK);
        CHECK(w > 0 && h > 0);
        CHECK(weva_document_element_at(row.d, x + w / 2, y + h / 2) == e);
    }
}

// What you click must be what is drawn on top. Paint follows CSS 2.1 Appendix
// E -- in-flow children first, positioned ones over them -- and hit testing
// used to walk the child list backwards instead, so the two disagreed for
// every positioned element declared BEFORE an in-flow sibling it overlaps.
//
// Found through a popover: a `position: fixed` menu drawn over a later <div>
// could not be clicked anywhere the two overlapped. Every click went to the
// div underneath, which also made light-dismiss fire on clicks inside the menu.
void test_abi_hit_follows_paint_order() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css =
        "html, body { margin: 0 }"
        // Declared FIRST, drawn LAST, because it is positioned.
        " #over { position: absolute; top: 20px; left: 20px; width: 100px; height: 100px }"
        " #under { height: 200px }";
    const char* html = "<div id=over></div><div id=under></div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);

    const auto at = [&](double x, double y) {
        weva_document_set_pointer(d, x, y, 0);
        weva_document_set_pointer(d, x, y, 1);
        weva_document_set_pointer(d, x, y, 0);
        weva_document_update(d, 0);
        std::string id;
        weva_event e{};
        while (weva_document_poll_event(d, &e)) {
            if (e.kind != WEVA_EVENT_CLICK) continue;
            char buf[64] = {0};
            weva_element_attribute(d, e.target, "id", buf, sizeof(buf));
            id = buf;
        }
        return id;
    };

    // Over the overlap: the positioned one, because that is what is drawn.
    CHECK(at(70, 70) == "over");
    // Outside it: the in-flow one.
    CHECK(at(200, 70) == "under");
    // Below it, still inside the in-flow box.
    CHECK(at(70, 180) == "under");

    // z-index orders positioned siblings against each other, and hit testing
    // has to follow that too.
    const char* more = "#a { position: absolute; top: 0; left: 0; width: 100px; height: 100px;"
                       " z-index: 5 } #b { position: absolute; top: 0; left: 0; width: 100px;"
                       " height: 100px; z-index: 1 }";
    weva_document_add_css(d, more, std::strlen(more));
    const char* two = "<div id=a></div><div id=b></div>";
    weva_document_load_html(d, two, std::strlen(two));
    weva_document_update(d, 0);
    // `a` is declared first but has the higher z-index, so it is on top --
    // reverse tree order would have answered `b`.
    CHECK(at(50, 50) == "a");
    weva_document_destroy(d);
}
