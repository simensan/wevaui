#include "weva/geometry.h"

namespace weva {

Transform2D Transform2D::rotate(double degrees) {
    // C# computes the angle in double, then narrows cos/sin to float.
    // Doing the narrowing at the same point keeps the two engines bit-equal.
    double rad = degrees * 3.14159265358979323846 / 180.0;
    float cos_v = static_cast<float>(std::cos(rad));
    float sin_v = static_cast<float>(std::sin(rad));
    return Transform2D(cos_v, sin_v, -sin_v, cos_v, 0, 0);
}

Transform2D Transform2D::multiply(const Transform2D& o) const {
    float na  = a * o.a + b * o.c;
    float nb  = a * o.b + b * o.d;
    float nc  = c * o.a + d * o.c;
    float nd  = c * o.b + d * o.d;
    float ntx = tx * o.a + ty * o.c + o.tx;
    float nty = tx * o.b + ty * o.d + o.ty;
    return Transform2D(na, nb, nc, nd, ntx, nty);
}

} // namespace weva
