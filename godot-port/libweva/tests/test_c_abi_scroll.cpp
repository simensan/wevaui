// Scrolling: moving what a container clips.
//
// `overflow: auto` already CLIPPED -- paint scissored and hit testing stopped
// at the padding box -- so a list too long for its box showed its first rows
// and hid the rest for good. That is a picture of a list. These drive the part
// that makes it one: a wheel over it, a script setting a position, and the
// clamping that keeps both honest when the content changes size underneath.
#include "check.h"
#include "weva_c.h"

#include <cstring>
#include <string>

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
    // Which element is drawn at a point, by id.
    std::string id_at(double x, double y) {
        const weva_element_t e = weva_document_element_at(d, x, y);
        if (e == WEVA_ELEMENT_NONE) return "";
        char buf[64] = {0};
        weva_element_attribute(d, e, "id", buf, sizeof(buf));
        return buf;
    }
};

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
