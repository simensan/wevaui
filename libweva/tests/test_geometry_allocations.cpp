// Keep the allocation counter outside the general test runner. These budgets
// cover actual mesh building, including a positive control inside libweva.
#include "weva/tessellate.h"
#include "weva/paint.h"
#include "weva/glyph_atlas.h"
#include "weva/software_renderer.h"
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
Point reference_displacement(const std::vector<Point>& pts, size_t i, double d) {
    const size_t n = pts.size();
    size_t prev = i, next = i;
    for (size_t k = 1; k <= n; ++k) { prev = (i + n - k) % n; if (pts[prev] != pts[i]) break; }
    for (size_t k = 1; k <= n; ++k) { next = (i + k) % n; if (pts[next] != pts[i]) break; }
    const double ax = pts[i].first - pts[prev].first, ay = pts[i].second - pts[prev].second;
    const double bx = pts[next].first - pts[i].first, by = pts[next].second - pts[i].second;
    const double la = std::sqrt(ax*ax + ay*ay), lb = std::sqrt(bx*bx + by*by);
    if (la <= 0 || lb <= 0) return {0,0};
    const double n1x = ay/la, n1y = -ax/la, n2x = by/lb, n2y = -bx/lb;
    double mx = n1x+n2x, my = n1y+n2y;
    const double lm = std::sqrt(mx*mx + my*my);
    if (lm <= 1e-9) return {0,0};
    mx /= lm; my /= lm;
    const double c = mx*n1x + my*n1y;
    const double scale = c > .2 ? d/c : d*5;
    return {mx*scale, my*scale};
}
Point reference_offset(const std::vector<Point>& pts, size_t i, double d) {
    const auto delta = reference_displacement(pts,i,d);
    return {pts[i].first+delta.first,pts[i].second+delta.second};
}
std::vector<Point> reference_outline(const weva::Rect& r, const weva::BorderRadii& radii, int segments, bool uniform=false) {
    const auto c = weva::clamp_radii_to_rect(radii, r.width, r.height);
    std::vector<Point> pts;
    const double pi = 3.14159265358979323846;
    const auto arc = [&](double x, double y, const weva::CornerRadius& radius, double start) {
        if (radius.x_radius <= 0 || radius.y_radius <= 0) { for (int i=0; i<(uniform ? segments+1 : 1); ++i) pts.emplace_back(x,y); return; }
        for (int i=0; i<=segments; ++i) {
            const double t = start + (pi*.5)*(static_cast<double>(i)/segments);
            pts.emplace_back(x+radius.x_radius*std::cos(t), y+radius.y_radius*std::sin(t));
        }
    };
    arc(r.x+c.top_left.x_radius,r.y+c.top_left.y_radius,c.top_left,pi);
    arc(r.right()-c.top_right.x_radius,r.y+c.top_right.y_radius,c.top_right,-pi*.5);
    arc(r.right()-c.bottom_right.x_radius,r.bottom()-c.bottom_right.y_radius,c.bottom_right,0);
    arc(r.x+c.bottom_left.x_radius,r.bottom()-c.bottom_left.y_radius,c.bottom_left,pi*.5);
    return pts;
}
bool scalar_rounding_matches(const weva::Rect& r, const weva::BorderRadii& radii, int segments) {
    const auto pts = reference_outline(r,radii,segments);
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
bool scalar_thin_border_matches(const weva::Rect& r, const weva::BorderRadii& radii, int segments, double width) {
    const weva::Rect inner(r.x+width,r.y+width,std::max(0.0,r.width-2*width),std::max(0.0,r.height-2*width));
    const auto inside=weva::inset_radii(weva::clamp_radii_to_rect(radii,r.width,r.height),width,width,width,width);
    auto outer_points=reference_outline(r,radii,segments);
    auto inner_points=reference_outline(inner,inside,segments);
    if (outer_points.size()!=inner_points.size()) {
        outer_points=reference_outline(r,radii,segments,true);
        inner_points=reference_outline(inner,inside,segments,true);
    }
    weva::Mesh actual;
    actual.vertices.resize(3);
    actual.indices = {2,1,0};
    const weva::LinearColor colors[4]={{.2f,.4f,.6f,.7f},{.2f,.4f,.6f,.7f},{.2f,.4f,.6f,.7f},{.2f,.4f,.6f,.7f}};
    weva::tessellate_border(r,radii,width,width,width,width,colors,&actual,segments,true);
    if (actual.vertices.size()!=3+4*outer_points.size() || actual.indices.size()!=3+18*outer_points.size()) return false;
    if (actual.indices[0]!=2 || actual.indices[1]!=1 || actual.indices[2]!=0) return false;
    for (size_t i=0;i<outer_points.size();++i) {
        const auto od=reference_displacement(outer_points,i,1);
        const auto id=reference_displacement(inner_points,i,1);
        for (int ring=0;ring<4;++ring) {
            const auto& p=ring<2 ? outer_points[i] : inner_points[i];
            const auto& delta=ring<2 ? od : id;
            const double distance=ring==0 ? .5 : ring==1 ? .5-width : ring==2 ? width-.5 : -.5;
            const auto& v=actual.vertices[3+4*i+ring];
            if (v.position.x!=static_cast<float>(p.first+delta.first*distance) ||
                v.position.y!=static_cast<float>(p.second+delta.second*distance) ||
                v.color.a!=((ring==0 || ring==3) ? 0.f : .7f*static_cast<float>(width))) return false;
        }
        const size_t next=(i+1)%outer_points.size();
        for (uint32_t strip=0;strip<3;++strip) {
            const uint32_t a=static_cast<uint32_t>(3+4*i+strip), b=static_cast<uint32_t>(3+4*next+strip);
            const uint32_t expected[6]={a,b,a+1,a+1,b,b+1};
            for (size_t j=0;j<6;++j) if (actual.indices[3+18*i+6*strip+j]!=expected[j]) return false;
        }
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
    // Full and collapsed inner corners, repeated points at square corners,
    // and a closing-edge run must retain the former scalar geometry exactly.
    for (int segments : {1, 4, 8, 31}) for (double width : {.25, .5, 1.})
    for (double radius : {.125, .5, 1., 8., 100.}) for (bool mixed : {false,true}) {
        auto radii=BorderRadii::uniform(radius);
        if (mixed) { radii.top_left={0,0}; radii.bottom_right={radius*.3,radius*.7}; }
        check(scalar_thin_border_matches(rect,radii,segments,width),
              "thin border matches independent scalar geometry",0,0);
    }
    for (const Rect small : {Rect(0,0,2,2),Rect(-.25,.125,1.5,2),Rect(.125,-.25,2,1.5)})
    for (double width : {.75,1.}) for (double radius : {1.,100.}) for (int segments : {1,8}) {
        check(scalar_thin_border_matches(small,BorderRadii::uniform(radius),segments,width),
              "collapsed inner outline matches scalar geometry",0,0);
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
    // Preparation and painting both shape these labels. Only the preparation
    // phase can share storage: a fresh output allocation for every label would
    // double the allocation budget without changing the resulting glyphs.
    struct MeasuredFont : StubFont {
        size_t shape_allocations = 0;
        void shape(FaceHandle f, std::string_view text, double px,
                   std::vector<ShapedGlyph>* out) override {
            shape_allocations += measure([&] { StubFont::shape(f, text, px, out); });
        }
    } measured_font;
    std::vector<ShapedGlyph> probe;
    const std::string label = "A preparation buffer";
    measured_font.shape(face, label, 16, &probe);
    const size_t cold_shape = measured_font.shape_allocations;
    check(cold_shape > 0, "shape allocation positive control", cold_shape, 1);
    measured_font.shape_allocations = 0;
    BoxTree text_tree;
    ComputedStyle text_style;
    text_style.set("color", "red");
    const BoxId text_root = text_tree.create(BoxKind::Block, nullptr, nullptr);
    text_tree[text_root].width = text_tree[text_root].height = 100;
    constexpr int labels = 100;
    for (int i = 0; i < labels; ++i) {
        const BoxId child = text_tree.create(BoxKind::Text, nullptr, &text_style);
        text_tree.append_child(text_root, child);
        text_tree[child].text = label;
        text_tree[child].font_size = 16;
        text_tree[child].width = 100;
        text_tree[child].height = 16;
    }
    GlyphAtlas preparation_atlas;
    SoftwareRenderer preparation_renderer(100, 20);
    PaintContext preparation_paint;
    preparation_paint.backend = &preparation_renderer;
    preparation_paint.font = &measured_font;
    preparation_paint.atlas = &preparation_atlas;
    preparation_paint.face = face;
    paint_tree(text_tree, text_root, LayoutContext{}, preparation_paint);
    const size_t preparation_budget = cold_shape * (labels + 1);
    check(measured_font.shape_allocations <= preparation_budget,
          "100 labels share preparation storage", measured_font.shape_allocations, preparation_budget);
    std::printf("100-label shaping: %zu allocations; budget %zu\n",
                measured_font.shape_allocations, preparation_budget);
    std::printf("geometry allocations: %d checks, %d failures; geometry hash %016llx\n", checks, failures,
                static_cast<unsigned long long>(geometry_hash));
    return failures ? 1 : 0;
}
