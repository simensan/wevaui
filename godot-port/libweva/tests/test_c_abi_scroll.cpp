// Scrolling: moving what a container clips.
//
// `overflow: auto` already CLIPPED -- paint scissored and hit testing stopped
// at the padding box -- so a list too long for its box showed its first rows
// and hid the rest for good. That is a picture of a list. These drive the part
// that makes it one: a wheel over it, a script setting a position, and the
// clamping that keeps both honest when the content changes size underneath.
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

    weva_element_t at(const char* selector) { return weva_document_query(d, selector); }
    double top(const char* selector) {
        double y = 0;
        weva_element_scroll(d, at(selector), nullptr, &y, nullptr, nullptr);
        return y;
    }
    double max_top(const char* selector) {
        double m = 0;
        weva_element_scroll(d, at(selector), nullptr, nullptr, nullptr, &m);
        return m;
    }
    std::string value(const char* selector) {
        char buf[128] = {0};
        weva_element_attribute(d, at(selector), "value", buf, sizeof(buf));
        return buf;
    }
    // Which element is drawn at a point, by id.
    std::string id_at(double x, double y) {
        const weva_element_t e = weva_document_element_at(d, x, y);
        if (e == WEVA_ELEMENT_NONE) return "";
        char buf[64] = {0};
        weva_element_attribute(d, e, "id", buf, sizeof(buf));
        return buf;
    }
};

// Every distinct opaque-ish vertex colour in the frame, as sRGB hex. The
// engine works in linear light; the stylesheet is written in sRGB.
std::vector<uint32_t> colours(weva_document_t d) {
    std::vector<uint32_t> out;
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(d, &count);
    for (size_t i = 0; i < count; ++i) {
        for (size_t v = 0; v < draws[i].vertex_count; ++v) {
            const weva_vertex& vert = draws[i].vertices[v];
            if (vert.a < 0.4f) continue;
            const auto q = [](float f) {
                const float c = f < 0 ? 0 : (f > 1 ? 1 : f);
                const float srgb =
                    c <= 0.0031308f ? c * 12.92f : 1.055f * std::pow(c, 1.0f / 2.4f) - 0.055f;
                return static_cast<uint32_t>(srgb * 255 + 0.5f);
            };
            const uint32_t key = (q(vert.r) << 16) | (q(vert.g) << 8) | q(vert.b);
            bool seen = false;
            for (uint32_t k : out) seen = seen || k == key;
            if (!seen) out.push_back(key);
        }
    }
    return out;
}

bool near_colour(const std::vector<uint32_t>& set, uint32_t rgb) {
    for (uint32_t k : set) {
        const int dr = static_cast<int>((k >> 16) & 0xff) - static_cast<int>((rgb >> 16) & 0xff);
        const int dg = static_cast<int>((k >> 8) & 0xff) - static_cast<int>((rgb >> 8) & 0xff);
        const int db = static_cast<int>(k & 0xff) - static_cast<int>(rgb & 0xff);
        if (dr * dr + dg * dg + db * db <= 300) return true;
    }
    return false;
}

// A 100px-tall viewport onto 5 rows of 40px: 200 of content, 100 of room.
const char* kListCss =
    "html, body { margin: 0 }"
    ".list { width: 200px; height: 100px; overflow: auto }"
    ".row { height: 40px }";
const char* kListHtml =
    "<div id=list class=list>"
    "<div id=r0 class=row></div><div id=r1 class=row></div><div id=r2 class=row></div>"
    "<div id=r3 class=row></div><div id=r4 class=row></div>"
    "</div>";

}   // namespace

