// Keep the allocation counter outside the general test runner. These budgets
// cover actual mesh building, including a positive control inside libweva.
#include "weva/tessellate.h"
#include "weva/paint.h"
#include "weva/glyph_atlas.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <new>
#include <string>

namespace {
bool counting = false;
size_t allocations = 0;
uint64_t geometry_hash = 1469598103934665603ULL;
void hash_bytes(const void* data, size_t size) {
    const auto* bytes = static_cast<const unsigned char*>(data);
    for (size_t i = 0; i < size; ++i) { geometry_hash ^= bytes[i]; geometry_hash *= 1099511628211ULL; }
}
bool validate(const weva::Mesh& mesh) {
    for (const auto& v : mesh.vertices) {
        const float fields[] = {v.position.x, v.position.y, v.tex_coord.x, v.tex_coord.y,
                                v.color.r, v.color.g, v.color.b, v.color.a};
        hash_bytes(fields, sizeof(fields));
    }
    for (const auto index : mesh.indices) {
        if (index >= mesh.vertices.size()) return false;
        hash_bytes(&index, sizeof(index));
    }
    return mesh.indices.size() % 3 == 0;
}
template<class F> size_t measure(F call) {
    allocations = 0;
    counting = true;
    call();
    counting = false;
    return allocations;
}
// Independent scalar reference for the pre-optimization AA ring construction.
using Point = std::pair<double, double>;
Point reference_offset(const std::vector<Point>& pts, size_t i, double d) {
    const size_t n = pts.size();
    size_t prev = i, next = i;
    for (size_t k = 1; k <= n; ++k) { prev = (i + n - k) % n; if (pts[prev] != pts[i]) break; }
    for (size_t k = 1; k <= n; ++k) { next = (i + k) % n; if (pts[next] != pts[i]) break; }
    const double ax = pts[i].first - pts[prev].first, ay = pts[i].second - pts[prev].second;
    const double bx = pts[next].first - pts[i].first, by = pts[next].second - pts[i].second;
    const double la = std::sqrt(ax*ax + ay*ay), lb = std::sqrt(bx*bx + by*by);
    if (la <= 0 || lb <= 0) return pts[i];
    const double n1x = ay/la, n1y = -ax/la, n2x = by/lb, n2y = -bx/lb;
    double mx = n1x+n2x, my = n1y+n2y;
    const double lm = std::sqrt(mx*mx + my*my);
    if (lm <= 1e-9) return pts[i];
    mx /= lm; my /= lm;
    const double c = mx*n1x + my*n1y;
    const double scale = c > .2 ? d/c : d*5;
    return {pts[i].first + mx*scale, pts[i].second + my*scale};
}
bool scalar_rounding_matches(const weva::Rect& r, const weva::BorderRadii& radii, int segments) {
    const auto c = weva::clamp_radii_to_rect(radii, r.width, r.height);
    std::vector<Point> pts;
    const double pi = 3.14159265358979323846;
    const auto arc = [&](double x, double y, const weva::CornerRadius& radius, double start) {
        if (radius.x_radius <= 0 || radius.y_radius <= 0) { pts.emplace_back(x,y); return; }
        for (int i=0; i<=segments; ++i) {
            const double t = start + (pi*.5)*(static_cast<double>(i)/segments);
            pts.emplace_back(x+radius.x_radius*std::cos(t), y+radius.y_radius*std::sin(t));
        }
    };
    arc(r.x+c.top_left.x_radius,r.y+c.top_left.y_radius,c.top_left,pi);
    arc(r.right()-c.top_right.x_radius,r.y+c.top_right.y_radius,c.top_right,-pi*.5);
    arc(r.right()-c.bottom_right.x_radius,r.bottom()-c.bottom_right.y_radius,c.bottom_right,0);
    arc(r.x+c.bottom_left.x_radius,r.bottom()-c.bottom_left.y_radius,c.bottom_left,pi*.5);
    weva::Mesh actual;
    weva::tessellate_rounded_rect(r,radii,weva::LinearColor::white(),&actual,segments,true);
    if (actual.vertices.size()!=1+2*pts.size()) return false;
    for (size_t i=0; i<pts.size(); ++i) for (int ring=0; ring<2; ++ring) {
        const auto expected=reference_offset(pts,i,ring ? .5 : -.5);
        const auto& v=actual.vertices[1+ring*pts.size()+i];
        if (v.position.x!=static_cast<float>(expected.first) || v.position.y!=static_cast<float>(expected.second) ||
            v.color.a!=(ring ? 0.f : 1.f)) return false;
    }
    return true;
}
}

void* operator new(size_t size) {
    if (counting) ++allocations;
    if (void* p = std::malloc(size ? size : 1)) return p;
    std::abort();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }

