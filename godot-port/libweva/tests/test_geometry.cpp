#include "check.h"
#include "weva/color.h"
#include "weva/geometry.h"
#include <cmath>

void test_geometry() {
    using namespace weva;

    Rect r(10, 20, 100, 50);
    CHECK(r.right() == 110 && r.bottom() == 70);
    CHECK(!r.is_empty());
    CHECK(Rect(0, 0, 0, 10).is_empty());

    // Half-open on the far edges, matching C# Rect.Contains.
    CHECK(r.contains(10, 20));
    CHECK(!r.contains(110, 70));
    CHECK(r.contains(109.999, 69.999));

    CHECK(r.intersect(Rect(60, 40, 100, 100)) == Rect(60, 40, 50, 30));
    CHECK(r.intersect(Rect(500, 500, 10, 10)).is_empty());
    // Edge-touching rects share no area.
    CHECK(r.intersect(Rect(110, 20, 10, 10)).is_empty());

    auto id = Transform2D::identity();
    double x, y;
    id.apply(3, 4, &x, &y);
    CHECK(x == 3 && y == 4);

    Transform2D::translate(5, 6).apply(1, 1, &x, &y);
    CHECK(x == 6 && y == 7);

    // multiply(): this applies first, then other.
    auto t = Transform2D::scale(2, 2).multiply(Transform2D::translate(10, 0));
    t.apply(3, 4, &x, &y);
    CHECK(x == 16 && y == 8);

    auto rot = Transform2D::rotate(90);
    rot.apply(1, 0, &x, &y);
    CHECK(std::fabs(x) < 1e-6 && std::fabs(y - 1) < 1e-6);

    CHECK(BorderRadii::zero().is_zero());
    CHECK(!BorderRadii::uniform(4).is_zero());
    CHECK(BorderRadii::uniform(3).top_left == CornerRadius(3, 3));

    // Endpoints must be exact or White stops round-tripping to 1.0.
    CHECK(srgb_byte_to_linear(0) == 0.0f);
    CHECK(srgb_byte_to_linear(255) == 1.0f);
    // Below the 0.04045 knee the curve is the linear segment.
    CHECK(std::fabs(srgb_byte_to_linear(10) - (10 / 255.0f) / 12.92f) < 1e-9f);
    // Above it, monotonic and bounded.
    CHECK(srgb_byte_to_linear(128) > 0.21f && srgb_byte_to_linear(128) < 0.22f);
    CHECK(srgb_byte_to_linear(200) > srgb_byte_to_linear(128));

    auto c = LinearColor::from_srgb(255, 255, 255, 1.0f);
    CHECK(c == LinearColor::white());
    auto pm = LinearColor(1, 1, 1, 0.5f).premultiplied();
    CHECK(pm.r == 0.5f && pm.a == 0.5f);
}