// How far there is to go, and that a container with room to spare has nowhere.
void test_abi_scroll_extent() {
    Doc doc(kListCss, kListHtml);
    // Five 40px rows in a 100px box: 200 of content, 100 of it visible.
    CHECK(doc.max_top("#list") == 100);
    CHECK(doc.top("#list") == 0);

    // Content that fits leaves nothing to scroll, and the wheel over it says
    // so rather than pretending it moved.
    Doc small(kListCss, "<div id=list class=list><div id=r0 class=row></div></div>");
    CHECK(small.max_top("#list") == 0);
    CHECK(weva_document_scroll(small.d, 100, 50, 0, 40) == 0);

    // End padding is part of the scrollable area (Overflow L3 3), so a padded
    // list does not end flush against its last row. `height` is the CONTENT
    // box, so there are 120 of room; the rows reach 130 from the padding edge
    // and the bottom padding takes that to 140.
    Doc padded("html, body { margin: 0 }"
               ".list { width: 200px; height: 100px; overflow: auto; padding: 10px }"
               ".row { height: 40px }",
               "<div id=list class=list><div class=row></div><div class=row></div>"
               "<div class=row></div></div>");
    CHECK(padded.max_top("#list") == 20);
}

// The wheel, and where it stops.
void test_abi_scroll_wheel() {
    Doc doc(kListCss, kListHtml);
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, 30) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 30);

    // Past the end clamps rather than running off, and the wheel that ran out
    // of room reports that nothing moved -- which is how a host knows to hand
    // the wheel to whatever is behind the document.
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, 500) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, 10) == 0);

    // And back up, to exactly zero.
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, -500) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 0);
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, -10) == 0);

    // A wheel over nothing scrollable is not consumed.
    Doc plain("html, body { margin: 0 } .box { width: 200px; height: 100px }",
              "<div id=box class=box></div>");
    CHECK(weva_document_scroll(plain.d, 50, 50, 0, 40) == 0);
}

// What you see is what you hit: the content moves, and so do its targets.
void test_abi_scroll_moves_content() {
    Doc doc(kListCss, kListHtml);
    // Rows 0 and 1 fill the visible 100px; row 2 starts below the fold.
    CHECK(doc.id_at(100, 10) == "r0");
    CHECK(doc.id_at(100, 50) == "r1");
    CHECK(doc.id_at(100, 130) == "");   // clipped away, and not hit there

    weva_document_scroll(doc.d, 100, 50, 0, 80);
    weva_document_update(doc.d, 0);
    // 80 down: row 2 (at 80) is now at the top, row 4 (at 160) at y=80.
    CHECK(doc.id_at(100, 10) == "r2");
    CHECK(doc.id_at(100, 90) == "r4");
    // The container itself has not moved -- only what it holds.
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(doc.d, doc.at("#list"), &x, &y, &w, &h);
    CHECK(y == 0);
    CHECK(h == 100);
}

// The inner list takes the wheel until it has nowhere left to go, and then the
// page gets it. Without this a scrolled-to-the-bottom list swallows the wheel
// and the page under it feels stuck.
void test_abi_scroll_nested() {
    Doc doc("html, body { margin: 0 }"
            ".page { width: 300px; height: 150px; overflow: auto }"
            ".list { width: 200px; height: 100px; overflow: auto }"
            ".row { height: 40px }"
            ".tail { height: 300px }",
            "<div id=page class=page>"
            "<div id=list class=list>"
            "<div class=row></div><div class=row></div><div class=row></div>"
            "<div class=row></div><div class=row></div></div>"
            "<div class=tail></div></div>");
    CHECK(doc.max_top("#list") == 100);
    CHECK(doc.max_top("#page") > 0);

    // Over the list: the list moves, the page does not.
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, 60) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 60);
    CHECK(doc.top("#page") == 0);

    // Once the list is at its end the wheel goes past it to the page.
    weva_document_scroll(doc.d, 100, 50, 0, 100);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, 40) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);
    CHECK(doc.top("#page") == 40);
}