int main() {
    using namespace weva;
    int checks = 0, failures = 0;
    const auto check = [&](bool pass, const char* label, size_t count, size_t limit) {
        ++checks;
        if (!pass) { ++failures; std::printf("FAIL %s: %zu allocations, limit %zu\n", label, count, limit); }
    };
    const Rect rect(2.125, -3.75, 103.5, 41.25);
    const LinearColor colors[] = {{1,.5f,0,1}, {0,1,.2f,.8f}, {0,0,1,.4f}, {1,0,1,0}};
    for (int segments : {1, 4, 8, 31}) for (double radius : {.25, 8., 100.})
    for (double y : {-1000.125, 0., 97.375}) {
        auto corners = BorderRadii::uniform(radius);
        corners.top_right = {radius*.3, radius*.7};
        corners.bottom_left = {0,0};
        check(scalar_rounding_matches(Rect(2.125,y,103.5,41.25),corners,segments),
              "AA vertices exactly match scalar reference",0,0);
    }
    {
        Mesh border;
        const LinearColor uniform[4] = {colors[0],colors[0],colors[0],colors[0]};
        const size_t n = measure([&] {
            tessellate_border(rect,BorderRadii::uniform(8),1,1,1,1,uniform,&border);
        });
        check(n==4 && !border.empty(),"thin border owns only two outlines and two mesh buffers",n,4);
    }
    for (int segments : {1, 4, 8, 31}) {
        for (bool aa : {false, true}) {
            for (double radius : {0., .25, 8., 100.}) {
                Mesh fill;
                const size_t n = measure([&] { tessellate_rounded_rect(rect, BorderRadii::uniform(radius), colors[0], &fill, segments, aa); });
                check(validate(fill) && n > 0 && n <= 5 && !fill.empty(), "rounded fill", n, 5);
                for (double width : {.5, 2., 16.}) {
                    Mesh border;
                    const size_t b = measure([&] { tessellate_border(rect, BorderRadii::uniform(radius), width, width, width, width, colors, &border, segments, aa); });
                    if (b>10) std::printf("  border segments=%d aa=%d radius=%g width=%g\n",segments,aa,radius,width);
                    check(validate(border) && b <= 10 && !border.empty(), "border", b, 10);
                }
            }
        }
    }
    Mesh quad, joined;
    tessellate_rect(rect, colors[0], &quad);
    const size_t append = measure([&] { for (int i = 0; i < 500; ++i) joined.append(quad); });
    check(validate(joined) && append <= 50 && joined.vertices.size() == 2000 && joined.indices.size() == 3000,
          "500 appended quads grow geometrically", append, 50);

    StubFont font;
    const auto face = font.load_face({}, 0);
    GlyphAtlas atlas;
    PaintContext paint;
    paint.font = &font; paint.face = face; paint.atlas = &atlas;
    struct TextCase { std::string text; size_t geometry_allocations; };
    const TextCase text_cases[] = {
        {std::string(100, 'A'), 4}, {std::string(4096, 'A'), 30},
        {std::string(4096, ' '), 0}, {std::string("A") + std::string(4096, ' '), 4},
        {"Size check with spaces", 4},
    };
    for (const auto& sample : text_cases) {
        const auto& text = sample.text;
        Mesh warm;
        build_text_geometry(text, 1.5, 23.25, 16, colors[0], paint, &warm, .125);
        std::vector<ShapedGlyph> shaped;
        const size_t shape_count = measure([&] { font.shape(face, text, 16, &shaped); });
        Mesh text_mesh;
        const size_t n = measure([&] { build_text_geometry(text, 1.5, 23.25, 16, colors[0], paint, &text_mesh, .125); });
        const size_t budget = shape_count + sample.geometry_allocations;
        check(validate(text_mesh) && n <= budget && text_mesh.indices == warm.indices,
              "text mesh allocation budget", n, budget);
        check(text_mesh.vertices.size() == warm.vertices.size(), "text vertex count", n, budget);
        for (size_t i = 0; i < text_mesh.vertices.size() && i < warm.vertices.size(); ++i) {
            const auto& a = text_mesh.vertices[i]; const auto& b = warm.vertices[i];
            if (a.position.x != b.position.x || a.position.y != b.position.y || a.tex_coord.x != b.tex_coord.x ||
                a.tex_coord.y != b.tex_coord.y || a.color != b.color) { ++failures; break; }
        }
    }
    std::printf("geometry allocations: %d checks, %d failures; geometry hash %016llx\n", checks, failures,
                static_cast<unsigned long long>(geometry_hash));
    return failures ? 1 : 0;
}
