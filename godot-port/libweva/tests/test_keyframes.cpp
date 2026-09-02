// @keyframes, end to end.
//
// The parser already kept these: a deferred at-rule preserves its prelude and
// its body, and a keyframes body IS a list of style rules whose selectors are
// offsets. So nothing had to change there -- this reads what was always sitting
// in the tree unread.
#include "check.h"
#include "weva/keyframes.h"
#include "weva_c.h"

#include <cmath>
#include <cstring>
#include <map>
#include <string>

using namespace weva;

namespace {

bool near(double a, double b, double eps = 1e-4) { return std::fabs(a - b) < eps; }

std::map<std::string, KeyframeAnimation> parse(const char* css) {
    Stylesheet sheet;
    CssParseError err;
    CHECK(parse_stylesheet(css, false, &sheet, &err));
    std::map<std::string, KeyframeAnimation> out;
    collect_keyframes(sheet, &out);
    return out;
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
    Doc(const Doc&) = delete;
    Doc& operator=(const Doc&) = delete;

    double width() {
        double x = 0, y = 0, w = 0, h = 0;
        weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h);
        return w;
    }
};

const char* kHtml = "<div id=a>x</div>";

}   // namespace

void test_keyframes_parsing() {
    {
        const auto k = parse("@keyframes pulse { from { opacity: 1 } to { opacity: 0 } }");
        CHECK(k.size() == 1);
        CHECK(k.count("pulse") == 1);
        const KeyframeAnimation& a = k.at("pulse");
        CHECK(a.frames.size() == 2);
        CHECK(near(a.frames[0].offset, 0.0));
        CHECK(near(a.frames[1].offset, 1.0));
        CHECK(a.properties.size() == 1 && a.properties[0] == "opacity");
    }
    {
        // Percentages, and one rule stating several offsets at once -- which is
        // how a loop says its two ends agree.
        const auto k = parse("@keyframes hum { 0%, 100% { width: 10px } 50% { width: 30px } }");
        const KeyframeAnimation& a = k.at("hum");
        CHECK(a.frames.size() == 3);
        // Sorted, so a lookup is a walk.
        CHECK(near(a.frames[0].offset, 0.0));
        CHECK(near(a.frames[1].offset, 0.5));
        CHECK(near(a.frames[2].offset, 1.0));
    }
    {
        // A later definition of a name replaces the earlier one whole.
        const auto k = parse("@keyframes x { from { width: 1px } }"
                             "@keyframes x { from { width: 2px } to { width: 9px } }");
        CHECK(k.size() == 1);
        CHECK(k.at("x").frames.size() == 2);
    }
    {
        const auto k = parse("@keyframes empty { }  @media screen { }  .a { color: red }");
        CHECK(k.empty());
    }
}

void test_keyframe_sampling() {
    const auto k = parse("@keyframes hum { 0% { width: 0px } 50% { width: 100px }"
                         "                 100% { width: 0px } }");
    const KeyframeAnimation& a = k.at("hum");
    std::string v;
    CHECK(keyframe_value_at(a, "width", 0.0, &v) && v == "0px");
    CHECK(keyframe_value_at(a, "width", 0.25, &v) && v == "50px");
    CHECK(keyframe_value_at(a, "width", 0.5, &v) && v == "100px");
    CHECK(keyframe_value_at(a, "width", 0.75, &v) && v == "50px");
    CHECK(keyframe_value_at(a, "width", 1.0, &v) && v == "0px");
    // A property no frame mentions is not this animation's business.
    CHECK(!keyframe_value_at(a, "height", 0.5, &v));

    // A frame that does not mention the property does not bound it: opacity is
    // interpolated across the whole run even though the middle frame is silent
    // about it. That is what lets frames set different properties.
    const auto p = parse("@keyframes mixed { 0% { opacity: 0 } 50% { width: 5px }"
                         "                   100% { opacity: 1 } }");
    CHECK(keyframe_value_at(p.at("mixed"), "opacity", 0.5, &v) && v == "0.5");
}