// A script's own position, and what happens when the content changes under it.
void test_abi_scroll_set_and_clamp() {
    Doc doc(kListCss, kListHtml);
    CHECK(weva_element_set_scroll(doc.d, doc.at("#list"), 0, 70) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 70);

    // Past the end is clamped by the update, not stored as given.
    weva_element_set_scroll(doc.d, doc.at("#list"), 0, 9999);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);

    // The offset is kept on the ELEMENT, so a relayout does not lose it: this
    // one rebuilds the whole box tree.
    weva_element_set_attribute(doc.d, doc.at("#r0"), "class", "row wide");
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);

    // Content shrinking under a scrolled view pulls it back up rather than
    // leaving it staring at the space past the end.
    weva_element_set_attribute(doc.d, doc.at("#r3"), "style", "display: none");
    weva_element_set_attribute(doc.d, doc.at("#r4"), "style", "display: none");
    weva_document_update(doc.d, 0);
    CHECK(doc.max_top("#list") == 20);
    CHECK(doc.top("#list") == 20);

    // And a box that does not clip cannot be scrolled at all.
    Doc plain("html, body { margin: 0 } .box { width: 100px; height: 50px } .tall { height: 500px }",
              "<div id=box class=box><div class=tall></div></div>");
    weva_element_set_scroll(plain.d, plain.at("#box"), 0, 40);
    weva_document_update(plain.d, 0);
    CHECK(plain.top("#box") == 0);
    CHECK(plain.max_top("#box") == 0);
}

// Scrolling republishes the frame. A host that scrolled and drew what it
// already had would show the same picture and look frozen.
//
// The rows are given different colours deliberately: with five identical ones
// the draw list after a scroll is byte-identical to the one before -- correct,
// and no evidence of anything.
void test_abi_scroll_repaints() {
    Doc doc("html, body { margin: 0 }"
            ".list { width: 200px; height: 100px; overflow: auto }"
            ".row { height: 40px }",
            "<div id=list class=list>"
            "<div class=row style='background: #ff0000'></div>"
            "<div class=row style='background: #00ff00'></div>"
            "<div class=row style='background: #0000ff'></div>"
            "<div class=row style='background: #ffff00'></div>"
            "<div class=row style='background: #ff00ff'></div></div>");
    // Which channel leads the topmost drawn thing: red first, green once the
    // red row has been scrolled off the top.
    const auto first_colour = [](weva_document_t d) {
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        CHECK(count > 0);
        const weva_vertex& v = draws[0].vertices[0];
        return v.r > v.g ? 'r' : 'g';
    };
    CHECK(first_colour(doc.d) == 'r');

    weva_document_scroll(doc.d, 100, 50, 0, 40);
    weva_document_update(doc.d, 0);
    CHECK(first_colour(doc.d) == 'g');

    // And the geometry moved with it: what is drawn now starts one row up.
    weva_document_scroll(doc.d, 100, 50, 0, -40);
    weva_document_update(doc.d, 0);
    CHECK(first_colour(doc.d) == 'r');
}

// Bringing something into view: the least scrolling that shows it.
void test_abi_scroll_into_view() {
    Doc doc(kListCss, kListHtml);
    // Row 4 lies from 160 to 200 in a 100-tall window: it comes up just far
    // enough to sit against the bottom edge, not to the top.
    CHECK(weva_element_scroll_into_view(doc.d, doc.at("#r4")) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);

    // Something already in view does not move anything.
    CHECK(weva_element_scroll_into_view(doc.d, doc.at("#r3")) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);

    // And one above the view comes down only to the top edge.
    weva_element_scroll_into_view(doc.d, doc.at("#r1"));
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 40);

    // Focus does it by itself, or a tab into a list appears to go nowhere.
    weva_element_set_scroll(doc.d, doc.at("#list"), 0, 0);
    weva_document_update(doc.d, 0);
    weva_document_set_focus(doc.d, doc.at("#r4"));
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);
}

// Nested containers each scroll by their own share.
void test_abi_scroll_into_view_nested() {
    Doc doc("html, body { margin: 0 }"
            ".page { width: 300px; height: 100px; overflow: auto }"
            ".pad { height: 200px }"
            ".list { width: 200px; height: 100px; overflow: auto }"
            ".row { height: 40px }",
            "<div id=page class=page>"
            "<div class=pad></div>"
            "<div id=list class=list>"
            "<div class=row></div><div class=row></div><div class=row></div>"
            "<div class=row></div><div id=r4 class=row></div></div></div>");
    CHECK(weva_element_scroll_into_view(doc.d, doc.at("#r4")) == WEVA_OK);
    weva_document_update(doc.d, 0);
    // The list scrolls to its end to show the row, and the page scrolls to
    // show the part of the list the row is now in.
    CHECK(doc.top("#list") == 100);
    CHECK(doc.top("#page") == 200);
}

