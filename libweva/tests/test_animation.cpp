#include "check.h"
#include "weva/animation.h"

#include <cmath>
#include <string>

using namespace weva;

namespace {

bool near(double a, double b, double eps = 1e-4) { return std::fabs(a - b) < eps; }

std::string mix(const char* from, const char* to, double t) {
    std::string out;
    CHECK(interpolate_css(from, to, t, &out));
    return out;
}

}   // namespace

void test_easing_curves() {
    Easing e;

    // Every curve passes through its ends, whatever its shape between them.
    for (const char* name : {"linear", "ease", "ease-in", "ease-out", "ease-in-out",
                             "cubic-bezier(0.68, -0.55, 0.27, 1.55)"}) {
        CHECK(parse_easing(name, &e));
        CHECK(near(e(0.0), 0.0, 1e-3));
        CHECK(near(e(1.0), 1.0, 1e-3));
    }

    CHECK(parse_easing("linear", &e));
    CHECK(near(e(0.25), 0.25, 1e-3));
    CHECK(near(e(0.5), 0.5, 1e-3));

    // `ease` is cubic-bezier(0.25, 0.1, 0.25, 1): ahead of linear in the middle,
    // which is the whole point of it.
    CHECK(parse_easing("ease", &e));
    CHECK(e(0.5) > 0.5);
    CHECK(parse_easing("ease-in", &e));
    CHECK(e(0.5) < 0.5);            // slow to start
    CHECK(parse_easing("ease-out", &e));
    CHECK(e(0.5) > 0.5);            // quick to start

    // A bezier that overshoots is legal and must be allowed to leave 0..1 on
    // the Y axis -- that is what a spring-like curve IS.
    CHECK(parse_easing("cubic-bezier(0.68, -0.55, 0.27, 1.55)", &e));
    bool overshoots = false;
    for (double t = 0; t <= 1.0; t += 0.02) {
        if (e(t) > 1.001 || e(t) < -0.001) overshoots = true;
    }
    CHECK(overshoots);

    // X outside 0..1 is not a function of time and is rejected.
    CHECK(!parse_easing("cubic-bezier(1.5, 0, 0.5, 1)", &e));
    CHECK(!parse_easing("cubic-bezier(0, 0)", &e));
    CHECK(!parse_easing("wobble", &e));

    // Steps. `steps(4, end)` holds 0 through the first quarter and reaches 1
    // only at the end; `start` jumps immediately.
    CHECK(parse_easing("steps(4)", &e));
    CHECK(near(e(0.1), 0.0));
    CHECK(near(e(0.3), 0.25));
    CHECK(near(e(1.0), 1.0));
    CHECK(parse_easing("steps(4, start)", &e));
    CHECK(near(e(0.1), 0.25));
    CHECK(parse_easing("step-start", &e));
    CHECK(near(e(0.01), 1.0));
    CHECK(parse_easing("step-end", &e));
    CHECK(near(e(0.99), 0.0));
    CHECK(!parse_easing("steps(0)", &e));
}

void test_time_parsing() {
    double v = 0;
    CHECK(parse_time_seconds("1.5s", &v) && near(v, 1.5));
    CHECK(parse_time_seconds("300ms", &v) && near(v, 0.3));
    CHECK(parse_time_seconds("0", &v) && near(v, 0));
    CHECK(parse_time_seconds("0s", &v) && near(v, 0));
    // A negative delay is legal: it starts the animation part-way through.
    CHECK(parse_time_seconds("-200ms", &v) && near(v, -0.2));
    CHECK(!parse_time_seconds("300", &v));      // a bare number is not a time
    CHECK(!parse_time_seconds("fast", &v));
    CHECK(!parse_time_seconds("", &v));
}

void test_value_interpolation() {
    // Lengths sharing a unit.
    CHECK(mix("0px", "100px", 0.25) == "25px");
    CHECK(mix("10px", "20px", 0.5) == "15px");
    CHECK(mix("4em", "8em", 0.5) == "6em");
    CHECK(mix("0%", "50%", 0.5) == "25%");

    // Numbers, which is what carries opacity, flex-grow, z-index.
    CHECK(mix("0", "1", 0.5) == "0.5");
    CHECK(mix("1", "0", 0.25) == "0.75");

    // Angles.
    CHECK(mix("0deg", "90deg", 0.5) == "45deg");

    // Colours, premultiplied so a fade to `transparent` does not go through
    // black -- transparent IS rgba(0,0,0,0), and mixing its unpremultiplied
    // channels would darken whatever it fades from.
    CHECK(mix("#000000", "#ffffff", 0.5) == "rgba(128, 128, 128, 1)");
    const std::string faded = mix("#ff0000", "transparent", 0.5);
    CHECK(faded.rfind("rgba(255, 0, 0", 0) == 0);

    // Lists interpolate item by item when they have the same shape, which is
    // how `padding: 4px 8px` and a multi-part shadow get there.
    CHECK(mix("0px 0px", "10px 20px", 0.5) == "5px 10px");
    CHECK(mix("1px, 2px", "3px, 6px", 0.5) == "2px, 4px");

    // Everything else is discrete, and CSS says a discrete property flips at
    // the halfway point.
    CHECK(mix("block", "inline", 0.4) == "block");
    CHECK(mix("block", "inline", 0.6) == "inline");
    // Mixed units need a resolution context this does not have, so they are
    // discrete rather than guessed at.
    CHECK(mix("1em", "20px", 0.4) == "1em");
    CHECK(mix("1em", "20px", 0.6) == "20px");
    // A list whose shape changes is a different value, not a mixable one.
    CHECK(mix("1px 2px", "3px", 0.6) == "3px");

    // The ends are exact, which matters more than anything between them: a
    // transition that does not land on its declared value leaves the document
    // subtly wrong forever.
    CHECK(mix("10px", "250px", 0.0) == "10px");
    CHECK(mix("10px", "250px", 1.0) == "250px");
    CHECK(mix("block", "inline", 1.0) == "inline");
}