void test_keyframes_run() {
    // ---- a linear loop, sampled through one cycle
    {
        Doc doc("html, body { margin: 0 }"
                "@keyframes grow { from { width: 100px } to { width: 300px } }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     animation: grow 1s linear infinite }",
                kHtml);
        CHECK(doc.width() == 100);
        weva_document_update(doc.d, 0.5);
        const double mid = doc.width();
        CHECK(mid > 190 && mid < 210);
        // Past the end of a cycle it wraps, because the count is infinite.
        weva_document_update(doc.d, 0.75);
        const double wrapped = doc.width();
        CHECK(wrapped > 140 && wrapped < 160);
        CHECK(weva_document_is_animating(doc.d) == 1);
    }

    // ---- a single iteration stops, and without a fill mode snaps back
    {
        Doc doc("html, body { margin: 0 }"
                "@keyframes grow { from { width: 100px } to { width: 300px } }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     animation: grow 1s linear }",
                kHtml);
        weva_document_update(doc.d, 0.5);
        CHECK(doc.width() > 190 && doc.width() < 210);
        weva_document_update(doc.d, 1.0);
        // The cascaded value is 100px, and nothing holds the end.
        CHECK(doc.width() == 100);
        CHECK(weva_document_is_animating(doc.d) == 0);
    }

    // ---- `forwards` holds the last frame
    {
        Doc doc("html, body { margin: 0 }"
                "@keyframes grow { from { width: 100px } to { width: 300px } }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     animation: grow 1s linear forwards }",
                kHtml);
        weva_document_update(doc.d, 2.0);
        CHECK(doc.width() == 300);
    }

    // ---- a delay holds the start, and `backwards` shows the first frame
    //      during it rather than the cascaded value
    {
        Doc doc("html, body { margin: 0 }"
                "@keyframes grow { from { width: 220px } to { width: 300px } }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     animation: grow 1s linear 1s backwards }",
                kHtml);
        weva_document_update(doc.d, 0.5);
        CHECK(doc.width() == 220);      // waiting, but showing frame zero
    }

    // ---- `alternate` runs the second cycle backwards
    {
        Doc doc("html, body { margin: 0 }"
                "@keyframes grow { from { width: 100px } to { width: 300px } }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     animation: grow 1s linear infinite alternate }",
                kHtml);
        weva_document_update(doc.d, 0.5);
        const double first = doc.width();
        CHECK(first > 190 && first < 210);
        // 1.5s in: half through the SECOND cycle, which runs backwards, so it
        // is at the same place -- and 1.75s must be smaller, not larger.
        weva_document_update(doc.d, 1.25);
        const double second = doc.width();
        CHECK(second > 140 && second < 160);
    }

    // ---- an animation beats a transition on the same property
    {
        Doc doc("html, body { margin: 0 }"
                "@keyframes grow { from { width: 100px } to { width: 300px } }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     transition: width 10s linear;"
                "     animation: grow 1s linear forwards }",
                kHtml);
        weva_document_update(doc.d, 1.0);
        // The transition would still be at the very start of a ten-second run;
        // the animation is done. CSS Cascade L5 §6.1 puts animations above.
        CHECK(doc.width() == 300);
    }

    // ---- a name that matches no @keyframes does nothing at all
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222;"
                "     animation: nosuch 1s linear infinite }",
                kHtml);
        weva_document_update(doc.d, 0.5);
        CHECK(doc.width() == 100);
        CHECK(weva_document_is_animating(doc.d) == 0);
    }

    // ---- a document with no animation stays free
    {
        Doc doc("html, body { margin: 0 }"
                "#a { width: 100px; height: 40px; background: #222 }",
                kHtml);
        size_t before = 0;
        weva_document_draws(doc.d, &before);
        weva_document_update(doc.d, 1.0);
        weva_document_update(doc.d, 1.0);
        size_t after = 0;
        weva_document_draws(doc.d, &after);
        CHECK(before == after);
        CHECK(weva_document_is_animating(doc.d) == 0);
    }
}