// The scrollbar: the thing that says there is more of the list.
//
// Overlay, so it takes no layout space -- a classic bar reserves a gutter and
// would move every box inside a scroller, which is not what a UI laid out to a
// designer's sizes should do when its content happens to grow.
void test_abi_scrollbar_appears() {
    Doc doc(kListCss, kListHtml);
    size_t with_bar = 0;
    weva_document_draws(doc.d, &with_bar);

    // The same list with nothing to scroll draws one thing less.
    Doc fits(kListCss, "<div id=list class=list><div id=r0 class=row></div></div>");
    size_t without = 0;
    weva_document_draws(fits.d, &without);
    CHECK(with_bar > without);

    // The layout is untouched by it: the rows are the width they were.
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(doc.d, doc.at("#r0"), &x, &y, &w, &h);
    CHECK(w == 200);

    // `scrollbar-width: none` is how a game keeps the scrolling and drops the
    // furniture.
    Doc bare("html, body { margin: 0 }"
             ".list { width: 200px; height: 100px; overflow: auto; scrollbar-width: none }"
             ".row { height: 40px }",
             kListHtml);
    size_t hidden = 0;
    weva_document_draws(bare.d, &hidden);
    CHECK(hidden < with_bar);
    CHECK(bare.max_top("#list") == 100);   // and it still scrolls

    // `scrollbar-color: <thumb> <track>` paints it in the page's own colours.
    Doc coloured("html, body { margin: 0 }"
                 ".list { width: 200px; height: 100px; overflow: auto;"
                 "        scrollbar-color: #ff00ff #003300 }"
                 ".row { height: 40px }",
                 kListHtml);
    const std::vector<uint32_t> set = colours(coloured.d);
    CHECK(near_colour(set, 0xff00ff));
    CHECK(near_colour(set, 0x003300));
}

// Dragging the thumb, which is the other half of having one.
void test_abi_scrollbar_drag() {
    Doc doc(kListCss, kListHtml);
    // 100 of view onto 200: the thumb is half the 100px track, so it has 50px
    // of travel worth 100 of scroll -- two units of list per pixel of thumb.
    // It sits at the right edge, 9px wide.
    weva_document_set_pointer(doc.d, 195, 25, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 0);   // taking hold moves nothing

    weva_document_set_pointer(doc.d, 195, 45, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 40);

    // The pointer wandering off the bar does not drop the drag: it is held
    // until the button comes up, which is what every scrollbar does.
    weva_document_set_pointer(doc.d, 20, 60, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 70);

    // And past the end stops at the end.
    weva_document_set_pointer(doc.d, 20, 400, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);

    // Let go, and the pointer is the pointer again.
    weva_document_set_pointer(doc.d, 100, 20, 0);
    weva_document_update(doc.d, 0);
    weva_document_set_pointer(doc.d, 100, 20, 1);
    weva_document_set_pointer(doc.d, 100, 20, 0);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);   // a click on the content scrolls nothing
}

// Clicking the track beside the thumb pages along it.
void test_abi_scrollbar_track_click() {
    Doc doc(kListCss, kListHtml);
    // Below the thumb, which spans the top half of the track.
    weva_document_set_pointer(doc.d, 195, 80, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);   // a page is 100, and that is the end
    weva_document_set_pointer(doc.d, 195, 80, 0);

    // And back: the thumb is now at the bottom, so above it pages up.
    weva_document_set_pointer(doc.d, 195, 10, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 0);
}

