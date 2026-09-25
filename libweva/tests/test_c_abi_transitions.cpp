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
    struct EasedReversalCase { const char* easing; double forward; double widths[4]; };
    // Chrome 152 getBoundingClientRect samples; tolerate its 1/64px rounding.
    const EasedReversalCase eased_cases[] = {
        {"ease", .2, {159.046875,145.671875,168.09375,300}},
        {"ease", .7, {288.140625,281.375,300,300}},
        {"ease-in-out", .2, {116.328125,105.09375,109.140625,300}},
        {"ease-in-out", .7, {262.515625,261.3125,281.796875,300}},
        {"cubic-bezier(.3,-.8,.7,1.8)", .2, {76.484375,100,70.28125,300}},
        {"cubic-bezier(.3,-.8,.7,1.8)", .7, {293.96875,314.140625,300,300}},
        {"steps(4,end)", .2, {100,100,100,300}},
        {"steps(4,end)", .7, {200,200,200,300}},
    };
    for (const auto& c : eased_cases) {
        const std::string css = std::string("#a{width:100px;height:20px;transition:width 1s ") + c.easing + "}";
        Doc doc(css.c_str(), kHtml);
        const auto check_width = [&](int sample) {
            const double actual = doc.width_of("#a");
            if (std::fabs(actual - c.widths[sample]) >= .05)
                std::fprintf(stderr, "reversal %s at %.2f sample %d: got %.6f expected %.6f\n",
                    c.easing, c.forward, sample, actual, c.widths[sample]);
            CHECK(std::fabs(actual - c.widths[sample]) < .05);
        };
        const auto element = weva_document_query(doc.d, "#a");
        weva_element_set_style(doc.d, element, "width", "300px");
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, c.forward);
        check_width(0);
        weva_element_set_style(doc.d, element, "width", "100px");
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, .05);
        check_width(1);
        weva_element_set_style(doc.d, element, "width", "300px");
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, .1);
        check_width(2);
        weva_document_update(doc.d, 1);
        check_width(3);
    }
    for (bool negative_delay : {false, true}) {
        Doc doc("#a{width:100px;transition:width 1s linear}", kHtml);
        const auto element = weva_document_query(doc.d, "#a");
        weva_element_set_style(doc.d, element, "width", "300px");
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, .5);
        weva_element_set_style(doc.d, element, "transition-delay", negative_delay ? "-.2s" : ".2s");
        weva_element_set_style(doc.d, element, "width", "100px");
        weva_document_update(doc.d, 0);
        CHECK(std::fabs(doc.width_of("#a") - (negative_delay ? 180 : 200)) < .01);
        weva_document_update(doc.d, negative_delay ? .15 : .45);
        CHECK(std::fabs(doc.width_of("#a") - 150) < .01);
        weva_document_update(doc.d, .3);
        CHECK(std::fabs(doc.width_of("#a") - 100) < .01);
        CHECK(weva_document_is_animating(doc.d) == 0);
    }
    for (bool reverse_again : {false, true}) {
        Doc doc("#a{width:100px;height:20px;transition:width 1s linear}", kHtml);
        const auto element = weva_document_query(doc.d, "#a");
        weva_element_set_style(doc.d, element, "width", "300px");
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, .5);
        CHECK(std::fabs(doc.width_of("#a") - 200) < .01);
        weva_element_set_style(doc.d, element, "width", "100px");
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, .25);
        CHECK(std::fabs(doc.width_of("#a") - 150) < .01);
        weva_element_set_style(doc.d, element, "width", reverse_again ? "300px" : "400px");
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, .25);
        CHECK(std::fabs(doc.width_of("#a") - (reverse_again ? 200 : 212.5)) < .01);
        weva_document_update(doc.d, reverse_again ? .5 : .75);
        CHECK(std::fabs(doc.width_of("#a") - (reverse_again ? 300 : 400)) < .01);
        CHECK(weva_document_is_animating(doc.d) == 0);
    }
    for (const char* value : {"none", "opacity", "0s"}) {
        for (bool retarget : {false, true}) {
            Doc doc("#a{width:100px;height:20px;transition:width 1s linear}", kHtml);
            const auto element = weva_document_query(doc.d, "#a");
            CHECK(weva_element_set_style(doc.d, element, "width", "300px") == WEVA_OK);
            weva_document_update(doc.d, 0);
            weva_document_update(doc.d, .5);
            CHECK(std::fabs(doc.width_of("#a") - 200) < .01);
            const bool duration_only = value[0] == '0';
            CHECK(weva_element_set_style(doc.d, element, duration_only ? "transition-duration" : "transition-property", value) == WEVA_OK);
            if (retarget) CHECK(weva_element_set_style(doc.d, element, "width", "400px") == WEVA_OK);
            weva_document_update(doc.d, 0);
            const bool continues = duration_only && !retarget;
            CHECK(std::fabs(doc.width_of("#a") - (continues ? 200 : retarget ? 400 : 300)) < .01);
            CHECK(weva_document_is_animating(doc.d) == (continues ? 1 : 0));
            const auto serial = weva_document_draw_serial(doc.d);
            weva_document_update(doc.d, .25);
            CHECK(std::fabs(doc.width_of("#a") - (continues ? 250 : retarget ? 400 : 300)) < .01);
            if (!continues) CHECK(weva_document_draw_serial(doc.d) == serial);
        }
    }
    {
        Doc doc("#a{width:100px;transition:width 1s steps(2,end)}", kHtml);
        const auto element = weva_document_query(doc.d, "#a");
        CHECK(weva_element_set_style(doc.d, element, "width", "300px") == WEVA_OK);
        weva_document_update(doc.d, 0);
        const auto before_step = weva_document_draw_serial(doc.d);
        weva_document_update(doc.d, .25);
        CHECK(std::fabs(doc.width_of("#a") - 100) < 0.01);
        CHECK(weva_document_draw_serial(doc.d) == before_step);
        weva_document_update(doc.d, .25);
        CHECK(std::fabs(doc.width_of("#a") - 200) < 0.01);
        CHECK(weva_document_draw_serial(doc.d) != before_step);
        const auto after_step = weva_document_draw_serial(doc.d);
        weva_document_update(doc.d, .25);
        CHECK(weva_document_draw_serial(doc.d) == after_step);
    }
    for (const char* duration : {"0s", "1s"}) {
        const std::string css = std::string("#a{width:100px;height:20px;transition:width ") + duration + " linear .5s}";
        Doc doc(css.c_str(), kHtml);
        const auto element = weva_document_query(doc.d, "#a");
        CHECK(weva_element_set_style(doc.d, element, "width", "300px") == WEVA_OK);
        weva_document_update(doc.d, 0);
        CHECK(weva_document_is_animating(doc.d) == 1);
        CHECK(std::fabs(doc.width_of("#a") - 100) < 0.01);
        const auto serial = weva_document_draw_serial(doc.d);
        weva_document_update(doc.d, .25);
        CHECK(std::fabs(doc.width_of("#a") - 100) < 0.01);
        CHECK(weva_document_draw_serial(doc.d) == serial);
        weva_document_update(doc.d, .25);
        CHECK(std::fabs(doc.width_of("#a") - (duration[0] == '0' ? 300 : 100)) < 0.01);
        CHECK(weva_document_is_animating(doc.d) == (duration[0] == '0' ? 0 : 1));
    }
    for (const char* delay : {"-1s", "-2s"}) {
        const std::string css = std::string("#a{width:100px;transition:width 1s linear ") + delay + "}";
        Doc doc(css.c_str(), kHtml);
        const auto element = weva_document_query(doc.d, "#a");
        CHECK(weva_element_set_style(doc.d, element, "width", "300px") == WEVA_OK);
        weva_document_update(doc.d, 0);
        CHECK(std::fabs(doc.width_of("#a") - 300) < 0.01);
        CHECK(weva_document_is_animating(doc.d) == 0);
    }
    // Long property lists must retain late matches and last-match precedence.
    for (int prefix_count : {32, 33, 64}) {
        for (bool earlier_match : {false, true}) {
            std::string properties = earlier_match ? "width" : "opacity";
            for (int i = 1; i < prefix_count; ++i) properties += ",opacity";
            properties += ",width";
            const std::string css = "#a{width:100px;height:20px;transition-property:" + properties +
                ";transition-duration:1s,2s;transition-timing-function:linear}";
            Doc doc(css.c_str(), kHtml);
            const auto element = weva_document_query(doc.d, "#a");
            CHECK(weva_element_set_style(doc.d, element, "width", "300px") == WEVA_OK);
            weva_document_update(doc.d, 0);
            CHECK(weva_document_is_animating(doc.d) == 1);
            weva_document_update(doc.d, 0.5);
            const double expected = prefix_count % 2 ? 150 : 200;
            CHECK(std::fabs(doc.width_of("#a") - expected) < 0.01);
        }
    }
    for (const char* target : {"#a", "#panel"}) {
        Doc doc("#a{width:100px;height:20px;transition:width 1s linear}",
                "<div id=panel><div id=a>x</div></div>");
        const auto element = weva_document_query(doc.d, "#a");
        const auto panel = weva_document_query(doc.d, target);
        CHECK(weva_element_set_style(doc.d, element, "width", "300px") == WEVA_OK);
        weva_document_update(doc.d, 0);
        weva_document_update(doc.d, 0.5);
        CHECK(std::fabs(doc.width_of("#a") - 200) < 0.01);
        CHECK(weva_element_set_style(doc.d, panel, "display", "none") == WEVA_OK);
        weva_document_update(doc.d, 0);
        CHECK(weva_document_is_animating(doc.d) == 0);
        const auto serial = weva_document_draw_serial(doc.d);
        weva_document_update(doc.d, 0.25);
        CHECK(weva_document_draw_serial(doc.d) == serial);
        CHECK(weva_element_set_style(doc.d, element, "width", "400px") == WEVA_OK);
        weva_document_update(doc.d, 0);
        CHECK(weva_document_is_animating(doc.d) == 0);
        CHECK(weva_element_set_style(doc.d, panel, "display", "block") == WEVA_OK);
        CHECK(weva_element_set_style(doc.d, element, "width", "500px") == WEVA_OK);
        weva_document_update(doc.d, 0);
        CHECK(std::fabs(doc.width_of("#a") - 500) < 0.01);
        CHECK(weva_document_is_animating(doc.d) == 0);
        CHECK(weva_element_set_style(doc.d, element, "width", "600px") == WEVA_OK);
        weva_document_update(doc.d, 0);
        CHECK(weva_document_is_animating(doc.d) == 1);
        weva_document_update(doc.d, 0.5);
        CHECK(std::fabs(doc.width_of("#a") - 550) < 0.01);
    }
    {
        Doc doc("#a{width:100px;height:20px;transition:width 1s linear}", kHtml);
        const auto element = weva_document_query(doc.d, "#a");
        CHECK(weva_element_set_style(doc.d, element, "width", "300px") == WEVA_OK);
        CHECK(weva_element_set_style(doc.d, element, "visibility", "hidden") == WEVA_OK);
        weva_document_update(doc.d, 0);
        CHECK(weva_document_is_animating(doc.d) == 1);
        weva_document_update(doc.d, 0.5);
        CHECK(std::fabs(doc.width_of("#a") - 200) < 0.01);
    }
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
        // The reverse travels half the distance in half the original duration.
        CHECK(std::fabs(doc.width_of("#a") - 100) < .01);
        CHECK(weva_document_is_animating(doc.d) == 0);
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
