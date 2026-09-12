#pragma once
#include <cmath>

// Ports Runtime/Paint/{Rect,Transform2D,BorderRadii}.cs.
//
// Field widths are copied deliberately, not normalised: Rect is double,
// Transform2D's matrix is float with a double Apply(). The oracle compares
// geometry against the C# engine, so "tidying" a float to a double here
// shows up later as an unexplained sub-pixel divergence.

namespace weva {

struct Rect {
    double x = 0, y = 0, width = 0, height = 0;

    constexpr Rect() = default;
    constexpr Rect(double x_, double y_, double w_, double h_)
        : x(x_), y(y_), width(w_), height(h_) {}

    constexpr double right() const { return x + width; }
    constexpr double bottom() const { return y + height; }
    constexpr bool is_empty() const { return width <= 0 || height <= 0; }

    static constexpr Rect empty() { return Rect(); }

    // Half-open on the far edges, matching C# Rect.Contains.
    constexpr bool contains(double px, double py) const {
        return px >= x && px < right() && py >= y && py < bottom();
    }

    Rect intersect(const Rect& o) const {
        double x1 = std::fmax(x, o.x);
        double y1 = std::fmax(y, o.y);
        double x2 = std::fmin(right(), o.right());
        double y2 = std::fmin(bottom(), o.bottom());
        if (x2 <= x1 || y2 <= y1) return empty();
        return Rect(x1, y1, x2 - x1, y2 - y1);
    }

    friend constexpr bool operator==(const Rect& a, const Rect& b) {
        return a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
    }
    friend constexpr bool operator!=(const Rect& a, const Rect& b) { return !(a == b); }
};

// Row-major affine:  [A B 0; C D 0; Tx Ty 1]
//   x' = A*x + C*y + Tx
//   y' = B*x + D*y + Ty
struct Transform2D {
    float a = 1, b = 0, c = 0, d = 1, tx = 0, ty = 0;

    constexpr Transform2D() = default;
    constexpr Transform2D(float a_, float b_, float c_, float d_, float tx_, float ty_)
        : a(a_), b(b_), c(c_), d(d_), tx(tx_), ty(ty_) {}

    static constexpr Transform2D identity() { return Transform2D(); }
    static constexpr Transform2D translate(float x, float y) { return Transform2D(1, 0, 0, 1, x, y); }
    static constexpr Transform2D scale(float sx, float sy) { return Transform2D(sx, 0, 0, sy, 0, 0); }
    static Transform2D rotate(double degrees);

    // result = this * other — apply this first, then other.
    Transform2D multiply(const Transform2D& o) const;

    void apply(double x, double y, double* out_x, double* out_y) const {
        *out_x = a * x + c * y + tx;
        *out_y = b * x + d * y + ty;
    }

    friend constexpr bool operator==(const Transform2D& l, const Transform2D& r) {
        return l.a == r.a && l.b == r.b && l.c == r.c && l.d == r.d && l.tx == r.tx && l.ty == r.ty;
    }
    friend constexpr bool operator!=(const Transform2D& l, const Transform2D& r) { return !(l == r); }
};

struct CornerRadius {
    double x_radius = 0, y_radius = 0;

    constexpr CornerRadius() = default;
    constexpr explicit CornerRadius(double r) : x_radius(r), y_radius(r) {}
    constexpr CornerRadius(double xr, double yr) : x_radius(xr), y_radius(yr) {}

    constexpr bool is_zero() const { return x_radius <= 0 && y_radius <= 0; }

    friend constexpr bool operator==(const CornerRadius& a, const CornerRadius& b) {
        return a.x_radius == b.x_radius && a.y_radius == b.y_radius;
    }
    friend constexpr bool operator!=(const CornerRadius& a, const CornerRadius& b) { return !(a == b); }
};

struct BorderRadii {
    CornerRadius top_left, top_right, bottom_right, bottom_left;

    constexpr BorderRadii() = default;
    constexpr BorderRadii(CornerRadius tl, CornerRadius tr, CornerRadius br, CornerRadius bl)
        : top_left(tl), top_right(tr), bottom_right(br), bottom_left(bl) {}

    static constexpr BorderRadii zero() { return BorderRadii(); }
    static constexpr BorderRadii uniform(double r) {
        return BorderRadii(CornerRadius(r), CornerRadius(r), CornerRadius(r), CornerRadius(r));
    }

    constexpr bool is_zero() const {
        return top_left.is_zero() && top_right.is_zero()
            && bottom_right.is_zero() && bottom_left.is_zero();
    }

    friend constexpr bool operator==(const BorderRadii& a, const BorderRadii& b) {
        return a.top_left == b.top_left && a.top_right == b.top_right
            && a.bottom_right == b.bottom_right && a.bottom_left == b.bottom_left;
    }
    friend constexpr bool operator!=(const BorderRadii& a, const BorderRadii& b) { return !(a == b); }
};

} // namespace weva