// A drag is not a click: whatever the pointer passes over is not pressed, and
// nothing under it is left hovered when the drag ends.
void test_abi_scrollbar_drag_does_not_click() {
    Doc doc("html, body { margin: 0 }"
            ".list { width: 200px; height: 100px; overflow: auto }"
            ".row { height: 40px }",
            "<div id=list class=list>"
            "<div id=r0 class=row></div><div id=r1 class=row></div><div id=r2 class=row></div>"
            "<div id=r3 class=row></div><div id=r4 class=row></div></div>");
    weva_document_set_pointer(doc.d, 195, 25, 1);
    weva_document_set_pointer(doc.d, 100, 30, 1);   // over a row, mid-drag
    weva_document_set_pointer(doc.d, 100, 30, 0);
    weva_document_update(doc.d, 0);

    weva_event e{};
    while (weva_document_poll_event(doc.d, &e)) {
        CHECK(e.kind != WEVA_EVENT_CLICK);
    }
}

// `overflow: hidden` clips, and a script can still move it, but it is not a
// scroller: no bar, and the wheel goes past it to whatever is. Half a page's
// decoration is a clipped box, and every one of them would have worn a
// scrollbar without this.
void test_abi_hidden_is_not_a_scroller() {
    Doc doc("html, body { margin: 0 }"
            ".list { width: 200px; height: 100px; overflow: hidden }"
            ".row { height: 40px }",
            kListHtml);
    Doc shown(kListCss, kListHtml);
    size_t hidden_draws = 0, shown_draws = 0;
    weva_document_draws(doc.d, &hidden_draws);
    weva_document_draws(shown.d, &shown_draws);
    CHECK(hidden_draws < shown_draws);   // no bar on the clipped one

    // The wheel over it is not consumed, so the page behind it gets it.
    CHECK(weva_document_scroll(doc.d, 100, 50, 0, 40) == 0);
    // But a script can still put it where it likes, as in a browser.
    weva_element_set_scroll(doc.d, doc.at("#list"), 0, 40);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 40);

    // One axis at a time: a row of cards that scrolls sideways and clips
    // vertically takes a horizontal wheel and not a vertical one.
    Doc sideways("html, body { margin: 0 }"
                 ".strip { width: 200px; height: 60px; overflow-x: auto; overflow-y: hidden;"
                 "         white-space: nowrap }"
                 ".card { display: inline-block; width: 120px; height: 100px }",
                 "<div id=strip class=strip><div class=card></div><div class=card></div>"
                 "<div class=card></div></div>");
    CHECK(weva_document_scroll(sideways.d, 100, 30, 0, 40) == 0);
    CHECK(weva_document_scroll(sideways.d, 100, 30, 40, 0) == 1);
}

// Scrolling from the keyboard, which is the only way a keyboard user reaches
// the bottom of a list.
void test_abi_scroll_keys() {
    Doc doc("html, body { margin: 0 }"
            ".list { width: 200px; height: 100px; overflow: auto }"
            ".row { height: 40px }",
            "<div id=list class=list tabindex=0>"
            "<div id=r0 class=row></div><div id=r1 class=row></div><div id=r2 class=row></div>"
            "<div id=r3 class=row></div><div id=r4 class=row></div></div>");
    // Around whatever has focus: here the list itself.
    weva_document_set_focus(doc.d, doc.at("#list"));
    CHECK(weva_document_key(doc.d, WEVA_KEY_DOWN, 0, 1) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 40);   // a line, as for the wheel

    // A page is what you can see less a line, so the eye keeps its place.
    CHECK(weva_document_key(doc.d, WEVA_KEY_PAGE_DOWN, 0, 1) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);   // 40 + 60, and that is the end
    CHECK(weva_document_key(doc.d, WEVA_KEY_PAGE_UP, 0, 1) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 40);

    // End and Home go the whole way.
    weva_document_key(doc.d, WEVA_KEY_END, 0, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 100);
    weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 0);

    // Focus inside the list scrolls the list: the key belongs to the nearest
    // container that can take it, not to the element that has focus.
    weva_document_set_focus(doc.d, doc.at("#r0"));
    weva_document_update(doc.d, 0);
    CHECK(weva_document_key(doc.d, WEVA_KEY_DOWN, 0, 1) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 40);

    // With nothing focused it is what the pointer is over -- what a browser
    // scrolls when you have clicked nothing.
    weva_document_set_focus(doc.d, WEVA_ELEMENT_NONE);
    weva_document_set_pointer(doc.d, 100, 50, 0);
    weva_document_update(doc.d, 0);
    CHECK(weva_document_key(doc.d, WEVA_KEY_DOWN, 0, 1) == 1);
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 80);
}

// The keys a text field wants are the field's, and a key with nothing to
// scroll is left for the host.
void test_abi_scroll_keys_yield() {
    Doc doc("html, body { margin: 0 }"
            ".list { width: 200px; height: 100px; overflow: auto }"
            ".row { height: 40px }"
            "input { display: block; width: 100px }",
            "<div id=list class=list>"
            "<input id=f type=text value=abc>"
            "<div class=row></div><div class=row></div><div class=row></div></div>");
    weva_document_set_focus(doc.d, doc.at("#f"));
    // Home in a field is the start of the value, not the top of the list.
    CHECK(weva_document_key(doc.d, WEVA_KEY_HOME, 0, 1) == 1);
    weva_document_text_input(doc.d, "z");
    CHECK(doc.value("#f") == "zabc");
    weva_document_update(doc.d, 0);
    CHECK(doc.top("#list") == 0);

    // Nothing scrollable: the key is NOT consumed, so a host can use the
    // arrows for its own menu.
    Doc plain("html, body { margin: 0 } .box { width: 100px; height: 50px }",
              "<div id=box class=box></div>");
    weva_document_set_focus(plain.d, plain.at("#box"));
    CHECK(weva_document_key(plain.d, WEVA_KEY_DOWN, 0, 1) == 0);
    CHECK(weva_document_key(plain.d, WEVA_KEY_PAGE_DOWN, 0, 1) == 0);

    // And a key that is nobody's is nobody's.
    CHECK(weva_document_key(plain.d, WEVA_KEY_ESCAPE, 0, 1) == 0);
}

namespace {

size_t draw_count_of(const char* css, const char* html, double scroll_to) {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);
    weva_element_set_scroll(d, weva_document_query(d, "#list"), 0, scroll_to);
    weva_document_update(d, 0);
    size_t n = 0;
    weva_document_draws(d, &n);
    weva_document_destroy(d);
    return n;
}

}   // namespace

// Painting skips a subtree that cannot reach the clip it is inside, which is
// what makes a long list cheap -- scrolling a 2,000-row one went from 100 ms a
// frame to 4. The risk is culling something whose DECORATION still reaches in:
// a row scrolled out of view whose shadow falls back into it.
void test_abi_paint_cull_keeps_reaching_shadows() {
    const char* html = "<div id=list>"
                       "<div class=row id=first></div>"
                       "<div class=row></div><div class=row></div><div class=row></div>"
                       "<div class=row></div><div class=row></div><div class=row></div>"
                       "</div>";
    const char* plain = "html, body { margin: 0 }"
                        " #list { width: 200px; height: 60px; overflow-y: auto }"
                        " .row { height: 40px; background: #eee }";
    // The same document, with a shadow on the row that scrolling puts above
    // the visible area -- reaching 50px down, back into it.
    const char* shadowed = "html, body { margin: 0 }"
                           " #list { width: 200px; height: 60px; overflow-y: auto }"
                           " .row { height: 40px; background: #eee }"
                           " #first { box-shadow: 0 50px 0 0 #f00 }";

    // Scrolled past the first row: its own box is outside the clip.
    const size_t without = draw_count_of(plain, html, 80);
    const size_t with = draw_count_of(shadowed, html, 80);
    CHECK(with > without);   // the shadow is still drawn

    // And the cull is doing something: a list scrolled to the top draws fewer
    // things than one with no clipping at all, because the rows below the
    // 60px viewport are skipped rather than built and thrown away.
    const char* unclipped = "html, body { margin: 0 }"
                            " #list { width: 200px; height: 60px; overflow-y: visible }"
                            " .row { height: 40px; background: #eee }";
    CHECK(draw_count_of(plain, html, 0) < draw_count_of(unclipped, html, 0));
}
