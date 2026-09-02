#include "weva/paint.h"

#include "weva/background.h"
#include "weva/block_layout.h"
#include "weva/css_value.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <cmath>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace weva {

// A clip in force for a subtree: `clip-path`, or the rounded padding box of an
// `overflow: hidden` box. Chained through the parent so nested clips all
// apply; shared by the PaintState copies below rather than copied per box.
// Polygons are in SCREEN coordinates — mapped through the transform in force
// where the clip was pushed — because a descendant may add a transform of
// its own: a road rotated across a round map is clipped where it lands, not
// where it was laid out.
struct ClipNode {
    std::vector<ClipPoint> polygon;
    std::shared_ptr<const ClipNode> parent;
};

// The colour half of `filter` (Filter Effects L1 §8): brightness, contrast,
// grayscale, sepia, saturate, invert and opacity compose into one affine
// colour transform, applied in sRGB to every vertex colour and generated
// texel painted under the box. Nested filters compose parent-after-child.
// blur() and drop-shadow() are handled separately (a padded rasterize; an
// outer shadow); url() filters are not applied.
struct ColorFilter {
    float m[3][3] = {{1, 0, 0}, {0, 1, 0}, {0, 0, 1}};
    float add[3] = {0, 0, 0};
    float alpha = 1;

    // this = other ∘ this (apply this first, then other).
    void then(const ColorFilter& o) {
        float nm[3][3];
        float na[3];
        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) {
                nm[r][c] = o.m[r][0] * m[0][c] + o.m[r][1] * m[1][c] + o.m[r][2] * m[2][c];
            }
            na[r] = o.m[r][0] * add[0] + o.m[r][1] * add[1] + o.m[r][2] * add[2] + o.add[r];
        }
        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) m[r][c] = nm[r][c];
            add[r] = na[r];
        }
        alpha *= o.alpha;
    }
    void apply_srgb(float* r, float* g, float* b, float* a) const {
        const float in[3] = {*r, *g, *b};
        float out[3];
        for (int i = 0; i < 3; ++i) {
            out[i] = m[i][0] * in[0] + m[i][1] * in[1] + m[i][2] * in[2] + add[i];
            out[i] = std::min(1.f, std::max(0.f, out[i]));
        }
        *r = out[0];
        *g = out[1];
        *b = out[2];
        *a *= alpha;
    }
};

LinearColor resolve_color(const ComputedStyle* style, std::string_view property);

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

double radius_component(std::string_view raw, const LayoutContext& ctx, double font_size,
                        double basis) {
    if (raw.empty()) return 0;
    const ResolvedLength r = resolve_length(raw, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return std::max(0.0, r.pixels);
    if (r.kind == LengthKind::Percent) return std::max(0.0, basis * r.percent * 0.01);
    return 0;
}

// A corner radius may be one value (circular) or two (elliptical), and the two
// axes resolve against different basis lengths.
CornerRadius corner(const ComputedStyle* style, std::string_view property,
                    const LayoutContext& ctx, double font_size, double width, double height) {
    const std::string_view raw = get(style, property);
    if (raw.empty()) return {};
    // Split on the first top-level space; a calc() keeps its own spaces inside
    // parentheses.
    size_t split = std::string_view::npos;
    int depth = 0;
    for (size_t i = 0; i < raw.size(); ++i) {
        if (raw[i] == '(') ++depth;
        else if (raw[i] == ')') --depth;
        else if (depth == 0 && raw[i] == ' ') { split = i; break; }
    }
    if (split == std::string_view::npos) {
        const double v = radius_component(raw, ctx, font_size, width);
        // A single value is circular, but the two axes still resolve against
        // different bases when it is a percentage.
        return CornerRadius(v, radius_component(raw, ctx, font_size, height));
    }
    return CornerRadius(radius_component(raw.substr(0, split), ctx, font_size, width),
                        radius_component(raw.substr(split + 1), ctx, font_size, height));
}

// `letter-spacing` in px: a length, or a percentage of the font size (the
// reading inline layout takes, css-text-4).
double letter_spacing_of(const ComputedStyle* style, const LayoutContext& ctx, double font_size) {
    const std::string_view raw = get(style, "letter-spacing");
    if (raw.empty() || raw == "normal") return 0;
    const ResolvedLength r = resolve_length(style, "letter-spacing", ctx, font_size, std::nullopt);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return font_size * r.percent * 0.01;
    return 0;
}

std::vector<std::string_view> split_shadow_list(std::string_view s, char sep);

// `opacity` scales every vertex alpha: the group is not composited through a
// layer, so overlapping children of a translucent box double up where they
// overlap. A layer per opacity group is the later, exact form.
float srgb_to_linear_f(float v);
float linear_to_srgb_f(float v) {
    if (v <= 0) return 0;
    if (v >= 1) return 1;
    return v <= 0.0031308f ? v * 12.92f : 1.055f * std::pow(v, 1.f / 2.4f) - 0.055f;
}

void filter_vertices(std::vector<Vertex>* vertices, const ColorFilter& f) {
    for (Vertex& v : *vertices) {
        float r = linear_to_srgb_f(v.color.r), g = linear_to_srgb_f(v.color.g),
              b = linear_to_srgb_f(v.color.b), a = v.color.a;
        f.apply_srgb(&r, &g, &b, &a);
        v.color = LinearColor(srgb_to_linear_f(r), srgb_to_linear_f(g), srgb_to_linear_f(b), a);
    }
}

// Straight-alpha sRGB8 texels, in place (generated backgrounds).
void filter_rgba(std::vector<uint8_t>* rgba, const ColorFilter& f) {
    for (size_t i = 0; i + 3 < rgba->size(); i += 4) {
        float r = (*rgba)[i] / 255.f, g = (*rgba)[i + 1] / 255.f, b = (*rgba)[i + 2] / 255.f,
              a = (*rgba)[i + 3] / 255.f;
        f.apply_srgb(&r, &g, &b, &a);
        (*rgba)[i] = static_cast<uint8_t>(std::lround(r * 255));
        (*rgba)[i + 1] = static_cast<uint8_t>(std::lround(g * 255));
        (*rgba)[i + 2] = static_cast<uint8_t>(std::lround(b * 255));
        (*rgba)[i + 3] = static_cast<uint8_t>(std::lround(a * 255));
    }
}

void draw_mesh(const Mesh& source, RenderInterface* backend, TextureHandle tex, double opacity = 1,
               const Transform2D* xform = nullptr, const ClipNode* clip = nullptr,
               const ColorFilter* filter = nullptr) {
    if (source.empty()) return;
    // A colour filter rewrites the vertex colours: the whole story for solid
    // geometry and coverage text; textured draws had their texels filtered
    // where they were generated (see filter_rgba), and keep white vertices.
    Mesh filtered;
    const Mesh* in = &source;
    if (filter) {
        filtered = source;
        filter_vertices(&filtered.vertices, *filter);
        in = &filtered;
    }
    const Mesh& input = *in;
    if (clip) {
        // Into screen space first, then geometric clipping, innermost clip
        // outwards; opacity last. The transformed path below is folded in
        // here so the polygons and the vertices meet in one space.
        Mesh cur = input;
        if (xform) {
            for (Vertex& v : cur.vertices) {
                double x = 0, y = 0;
                xform->apply(v.position.x, v.position.y, &x, &y);
                v.position = {static_cast<float>(x), static_cast<float>(y)};
            }
        }
        for (const ClipNode* n = clip; n; n = n->parent.get()) {
            Mesh tmp;
            clip_triangles_polygon(cur.vertices, cur.indices, n->polygon, &tmp);
            cur = std::move(tmp);
            if (cur.empty()) return;
        }
        if (opacity < 1) {
            for (Vertex& v : cur.vertices) v.color.a *= static_cast<float>(std::max(0.0, opacity));
        }
        const GeometryHandle g = backend->compile_geometry(cur.vertices, cur.indices);
        backend->render_geometry(g, {0, 0}, tex);
        backend->release_geometry(g);
        return;
    }
    const Mesh& mesh = input;
    // Compiled and released per draw for now. A backend that batches will want
    // geometry to outlive a frame; that needs the paint cache, keyed on style
    // and layout versions, which is a later slice.
    if (opacity < 1 || xform) {
        Mesh copy = mesh;
        for (Vertex& v : copy.vertices) {
            if (opacity < 1) v.color.a *= static_cast<float>(std::max(0.0, opacity));
            if (xform) {
                double x = 0, y = 0;
                xform->apply(v.position.x, v.position.y, &x, &y);
                v.position = {static_cast<float>(x), static_cast<float>(y)};
            }
        }
        const GeometryHandle g = backend->compile_geometry(copy.vertices, copy.indices);
        backend->render_geometry(g, {0, 0}, tex);
        backend->release_geometry(g);
        return;
    }
    const GeometryHandle g = backend->compile_geometry(mesh.vertices, mesh.indices);
    backend->render_geometry(g, {0, 0}, tex);
    backend->release_geometry(g);
}

// ---- transform (CSS Transforms L1) ---------------------------------------

// `<transform-list>` applied about `transform-origin`, as a matrix in the
// coordinates of the box's border box; identity when there is none. skew and
// matrix() are read; 3D functions are ignored.
bool parse_transform(const ComputedStyle* style, const LayoutContext& ctx, double font_size,
                     double width, double height, Transform2D* out) {
    const std::string_view raw = get(style, "transform");
    if (raw.empty() || raw == "none") return false;
    struct Fn { std::string name; std::vector<std::string_view> args; };
    std::vector<Fn> fns;
    size_t cursor = 0;
    while (cursor < raw.size()) {
        const size_t open = raw.find('(', cursor);
        if (open == std::string_view::npos) break;
        size_t close = std::string_view::npos;
        int depth = 0;
        for (size_t i = open; i < raw.size(); ++i) {
            if (raw[i] == '(') ++depth;
            else if (raw[i] == ')' && --depth == 0) { close = i; break; }
        }
        if (close == std::string_view::npos) break;
        Fn f;
        std::string_view name = raw.substr(cursor, open - cursor);
        while (!name.empty() && (name.front() == ' ' || name.front() == ',')) name.remove_prefix(1);
        while (!name.empty() && name.back() == ' ') name.remove_suffix(1);
        f.name.assign(name);
        for (char& c : f.name) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c);
        f.args = split_shadow_list(raw.substr(open + 1, close - open - 1), ',');
        for (std::string_view& a : f.args) {
            while (!a.empty() && a.front() == ' ') a.remove_prefix(1);
            while (!a.empty() && a.back() == ' ') a.remove_suffix(1);
        }
        fns.push_back(std::move(f));
        cursor = close + 1;
    }
    if (fns.empty()) return false;
    const auto length = [&](std::string_view s, double basis) {
        const ResolvedLength r = resolve_length(s, ctx, font_size, basis);
        if (r.kind == LengthKind::Length) return r.pixels;
        if (r.kind == LengthKind::Percent) return basis * r.percent * 0.01;
        return 0.0;
    };
    const auto number = [](std::string_view s, double fallback) {
        const std::string t(s);
        char* end = nullptr;
        const double v = std::strtod(t.c_str(), &end);
        return end == t.c_str() ? fallback : v;
    };
    const auto angle = [&](std::string_view s) {
        const std::string t(s);
        char* end = nullptr;
        const double v = std::strtod(t.c_str(), &end);
        if (end == t.c_str()) return 0.0;
        const std::string_view unit(end);
        if (unit == "rad") return v * 180.0 / 3.14159265358979323846;
        if (unit == "grad") return v * 0.9;
        if (unit == "turn") return v * 360.0;
        return v;
    };
    // CSS applies the list right to left to a point: the last function
    // first. multiply() applies its receiver first, so fold from the end.
    Transform2D m = Transform2D::identity();
    for (size_t i = fns.size(); i-- > 0;) {
        const Fn& f = fns[i];
        Transform2D t = Transform2D::identity();
        const std::string& n = f.name;
        const size_t argc = f.args.size();
        if (n == "translate" && argc >= 1) {
            t = Transform2D::translate(static_cast<float>(length(f.args[0], width)),
                                       static_cast<float>(argc > 1 ? length(f.args[1], height) : 0.0));
        } else if (n == "translatex" && argc >= 1) {
            t = Transform2D::translate(static_cast<float>(length(f.args[0], width)), 0);
        } else if (n == "translatey" && argc >= 1) {
            t = Transform2D::translate(0, static_cast<float>(length(f.args[0], height)));
        } else if (n == "scale" && argc >= 1) {
            const double sx = number(f.args[0], 1);
            t = Transform2D::scale(static_cast<float>(sx),
                                   static_cast<float>(argc > 1 ? number(f.args[1], 1) : sx));
        } else if (n == "scalex" && argc >= 1) {
            t = Transform2D::scale(static_cast<float>(number(f.args[0], 1)), 1);
        } else if (n == "scaley" && argc >= 1) {
            t = Transform2D::scale(1, static_cast<float>(number(f.args[0], 1)));
        } else if (n == "rotate" && argc >= 1) {
            t = Transform2D::rotate(angle(f.args[0]));
        } else if (n == "skewx" && argc >= 1) {
            t = Transform2D(1, 0, static_cast<float>(std::tan(angle(f.args[0]) * 3.14159265358979323846 / 180)), 1, 0, 0);
        } else if (n == "skewy" && argc >= 1) {
            t = Transform2D(1, static_cast<float>(std::tan(angle(f.args[0]) * 3.14159265358979323846 / 180)), 0, 1, 0, 0);
        } else if (n == "skew" && argc >= 1) {
            const double ax = angle(f.args[0]), ay = argc > 1 ? angle(f.args[1]) : 0;
            t = Transform2D(1, static_cast<float>(std::tan(ay * 3.14159265358979323846 / 180)),
                            static_cast<float>(std::tan(ax * 3.14159265358979323846 / 180)), 1, 0, 0);
        } else if (n == "matrix" && argc >= 6) {
            t = Transform2D(static_cast<float>(number(f.args[0], 1)), static_cast<float>(number(f.args[1], 0)),
                            static_cast<float>(number(f.args[2], 0)), static_cast<float>(number(f.args[3], 1)),
                            static_cast<float>(number(f.args[4], 0)), static_cast<float>(number(f.args[5], 0)));
        } else {
            continue;   // a 3D or unknown function: no effect
        }
        m = m.multiply(t);
    }
    // transform-origin, default 50% 50%; keywords and one-value forms.
    double ox = width * 0.5, oy = height * 0.5;
    const std::string_view origin_raw = get(style, "transform-origin");
    if (!origin_raw.empty()) {
        std::vector<std::string_view> toks = split_shadow_list(origin_raw, ' ');
        const auto axis = [&](std::string_view s, double extent, bool horizontal) {
            if (s == "center") return extent * 0.5;
            if (s == (horizontal ? "left" : "top")) return 0.0;
            if (s == (horizontal ? "right" : "bottom")) return extent;
            return length(s, extent);
        };
        if (!toks.empty()) {
            std::string_view a = toks[0], b = toks.size() > 1 ? toks[1] : std::string_view("center");
            if (a == "top" || a == "bottom" || b == "left" || b == "right") std::swap(a, b);
            ox = axis(a, width, true);
            oy = axis(b, height, false);
        }
    }
    *out = Transform2D::translate(static_cast<float>(-ox), static_cast<float>(-oy))
               .multiply(m)
               .multiply(Transform2D::translate(static_cast<float>(ox), static_cast<float>(oy)));
    return true;
}

// ---- text-shadow (CSS Text Decoration L3 §4) ------------------------------

struct TextShadow {
    double x = 0, y = 0, blur = 0;
    LinearColor color;
};

std::vector<TextShadow> parse_text_shadows(const ComputedStyle* style, const LayoutContext& ctx,
                                           double font_size, const LinearColor& current) {
    std::vector<TextShadow> out;
    const std::string_view raw = get(style, "text-shadow");
    if (raw.empty() || raw == "none") return out;
    for (std::string_view layer : split_shadow_list(raw, ',')) {
        TextShadow sh;
        sh.color = current;
        std::vector<double> lengths;
        for (std::string_view tok : split_shadow_list(layer, ' ')) {
            const ResolvedLength r = resolve_length(tok, ctx, font_size, std::nullopt);
            if (r.kind == LengthKind::Length) { lengths.push_back(r.pixels); continue; }
            if (tok == "currentcolor" || tok == "currentColor") continue;
            CssParseError err;
            CssValuePtr v = parse_css_value(tok, &err);
            if (v && v->kind() == CssValueKind::Color) {
                const auto& c = static_cast<const CssColor&>(*v);
                sh.color = LinearColor::from_srgb(c.r, c.g, c.b, c.a);
                continue;
            }
            return {};
        }
        if (lengths.size() < 2) return {};
        sh.x = lengths[0];
        sh.y = lengths[1];
        if (lengths.size() > 2) sh.blur = std::max(0.0, lengths[2]);
        if (sh.color.a > 0) out.push_back(sh);
    }
    return out;
}

float srgb_to_linear_f(float v) {
    if (v <= 0) return 0;
    if (v >= 1) return 1;
    return v <= 0.04045f ? v / 12.92f : std::pow((v + 0.055f) / 1.055f, 2.4f);
}

bool clips_background_to_text(const ComputedStyle* style) {
    return get(style, "-webkit-background-clip") == "text" || get(style, "background-clip") == "text";
}

// `filter: blur(<length>)` — the one filter painted; the rest pass through.
double blur_filter_radius(const ComputedStyle* style, const LayoutContext& ctx, double font_size) {
    const std::string_view raw = get(style, "filter");
    const size_t at = raw.find("blur(");
    if (at == std::string_view::npos) return 0;
    const size_t close = raw.find(')', at);
    if (close == std::string_view::npos) return 0;
    std::string_view arg = raw.substr(at + 5, close - at - 5);
    while (!arg.empty() && arg.front() == ' ') arg.remove_prefix(1);
    const ResolvedLength r = resolve_length(arg, ctx, font_size, std::nullopt);
    return r.kind == LengthKind::Length ? std::max(0.0, r.pixels) : 0;
}

// ---- box-shadow (CSS Backgrounds L3 §7.1) --------------------------------

struct Shadow {
    double x = 0, y = 0, blur = 0, spread = 0;
    LinearColor color;
    bool inset = false;
};

std::vector<std::string_view> split_shadow_list(std::string_view s, char sep) {
    std::vector<std::string_view> out;
    int depth = 0;
    size_t start = 0;
    for (size_t i = 0; i < s.size(); ++i) {
        const char c = s[i];
        if (c == '(') ++depth;
        else if (c == ')') { if (depth > 0) --depth; }
        else if (depth == 0 && (sep == ' ' ? (c == ' ' || c == '\t' || c == '\n') : c == sep)) {
            if (i > start) out.push_back(s.substr(start, i - start));
            start = i + 1;
        }
    }
    if (start < s.size()) out.push_back(s.substr(start));
    return out;
}

// `[inset] <x> <y> [<blur> [<spread>]] [<color>]`, colour anywhere; a missing
// colour is currentcolor. Anything unreadable drops the whole list, as the
// cascade would.
std::vector<Shadow> parse_box_shadows(const ComputedStyle* style, const LayoutContext& ctx,
                                      double font_size, const LinearColor& current) {
    std::vector<Shadow> out;
    const std::string_view raw = get(style, "box-shadow");
    if (raw.empty() || raw == "none") return out;
    for (std::string_view layer : split_shadow_list(raw, ',')) {
        Shadow sh;
        sh.color = current;
        bool has_color = false;
        std::vector<double> lengths;
        for (std::string_view tok : split_shadow_list(layer, ' ')) {
            if (tok == "inset") { sh.inset = true; continue; }
            const ResolvedLength r = resolve_length(tok, ctx, font_size, std::nullopt);
            if (r.kind == LengthKind::Length) { lengths.push_back(r.pixels); continue; }
            if (tok == "currentcolor" || tok == "currentColor") { has_color = true; continue; }
            CssParseError err;
            CssValuePtr v = parse_css_value(tok, &err);
            if (v && v->kind() == CssValueKind::Color) {
                const auto& c = static_cast<const CssColor&>(*v);
                sh.color = LinearColor::from_srgb(c.r, c.g, c.b, c.a);
                has_color = true;
                continue;
            }
            return {};
        }
        (void)has_color;
        if (lengths.size() < 2) return {};
        sh.x = lengths[0];
        sh.y = lengths[1];
        if (lengths.size() > 2) sh.blur = std::max(0.0, lengths[2]);
        if (lengths.size() > 3) sh.spread = lengths[3];
        if (sh.color.a > 0) out.push_back(sh);
    }
    return out;
}

BorderRadii grow_radii(const BorderRadii& r, double d) {
    const auto g = [d](const CornerRadius& c) {
        return CornerRadius(std::max(0.0, c.x_radius + d), std::max(0.0, c.y_radius + d));
    };
    return BorderRadii(g(r.top_left), g(r.top_right), g(r.bottom_right), g(r.bottom_left));
}

// Coverage of a Gaussian-blurred straight edge at signed distance `e` outside
// it, for the blur's sigma (blur radius / 2, the convention Blink uses).
double blurred_coverage(double e, double sigma) {
    // `e <= 0`, not `e < 0`. With no blur the layer loop evaluates this at
    // exactly e == 0 — the hard edge itself — and a strict `<` called it
    // uncovered, so `target` came out 0, the layer failed the
    // `target <= accumulated` test, and a spread-only shadow like
    // `0 0 0 20px rgba(0,0,0,0.55)` drew NOTHING. Chrome draws a hard ring
    // there: 115/255 against white, which is exactly 0.55 of black over it.
    if (sigma <= 0) return e <= 0 ? 1.0 : 0.0;
    return 0.5 * std::erfc(e / (sigma * 1.4142135623730951));
}

// The blur is approximated with K nested shapes drawn outermost first, each
// with the alpha that brings the accumulated coverage to the Gaussian's value
// at its edge. No blur pass exists in the canvas, so K sets how smooth the
// falloff reads: one layer per ~2px of blur, since a step wider than that
// shows as a visible band. A fixed K = 12 was smooth at the 6-24px radii most
// samples use, but quests' `0 34px 90px` shadow banded into 12 grey rings.
// Clamped so a small shadow stays cheap and a huge one stays bounded.
int shadow_layers(double blur) {
    if (blur <= 0) return 1;
    const int k = static_cast<int>(std::lround(blur * 0.5));
    return std::min(48, std::max(12, k));
}

void paint_outer_shadows(const std::vector<Shadow>& shadows, const Rect& border_box,
                         const BorderRadii& radii, RenderInterface* backend, double opacity,
                         const Transform2D* xf = nullptr, const ClipNode* clip = nullptr,
                         const ColorFilter* filter = nullptr) {
    for (size_t s = shadows.size(); s-- > 0;) {   // first shadow on top
        const Shadow& sh = shadows[s];
        if (sh.inset) continue;
        const double sigma = sh.blur * 0.5;
        const int layers = shadow_layers(sh.blur);
        double accumulated = 0;
        for (int k = 0; k < layers; ++k) {
            // Extents run from +blur (nothing) to -blur (fully covered).
            const double e = sh.blur > 0 ? sh.blur * (1.0 - 2.0 * (k + 0.5) / layers) : 0.0;
            const double target = sh.color.a * blurred_coverage(e, sigma);
            if (target <= accumulated) continue;
            const double alpha = (target - accumulated) / (1.0 - accumulated);
            accumulated = target;
            const double grow = sh.spread + e;
            Rect r(border_box.x + sh.x - grow, border_box.y + sh.y - grow,
                   border_box.width + 2 * grow, border_box.height + 2 * grow);
            if (r.width <= 0 || r.height <= 0) continue;
            LinearColor c = sh.color;
            c.a = static_cast<float>(std::min(1.0, alpha));

            // CSS Backgrounds L3 §7.1 says an outer shadow is drawn outside the
            // border edge only, and this used to knock the border box out by
            // tessellating the ring between the shadow rect and the box. That
            // is the right RULE — under a translucent background the shadow
            // otherwise shows through, and vendor.html's cards were tinted per
            // rarity because of it — but the ring geometry was wrong: measured
            // against Chrome on a plain `0 0 40px` shadow it removed most of
            // the falloff (white at 15-35px out where Chrome has 249, 240,
            // 222) and dropped a blur-less spread shadow entirely.
            //
            // Filling the rect is the lesser of the two errors: it is invisible
            // under an opaque background, which is most of them, whereas the
            // ring lost shadows outright. Restored until the ring can be built
            // correctly — see PORT_PLAN for what was measured.
            Mesh mesh;
            tessellate_rounded_rect(r, clamp_radii_to_rect(grow_radii(radii, grow), r.width, r.height),
                                    c, &mesh);
            draw_mesh(mesh, backend, {}, opacity, xf, clip, filter);
        }
    }
}

// An inset shadow darkens the padding box from its edges inward; the offset
// deepens the sides it points away from. Rendered as nested frames.
void paint_inset_shadows(const std::vector<Shadow>& shadows, const Rect& padding_box,
                         const BorderRadii& radii, RenderInterface* backend, double opacity,
                         const Transform2D* xf = nullptr, const ClipNode* clip = nullptr,
                         const ColorFilter* filter = nullptr) {
    for (size_t s = shadows.size(); s-- > 0;) {
        const Shadow& sh = shadows[s];
        if (!sh.inset) continue;
        const double sigma = sh.blur * 0.5;
        const int layers = shadow_layers(sh.blur);
        double accumulated = 0;
        for (int k = 0; k < layers; ++k) {
            const double e = sh.blur > 0 ? sh.blur * (1.0 - 2.0 * (k + 0.5) / layers) : 0.0;
            const double target = sh.color.a * blurred_coverage(-e, sigma);
            // The frames run thickest (faint) to thinnest (dense): at depth
            // t inside the edge only the frames at least t thick cover it.
            const double thickness = sh.spread + e;
            const double top = std::max(0.0, thickness + sh.y);
            const double bottom = std::max(0.0, thickness - sh.y);
            const double left = std::max(0.0, thickness + sh.x);
            const double right = std::max(0.0, thickness - sh.x);
            if (top + bottom + left + right <= 0) { continue; }
            if (target <= accumulated) continue;
            const double alpha = (target - accumulated) / (1.0 - accumulated);
            accumulated = target;
            LinearColor c = sh.color;
            c.a = static_cast<float>(std::min(1.0, alpha));
            const LinearColor colors[4] = {c, c, c, c};
            Mesh mesh;
            tessellate_border(padding_box, radii, std::min(top, padding_box.height),
                              std::min(right, padding_box.width), std::min(bottom, padding_box.height),
                              std::min(left, padding_box.width), colors, &mesh);
            draw_mesh(mesh, backend, {}, opacity, xf, clip, filter);
        }
    }
}

double opacity_of(const ComputedStyle* style) {
    const std::string_view raw = get(style, "opacity");
    if (raw.empty()) return 1;
    const std::string s(raw);
    char* end = nullptr;
    const double v = std::strtod(s.c_str(), &end);
    if (end == s.c_str()) return 1;
    return std::clamp(*end == '%' ? v / 100.0 : v, 0.0, 1.0);
}

bool clips_children(const ComputedStyle* style) {
    for (const char* prop : {"overflow-x", "overflow-y"}) {
        const std::string_view v = get(style, prop);
        if (v == "hidden" || v == "clip" || v == "auto" || v == "scroll") return true;
    }
    return false;
}

// Where paint is: the accumulated opacity and the scissor in force.
struct PaintState {
    double opacity = 1;
    std::optional<Recti> scissor;
    bool transformed = false;
    Transform2D xform;   // accumulated, in absolute coordinates
    std::shared_ptr<const ClipNode> clip;
    std::shared_ptr<const ColorFilter> filter;
};

bool ci_equal(std::string_view a, std::string_view b);
std::string_view trim_view(std::string_view s);
std::vector<std::string_view> split_ws(std::string_view s);

// The colour functions and drop-shadow()s of a `filter` list, in order.
// Returns false when the list has none of them.
bool parse_color_filter(const ComputedStyle* style, const LayoutContext& ctx, double font_size,
                        ColorFilter* out, std::vector<Shadow>* drop_shadows) {
    const std::string_view raw = get(style, "filter");
    if (raw.empty() || ci_equal(raw, "none")) return false;
    bool any = false;
    size_t i = 0;
    while (i < raw.size()) {
        const size_t open = raw.find('(', i);
        if (open == std::string_view::npos) break;
        size_t depth = 1, close = open + 1;
        while (close < raw.size() && depth > 0) {
            if (raw[close] == '(') ++depth;
            else if (raw[close] == ')') --depth;
            ++close;
        }
        std::string name(trim_view(raw.substr(i, open - i)));
        for (char& c : name) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        const std::string_view arg = trim_view(raw.substr(open + 1, close - open - 2));
        i = close;
        const auto number = [&](double fallback) {
            if (arg.empty()) return fallback;
            const std::string a(arg);
            char* end = nullptr;
            double v = std::strtod(a.c_str(), &end);
            if (end == a.c_str()) return fallback;
            if (*end == '%') v /= 100.0;
            return std::max(0.0, v);
        };
        ColorFilter f;
        bool colour = true;
        if (name == "brightness") {
            const float k = static_cast<float>(number(1));
            for (int r = 0; r < 3; ++r) f.m[r][r] = k;
        } else if (name == "contrast") {
            const float k = static_cast<float>(number(1));
            for (int r = 0; r < 3; ++r) { f.m[r][r] = k; f.add[r] = 0.5f - 0.5f * k; }
        } else if (name == "grayscale" || name == "saturate" || name == "sepia") {
            double amount = number(name == "saturate" ? 1 : 0);
            if (name != "saturate") amount = std::min(1.0, amount);
            const float s = static_cast<float>(name == "saturate" ? amount : 1 - amount);
            if (name == "sepia") {
                // §8.1 sepia matrix, interpolated toward identity by (1 - amount).
                const float a = static_cast<float>(amount);
                const float sep[3][3] = {{0.393f, 0.769f, 0.189f}, {0.349f, 0.686f, 0.168f},
                                         {0.272f, 0.534f, 0.131f}};
                for (int r = 0; r < 3; ++r) {
                    for (int c = 0; c < 3; ++c) f.m[r][c] = (r == c ? 1 - a : 0) + a * sep[r][c];
                }
            } else {
                // §8.1 saturate / grayscale share the luminance-weighted matrix.
                const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
                f.m[0][0] = lr + (1 - lr) * s; f.m[0][1] = lg - lg * s;       f.m[0][2] = lb - lb * s;
                f.m[1][0] = lr - lr * s;       f.m[1][1] = lg + (1 - lg) * s; f.m[1][2] = lb - lb * s;
                f.m[2][0] = lr - lr * s;       f.m[2][1] = lg - lg * s;       f.m[2][2] = lb + (1 - lb) * s;
            }
        } else if (name == "invert") {
            const float a = static_cast<float>(std::min(1.0, number(0)));
            for (int r = 0; r < 3; ++r) { f.m[r][r] = 1 - 2 * a; f.add[r] = a; }
        } else if (name == "opacity") {
            f.alpha = static_cast<float>(std::min(1.0, number(1)));
        } else if (name == "drop-shadow") {
            colour = false;
            // <offset-x> <offset-y> [<blur>]? <color>? — parsed like a box-shadow
            // without spread; the box's border box stands in for its alpha
            // shape, which is right for a card and rough for text.
            const std::vector<std::string_view> parts = split_ws(arg);
            Shadow sh;
            sh.color = LinearColor(0, 0, 0, 1);
            std::vector<double> lengths;
            std::string colour_text;
            for (size_t k = 0; k < parts.size(); ++k) {
                const ResolvedLength r = resolve_length(parts[k], ctx, font_size, std::nullopt);
                if (r.kind == LengthKind::Length && lengths.size() < 3 &&
                    (std::isdigit(static_cast<unsigned char>(parts[k][0])) || parts[k][0] == '-' ||
                     parts[k][0] == '.' || parts[k][0] == '+')) {
                    lengths.push_back(r.pixels);
                } else {
                    // The colour may itself contain spaces: rejoin the rest.
                    for (size_t q = k; q < parts.size(); ++q) {
                        if (!colour_text.empty()) colour_text += ' ';
                        colour_text += std::string(parts[q]);
                    }
                    break;
                }
            }
            if (lengths.size() >= 2) {
                sh.x = lengths[0];
                sh.y = lengths[1];
                if (lengths.size() > 2) sh.blur = lengths[2];
                if (!colour_text.empty()) {
                    CssParseError err;
                    CssValuePtr v = parse_css_value(colour_text, &err);
                    if (v && v->kind() == CssValueKind::Color) {
                        const auto& c = static_cast<const CssColor&>(*v);
                        sh.color = LinearColor::from_srgb(c.r, c.g, c.b, c.a);
                    }
                }
                if (drop_shadows) drop_shadows->push_back(sh);
                any = true;
            }
        } else {
            colour = false;   // blur() handled elsewhere; url() not applied
        }
        if (colour) {
            out->then(f);
            any = true;
        }
    }
    return any;
}

// Pushes a polygon clip onto the state's chain, mapping it through the
// transform in force so it lives in screen space (see ClipNode).
void push_clip(PaintState* state, std::vector<ClipPoint> polygon) {
    if (polygon.size() < 3) return;
    if (state->transformed) {
        for (ClipPoint& p : polygon) {
            double x = 0, y = 0;
            state->xform.apply(p.x, p.y, &x, &y);
            p.x = x;
            p.y = y;
        }
    }
    auto n = std::make_shared<ClipNode>();
    n->polygon = std::move(polygon);
    n->parent = state->clip;
    state->clip = std::move(n);
}

// ---- clip-path (CSS Masking L1 §5) --------------------------------------------
//
// polygon(), circle(), ellipse() and inset() against the border box; url()
// and the other reference boxes are not handled (no clip). Lengths resolve
// like any other; percentages against the box's width (x) or height (y),
// and for a circle's radius against sqrt(w^2 + h^2) / sqrt(2).

bool ci_equal(std::string_view a, std::string_view b);   // defined with the form controls below

double clip_len(std::string_view raw, const LayoutContext& ctx, double font_size, double basis) {
    const ResolvedLength r = resolve_length(raw, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return basis * r.percent * 0.01;
    return 0;
}

std::string_view trim_view(std::string_view s) {
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.front()))) s.remove_prefix(1);
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) s.remove_suffix(1);
    return s;
}

std::vector<std::string_view> split_ws(std::string_view s) {
    std::vector<std::string_view> out;
    size_t i = 0;
    while (i < s.size()) {
        while (i < s.size() && std::isspace(static_cast<unsigned char>(s[i]))) ++i;
        const size_t start = i;
        while (i < s.size() && !std::isspace(static_cast<unsigned char>(s[i]))) ++i;
        if (i > start) out.push_back(s.substr(start, i - start));
    }
    return out;
}

// A <position> component: keyword or length-percentage, against `basis`.
double position_component(std::string_view raw, const LayoutContext& ctx, double font_size,
                          double basis) {
    if (ci_equal(raw, "left") || ci_equal(raw, "top")) return 0;
    if (ci_equal(raw, "center")) return basis * 0.5;
    if (ci_equal(raw, "right") || ci_equal(raw, "bottom")) return basis;
    return clip_len(raw, ctx, font_size, basis);
}

void ellipse_points(double cx, double cy, double rx, double ry, std::vector<ClipPoint>* out) {
    const int n = 48;
    for (int i = 0; i < n; ++i) {
        const double a = 2 * 3.14159265358979323846 * i / n;
        out->push_back({cx + rx * std::cos(a), cy + ry * std::sin(a)});
    }
}

bool parse_clip_path(const ComputedStyle* style, const LayoutContext& ctx, double font_size,
                     const Rect& box, std::vector<ClipPoint>* out) {
    const std::string_view raw = trim_view(get(style, "clip-path"));
    if (raw.empty() || ci_equal(raw, "none")) return false;
    const size_t open = raw.find('(');
    const size_t close = raw.rfind(')');
    if (open == std::string_view::npos || close == std::string_view::npos || close < open) return false;
    std::string name(trim_view(raw.substr(0, open)));
    for (char& c : name) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    const std::string_view inner = trim_view(raw.substr(open + 1, close - open - 1));
    out->clear();

    if (name == "polygon") {
        size_t i = 0;
        bool first = true;
        while (i <= inner.size()) {
            size_t comma = inner.find(',', i);
            if (comma == std::string_view::npos) comma = inner.size();
            const std::string_view seg = trim_view(inner.substr(i, comma - i));
            i = comma + 1;
            if (seg.empty()) continue;
            if (first && (ci_equal(seg, "nonzero") || ci_equal(seg, "evenodd"))) { first = false; continue; }
            first = false;
            const std::vector<std::string_view> parts = split_ws(seg);
            if (parts.size() < 2) return false;
            out->push_back({box.x + clip_len(parts[0], ctx, font_size, box.width),
                            box.y + clip_len(parts[1], ctx, font_size, box.height)});
        }
        return out->size() >= 3;
    }

    // circle( [<radius>]? [at <position>]? ), ellipse( [rx ry]? [at ...]? )
    if (name == "circle" || name == "ellipse") {
        std::string_view shape = inner, pos;
        const size_t at = inner.find(" at ");
        if (at != std::string_view::npos) {
            shape = trim_view(inner.substr(0, at));
            pos = trim_view(inner.substr(at + 4));
        } else if (inner.substr(0, 3) == "at " || inner.substr(0, 3) == "at\t") {
            shape = {};
            pos = trim_view(inner.substr(3));
        }
        double cx = box.width * 0.5, cy = box.height * 0.5;
        if (!pos.empty()) {
            const std::vector<std::string_view> p = split_ws(pos);
            if (!p.empty()) cx = position_component(p[0], ctx, font_size, box.width);
            if (p.size() > 1) cy = position_component(p[1], ctx, font_size, box.height);
        }
        const auto side_radius = [&](std::string_view r, double along_basis, bool horizontal) {
            const double d0 = horizontal ? cx : cy;
            const double d1 = (horizontal ? box.width : box.height) - d0;
            if (r.empty() || ci_equal(r, "closest-side")) return std::min(d0, d1);
            if (ci_equal(r, "farthest-side")) return std::max(d0, d1);
            return clip_len(r, ctx, font_size, along_basis);
        };
        double rx = 0, ry = 0;
        const std::vector<std::string_view> rs = split_ws(shape);
        if (name == "circle") {
            const double basis = std::sqrt(box.width * box.width + box.height * box.height) / std::sqrt(2.0);
            const std::string_view r = rs.empty() ? std::string_view() : rs[0];
            if (r.empty() || ci_equal(r, "closest-side")) {
                rx = ry = std::min({cx, cy, box.width - cx, box.height - cy});
            } else if (ci_equal(r, "farthest-side")) {
                rx = ry = std::max({cx, cy, box.width - cx, box.height - cy});
            } else {
                rx = ry = clip_len(r, ctx, font_size, basis);
            }
        } else {
            rx = side_radius(rs.empty() ? std::string_view() : rs[0], box.width, true);
            ry = side_radius(rs.size() > 1 ? rs[1] : std::string_view(), box.height, false);
        }
        if (rx <= 0 || ry <= 0) return false;
        ellipse_points(box.x + cx, box.y + cy, rx, ry, out);
        return true;
    }

    // inset( <top> [<right> [<bottom> [<left>]]] [round <radius>] )
    if (name == "inset") {
        std::string_view offsets = inner, round;
        const size_t r = inner.find(" round ");
        if (r != std::string_view::npos) {
            offsets = trim_view(inner.substr(0, r));
            round = trim_view(inner.substr(r + 7));
        }
        const std::vector<std::string_view> o = split_ws(offsets);
        if (o.empty()) return false;
        const double t = clip_len(o[0], ctx, font_size, box.height);
        const double rt = clip_len(o.size() > 1 ? o[1] : o[0], ctx, font_size, box.width);
        const double bt = clip_len(o.size() > 2 ? o[2] : o[0], ctx, font_size, box.height);
        const double lt = clip_len(o.size() > 3 ? o[3] : (o.size() > 1 ? o[1] : o[0]), ctx, font_size, box.width);
        const Rect inset(box.x + lt, box.y + t, box.width - lt - rt, box.height - t - bt);
        if (inset.width <= 0 || inset.height <= 0) return false;
        BorderRadii radii;
        if (!round.empty()) {
            const std::vector<std::string_view> rr = split_ws(round);
            const auto cr = [&](size_t i) {
                const std::string_view v = rr[std::min(i, rr.size() - 1)];
                return CornerRadius(clip_len(v, ctx, font_size, inset.width),
                                    clip_len(v, ctx, font_size, inset.height));
            };
            radii = BorderRadii(cr(0), cr(1), cr(2), cr(3));
        }
        *out = rounded_rect_outline(inset, radii);
        return out->size() >= 3;
    }
    return false;
}

Recti intersect(const Recti& a, const Recti& b) {
    const int x0 = std::max(a.x, b.x), y0 = std::max(a.y, b.y);
    const int x1 = std::min(a.x + a.width, b.x + b.width);
    const int y1 = std::min(a.y + a.height, b.y + b.height);
    return {x0, y0, std::max(0, x1 - x0), std::max(0, y1 - y0)};
}

// Packs every glyph the document will draw, without emitting any geometry.
//
// This exists so the atlas is uploaded exactly once per frame. Uploading it
// lazily at the first text draw looks equivalent and is not: a later run that
// adds a glyph makes the atlas dirty again, and the upload releases the texture
// the earlier draw already referenced. The published draw list then names a
// texture that no longer exists, which a host that maps ids faithfully renders
// untextured — and which, under a real GPU backend, frees a texture a queued
// draw is still using. Shaping twice is cheap next to that.
// The face a run draws with: the document face, or the host's bold / italic
// variant of it for the run's font-weight and font-style.
FaceHandle face_for_run(const Box& b, const PaintContext& paint) {
    if (!paint.font) return paint.face;
    const int weight = resolve_font_weight(b.style);
    const bool italic = resolve_font_italic(b.style);
    if (weight < 600 && !italic) return paint.face;
    return paint.font->variant(paint.face, weight, italic);
}

// ---- Form controls (Runtime/Forms/InputRenderer.cs) -----------------------
//
// An <input>'s value, a <select>'s chosen option and a placeholder are not in
// the box tree — the runtime paints them as an overlay on the control's box,
// and so does this. Checkbox / radio marks, the range rail + thumb and the
// select chevron are the same UA drawings the reference emits.

bool ci_equal(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(a[i])) !=
            std::tolower(static_cast<unsigned char>(b[i]))) {
            return false;
        }
    }
    return true;
}

std::string input_type(const Element& e) {
    std::string t(e.get_attribute("type"));
    for (char& c : t) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return t;
}

// HTML §4.10.7: the selected option is the last one with `selected`, else
// the first option (also looked for inside <optgroup>).
const Element* selected_option(const Element& select) {
    const Element* first = nullptr;
    const Element* chosen = nullptr;
    const auto visit = [&](const Element& o) {
        if (!first) first = &o;
        if (o.has_attribute("selected")) chosen = &o;
    };
    for (const Ref<Node>& c : select.children()) {
        if (c->node_type() != NodeType::Element) continue;
        const auto& ce = static_cast<const Element&>(*c);
        if (ce.tag_name() == "option") visit(ce);
        else if (ce.tag_name() == "optgroup") {
            for (const Ref<Node>& g : ce.children()) {
                if (g->node_type() == NodeType::Element &&
                    static_cast<const Element&>(*g).tag_name() == "option") {
                    visit(static_cast<const Element&>(*g));
                }
            }
        }
    }
    return chosen ? chosen : first;
}

std::string trimmed_text_of(const Element& e) {
    std::string s;
    for (const Ref<Node>& c : e.children()) {
        if (c->node_type() == NodeType::Text) s += static_cast<const TextNode&>(*c).data();
    }
    size_t a = 0, z = s.size();
    while (a < z && std::isspace(static_cast<unsigned char>(s[a]))) ++a;
    while (z > a && std::isspace(static_cast<unsigned char>(s[z - 1]))) --z;
    return s.substr(a, z - a);
}

struct ControlText {
    std::string text;
    bool placeholder = false;   // painted faded
    bool centered = true;       // vertically, in the content box (single-line controls)
};

// The text a control shows that no box carries. False for controls whose
// content is in the tree (a <textarea> with text, a <button>) or drawn as a
// mark rather than text.
bool form_control_text(const Box& b, ControlText* out) {
    if (!b.element || b.kind != BoxKind::Block) return false;
    const Element& e = *b.element;
    const std::string_view tag = e.tag_name();
    if (tag == "input") {
        const std::string type = input_type(e);
        if (type == "checkbox" || type == "radio" || type == "range" || type == "hidden" ||
            type == "file" || type == "color" || type == "image") {
            return false;
        }
        const std::string_view value = e.get_attribute("value");
        if (type == "submit" || type == "button" || type == "reset") {
            out->text = !value.empty() ? std::string(value)
                        : type == "submit" ? "Submit" : type == "reset" ? "Reset" : "";
            return !out->text.empty();
        }
        if (!value.empty()) {
            if (type == "password") {
                out->text.clear();
                for (size_t i = 0; i < value.size(); ++i) out->text += "\xE2\x80\xA2";
            } else {
                out->text = std::string(value);
            }
            return true;
        }
        const std::string_view ph = e.get_attribute("placeholder");
        if (ph.empty()) return false;
        out->text = std::string(ph);
        out->placeholder = true;
        return true;
    }
    if (tag == "select") {
        if (e.has_attribute("multiple") || e.has_attribute("size")) return false;
        const Element* opt = selected_option(e);
        if (!opt) return false;
        out->text = trimmed_text_of(*opt);
        return !out->text.empty();
    }
    if (tag == "textarea") {
        if (!trimmed_text_of(e).empty()) return false;
        const std::string_view ph = e.get_attribute("placeholder");
        if (ph.empty()) return false;
        out->text = std::string(ph);
        out->placeholder = true;
        out->centered = false;
        return true;
    }
    return false;
}

bool is_form_control(const Box& b) {
    if (!b.element || b.kind != BoxKind::Block) return false;
    const std::string_view tag = b.element->tag_name();
    return tag == "input" || tag == "select" || tag == "textarea";
}

const FontMetrics* control_metrics(const LayoutContext& ctx, const ComputedStyle* style) {
    const FontMetrics* base = ctx.font_for(get(style, "font-family"));
    if (!ctx.variant_metrics || !base) return base;
    const int weight = resolve_font_weight(style);
    const bool italic = resolve_font_italic(style);
    if (weight < 600 && !italic) return base;
    const FontMetrics* v = ctx.variant_metrics(ctx.variant_user, base, weight, italic);
    return v ? v : base;
}

// CSS UI 4 §5.5 accent-color; `auto` is the platform default, the runtime's
// indigo.
LinearColor accent_color_of(const ComputedStyle* style) {
    const LinearColor kCheck(0.090f, 0.196f, 0.671f, 1.f);
    const std::string_view raw = get(style, "accent-color");
    if (raw.empty() || ci_equal(raw, "auto")) return kCheck;
    const LinearColor c = resolve_color(style, "accent-color");
    return c.a > 0 ? c : kCheck;
}

void fill_rounded(const Rect& r, double radius, const LinearColor& color, RenderInterface* backend,
                  double opacity, const Transform2D* xf, const ClipNode* clip = nullptr,
                  const ColorFilter* filter = nullptr) {
    if (r.width <= 0 || r.height <= 0 || !backend) return;
    Mesh mesh;
    const double rr = std::min(radius, std::min(r.width, r.height) * 0.5);
    const CornerRadius cr(rr);
    tessellate_rounded_rect(r, BorderRadii(cr, cr, cr, cr), color, &mesh);
    draw_mesh(mesh, backend, {}, opacity, xf, clip, filter);
}

double attr_double(const Element& e, std::string_view name, double fallback) {
    const std::string_view raw = e.get_attribute(name);
    if (raw.empty()) return fallback;
    char* end = nullptr;
    const std::string s(raw);
    const double v = std::strtod(s.c_str(), &end);
    if (end == s.c_str() || !std::isfinite(v)) return fallback;
    return v;
}

void paint_form_control(const Box& b, const LayoutContext& ctx, double x, double y, double fs,
                        const PaintContext& paint, TextureHandle atlas_texture,
                        const PaintState& state, const Transform2D* xf) {
    const Element& e = *b.element;
    const std::string_view tag = e.tag_name();
    const double cl = x + b.border_left + b.padding_left;
    const double ct = y + b.border_top + b.padding_top;
    const double cw = b.width - b.border_left - b.border_right - b.padding_left - b.padding_right;
    const double ch = b.height - b.border_top - b.border_bottom - b.padding_top - b.padding_bottom;
    if (tag == "input") {
        const std::string type = input_type(e);
        if (type == "checkbox") {
            if (!e.has_attribute("checked")) return;
            const double inset = 2;
            fill_rounded(Rect(x + inset, y + inset, b.width - 2 * inset, b.height - 2 * inset), 1,
                         accent_color_of(b.style), paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
            return;
        }
        if (type == "radio") {
            if (!e.has_attribute("checked")) return;
            const double inset = b.width * 0.25;
            const double d = b.width - 2 * inset;
            fill_rounded(Rect(x + inset, y + inset, d, d), d * 0.5, accent_color_of(b.style),
                         paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
            return;
        }
        if (type == "range") {
            double lo = attr_double(e, "min", 0), hi = attr_double(e, "max", 100);
            if (hi <= lo) hi = lo + 1;
            const double value = attr_double(e, "value", (lo + hi) * 0.5);
            double frac = (value - lo) / (hi - lo);
            if (!(frac >= 0)) frac = 0;
            if (frac > 1) frac = 1;
            if (cw <= 0) return;
            const double content_h = ch > 0 ? ch : b.height;
            const double cy = ct + content_h * 0.5;
            const LinearColor accent = accent_color_of(b.style);
            const double rail_h = std::min(content_h, 6.0);
            const double thumb = std::max(rail_h, std::min(content_h, 14.0));
            const double rail_top = cy - rail_h * 0.5;
            const double usable = std::max(0.0, cw - thumb);
            const double cx = cl + thumb * 0.5 + frac * usable;
            LinearColor groove = accent;
            groove.a *= 0.3f;
            fill_rounded(Rect(cl, rail_top, cw, rail_h), rail_h * 0.5, groove, paint.backend,
                         state.opacity, xf, state.clip.get(), state.filter.get());
            if (cx - cl > 0) {
                fill_rounded(Rect(cl, rail_top, cx - cl, rail_h), rail_h * 0.5, accent, paint.backend,
                             state.opacity, xf, state.clip.get(), state.filter.get());
            }
            fill_rounded(Rect(cx - thumb * 0.5, cy - thumb * 0.5, thumb, thumb), thumb * 0.5, accent,
                         paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
            return;
        }
    }

    ControlText t;
    const bool has_text = form_control_text(b, &t);
    if (has_text && paint.font && paint.atlas && cw > 0 && ch > 0) {
        const FaceHandle face = face_for_run(b, paint);
        const FontMetrics* m = control_metrics(ctx, b.style);
        const double ascent = m ? m->ascent(fs) : fs * 0.8;
        const double line_h = m ? m->line_height(fs) : fs * kDefaultLineHeightFactor;
        const double baseline =
            t.centered ? ct + std::max(0.0, (ch - line_h) * 0.5) + ascent : ct + ascent;
        LinearColor color = resolve_color(b.style, "color");
        if (t.placeholder) color = LinearColor(color.r * 0.5f, color.g * 0.5f, color.b * 0.5f, color.a * 0.5f);
        // The overlay is clipped to the padding box, as the runtime's text
        // overlay is; a long value does not spill past the border.
        Recti clip{static_cast<int>(std::floor(cl)), static_cast<int>(std::floor(ct)),
                   static_cast<int>(std::ceil(cl + cw)) - static_cast<int>(std::floor(cl)),
                   static_cast<int>(std::ceil(ct + ch)) - static_cast<int>(std::floor(ct))};
        if (state.scissor) clip = intersect(*state.scissor, clip);
        paint.backend->set_scissor(&clip);
        Mesh text;
        build_text_geometry(t.text, cl, baseline, fs, color, paint, &text,
                            letter_spacing_of(b.style, ctx, fs), &face);
        draw_mesh(text, paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());
        paint.backend->set_scissor(state.scissor ? &*state.scissor : nullptr);
    }
    if (tag == "select" && !e.has_attribute("multiple") && !e.has_attribute("size")) {
        // The runtime's v1 caret: a 6x3 grey bar 8px from the right edge.
        const double margin = 8, w = 6, h = 3;
        fill_rounded(Rect(x + b.width - margin - w, y + (b.height - h) * 0.5, w, h), 1,
                     LinearColor(0.6f, 0.6f, 0.6f, 1.f), paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
    }
}

void prepare_glyphs(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                    const PaintContext& paint) {
    const Box& b = tree[id];
    if (b.kind == BoxKind::Text && !b.text.empty() && paint.font && paint.atlas) {
        const FaceHandle face = face_for_run(b, paint);
        std::vector<ShapedGlyph> glyphs;
        paint.font->shape(face, b.text, b.font_size, &glyphs);
        for (const ShapedGlyph& g : glyphs) {
            paint.atlas->get(paint.font, face, g.glyph, b.font_size);
        }
    }
    // Control overlays draw text no box carries; their glyphs go into the
    // same single up-front upload.
    ControlText t;
    if (paint.font && paint.atlas && is_form_control(b) && form_control_text(b, &t)) {
        const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
        const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
        const FaceHandle face = face_for_run(b, paint);
        std::vector<ShapedGlyph> glyphs;
        paint.font->shape(face, t.text, fs, &glyphs);
        for (const ShapedGlyph& g : glyphs) paint.atlas->get(paint.font, face, g.glyph, fs);
    }
    for (BoxId c : tree.children(id)) prepare_glyphs(tree, c, ctx, paint);
}

bool has_gradient_layer(const std::vector<BackgroundLayer>& layers) {
    for (const BackgroundLayer& l : layers) {
        if (l.is_gradient) return true;
    }
    return false;
}

// Paints `layers` over `color` across `area` as ONE textured mesh: the layers
// are rasterized together into an RGBA texture the size of the area (capped,
// the UVs scale), and the rounded rectangle samples it. Rendering the
// gradient itself as geometry would need per-pixel maths the canvas cannot
// do; a texture per box is exact and one draw. Returns false when there is
// nothing textured to paint, so the caller paints the plain colour.
bool paint_layered_background(const std::vector<BackgroundLayer>& layers, const LinearColor& color,
                              const Rect& area, const BorderRadii& radii, const LayoutContext& ctx,
                              double font_size, const PaintContext& paint, double opacity = 1,
                              const Transform2D* xf = nullptr, const ClipNode* clip = nullptr,
                              const ColorFilter* filter = nullptr) {
    if (!has_gradient_layer(layers) || !paint.backend || area.width <= 0 || area.height <= 0) {
        return false;
    }
    const int tex_w = static_cast<int>(std::min(1024.0, std::ceil(area.width)));
    const int tex_h = static_cast<int>(std::min(1024.0, std::ceil(area.height)));
    std::vector<uint8_t> rgba;
    rasterize_background(layers, color, area.width, area.height, tex_w, tex_h, ctx, font_size, &rgba);
    if (filter) filter_rgba(&rgba, *filter);
    const TextureHandle tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
    if (paint.owned_textures) paint.owned_textures->push_back(tex);

    Mesh mesh;
    tessellate_rounded_rect(area, radii, LinearColor::white(), &mesh);
    for (Vertex& v : mesh.vertices) {
        v.tex_coord = {static_cast<float>((v.position.x - area.x) / area.width),
                       static_cast<float>((v.position.y - area.y) / area.height)};
    }
    draw_mesh(mesh, paint.backend, tex, opacity, xf, clip, filter);
    return true;
}

// The box's background image layers, resolved against its own colour.
std::vector<BackgroundLayer> layers_of(const Box& b) {
    if (!b.style) return {};
    return resolve_background_layers(b.style, resolve_color(b.style, "color"));
}

void paint_recursive(const BoxTree& tree, BoxId id, const LayoutContext& ctx, double origin_x,
                     double origin_y, const PaintContext& paint, TextureHandle atlas_texture,
                     BoxId canvas_owner, PaintState state) {
    const Box& b = tree[id];
    const double x = origin_x + b.x;
    const double y = origin_y + b.y;

    // Line boxes carry their container's style for inline layout's sake and
    // anonymous boxes are not elements: neither has a background or border of
    // its own to paint. Painting a line box with its <th>'s background drew a
    // bar behind every header's text.
    const bool decorated = b.kind != BoxKind::Line && b.kind != BoxKind::AnonymousBlock &&
                           b.kind != BoxKind::AnonymousInline && b.kind != BoxKind::Text;
    if (decorated && b.style) state.opacity *= opacity_of(b.style);
    // `visibility: hidden` paints nothing of the box itself; its children
    // inherit the value and paint nothing either unless they override it.
    const bool hidden = b.style && get(b.style, "visibility") == "hidden";

    const BoxId parent = b.parent;
    const ComputedStyle* ps = parent == kNoBox ? nullptr : tree[parent].style;
    const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
    const Rect border_box(x, y, b.width, b.height);
    const BorderRadii radii =
        decorated && b.style ? resolve_border_radii(b.style, b.width, b.height, ctx, fs)
                             : BorderRadii::zero();

    // A transform applies to the box and everything in it, about its origin
    // in the box, in the frame its ancestors' transforms already set.
    if (decorated && b.style && b.width > 0 && b.height > 0) {
        Transform2D local;
        if (parse_transform(b.style, ctx, fs, b.width, b.height, &local)) {
            const Transform2D about_box =
                Transform2D::translate(static_cast<float>(-x), static_cast<float>(-y))
                    .multiply(local)
                    .multiply(Transform2D::translate(static_cast<float>(x), static_cast<float>(y)));
            state.xform = state.transformed ? about_box.multiply(state.xform) : about_box;
            state.transformed = true;
        }
    }
    const Transform2D* xf = state.transformed ? &state.xform : nullptr;

    // `clip-path` clips the box's own paint and everything below it.
    if (decorated && b.style && b.width > 0 && b.height > 0) {
        std::vector<ClipPoint> poly;
        if (parse_clip_path(b.style, ctx, fs, border_box, &poly)) push_clip(&state, std::move(poly));
    }

    // `filter: blur()`: the box's own paint — background and shape — is
    // rasterized with room around it, blurred, and drawn as one texture; its
    // border and shadows are folded into that (dropped, for now), and its
    // children paint sharp on top.
    const double blur = decorated && b.style ? blur_filter_radius(b.style, ctx, fs) : 0;
    bool blurred = false;
    if (blur > 0 && !hidden && b.width > 0 && b.height > 0 && paint.backend && id != canvas_owner) {
        const std::vector<BackgroundLayer> layers = layers_of(b);
        const LinearColor bg = resolve_color(b.style, "background-color");
        if (bg.a > 0 || has_gradient_layer(layers)) {
            const double pad_px = 3 * blur;
            const double full_w = b.width + 2 * pad_px, full_h = b.height + 2 * pad_px;
            const double scale = std::min(1.0, 1024.0 / std::max(full_w, full_h));
            const int tex_w = std::max(1, static_cast<int>(std::ceil(full_w * scale)));
            const int tex_h = std::max(1, static_cast<int>(std::ceil(full_h * scale)));
            const int pad = static_cast<int>(std::round(pad_px * scale));
            std::vector<uint8_t> rgba;
            rasterize_background_padded(layers, bg, b.width, b.height, tex_w, tex_h, pad, &radii, ctx,
                                        fs, &rgba);
            blur_rgba(&rgba, tex_w, tex_h, blur * scale);
            const TextureHandle tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
            if (paint.owned_textures) paint.owned_textures->push_back(tex);
            const Rect area(x - pad_px, y - pad_px, full_w, full_h);
            Mesh mesh;
            tessellate_rect(area, LinearColor::white(), &mesh);
            for (Vertex& v : mesh.vertices) {
                v.tex_coord = {static_cast<float>((v.position.x - area.x) / area.width),
                               static_cast<float>((v.position.y - area.y) / area.height)};
            }
            draw_mesh(mesh, paint.backend, tex, state.opacity, xf, state.clip.get(), state.filter.get());
            blurred = true;
        }
    }

    // `filter`'s colour functions apply to the box and its subtree, composed
    // under any filter already in force; drop-shadow()s paint as outer
    // shadows of the border box.
    std::vector<Shadow> drop_shadows;
    if (decorated && b.style && b.width > 0 && b.height > 0) {
        ColorFilter own;
        if (parse_color_filter(b.style, ctx, fs, &own, &drop_shadows)) {
            auto combined = std::make_shared<ColorFilter>(own);
            if (state.filter) combined->then(*state.filter);
            state.filter = std::move(combined);
        }
    }

    std::vector<Shadow> shadows;
    if (decorated && b.style && !hidden && !blurred && b.width > 0 && b.height > 0) {
        shadows = parse_box_shadows(b.style, ctx, fs, resolve_color(b.style, "color"));
        if (!drop_shadows.empty()) {
            paint_outer_shadows(drop_shadows, border_box, radii, paint.backend, state.opacity, xf,
                                state.clip.get(), state.filter.get());
        }
        paint_outer_shadows(shadows, border_box, radii, paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
    }

    // A box whose background went onto the canvas (§14.2) does not paint it
    // again; a box with gradient layers paints them as one texture and only
    // its border through the mesh; one that clips its background to its text
    // paints it through the glyphs instead (see the text branch).
    const bool clip_text = decorated && b.style && clips_background_to_text(b.style);
    bool background_done = id == canvas_owner || !decorated || hidden || blurred || clip_text;
    if (!background_done && b.style && b.width > 0 && b.height > 0) {
        const std::vector<BackgroundLayer> layers = layers_of(b);
        if (has_gradient_layer(layers)) {
            background_done = paint_layered_background(
                layers, resolve_color(b.style, "background-color"), border_box, radii, ctx, fs, paint,
                state.opacity, xf, state.clip.get(), state.filter.get());
        }
    }

    if (decorated && !hidden && !blurred) {
        Mesh mesh;
        paint_box_decorations(tree, id, ctx, x, y, &mesh, !background_done);
        draw_mesh(mesh, paint.backend, {}, state.opacity, xf, state.clip.get(), state.filter.get());
        if (!shadows.empty()) {
            const Rect padding_box(x + b.border_left, y + b.border_top,
                                   b.width - b.border_left - b.border_right,
                                   b.height - b.border_top - b.border_bottom);
            if (padding_box.width > 0 && padding_box.height > 0) {
                paint_inset_shadows(shadows, padding_box,
                                    inset_radii(radii, b.border_top, b.border_right, b.border_bottom,
                                                b.border_left),
                                    paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
            }
        }
    }

    // A control's UA drawing — value / placeholder / chosen option text,
    // check and radio marks, the range rail, the select caret — sits on its
    // own box, under its children (a <textarea>'s text).
    if (decorated && !hidden && paint.backend && is_form_control(b) && b.width > 0 &&
        b.height > 0) {
        paint_form_control(b, ctx, x, y, fs, paint, atlas_texture, state, xf);
    }

    // A text run's own y is its top; the baseline is where the glyphs sit, and
    // the line box put it there.
    if (b.kind == BoxKind::Text && !b.text.empty() && paint.font && paint.atlas && !hidden) {
        const BoxId line = b.parent;
        const double baseline =
            line != kNoBox && tree[line].kind == BoxKind::Line ? origin_y + tree[line].baseline
                                                               : y + b.height;
        const double spacing =
            letter_spacing_of(b.style, ctx, b.font_size) + b.justify_letter_spacing;
        const LinearColor text_color = resolve_color(b.style, "color");
        const FaceHandle run_face = face_for_run(b, paint);
        // text-shadow first, under the glyphs: the offset run in the shadow
        // colour. A blur is a 5x5 Gaussian kernel of glyph copies, sigma =
        // blur / 2, weights normalised so the stack's coverage approaches
        // the shadow's alpha at the centre — no blur pass exists in the
        // canvas, and text is cheap to draw 25 times.
        for (const TextShadow& sh : parse_text_shadows(b.style, ctx, b.font_size, text_color)) {
            if (sh.blur <= 0) {
                Mesh shadow;
                build_text_geometry(b.text, x + sh.x, baseline + sh.y, b.font_size, sh.color, paint,
                                    &shadow, spacing, &run_face);
                draw_mesh(shadow, paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());
                continue;
            }
            const double sigma = sh.blur * 0.5;
            double weights[5][5];
            double total = 0;
            for (int i = -2; i <= 2; ++i) {
                for (int j = -2; j <= 2; ++j) {
                    weights[i + 2][j + 2] = std::exp(-(i * i + j * j) / 2.0);
                    total += weights[i + 2][j + 2];
                }
            }
            for (int i = -2; i <= 2; ++i) {
                for (int j = -2; j <= 2; ++j) {
                    LinearColor c = sh.color;
                    // Source-over of the stack: each copy's alpha is its share
                    // of the remaining coverage, so the centre sums to the
                    // shadow's own alpha rather than saturating.
                    c.a = static_cast<float>(sh.color.a * weights[i + 2][j + 2] / total * 1.6);
                    if (c.a > 1) c.a = 1;
                    Mesh shadow;
                    build_text_geometry(b.text, x + sh.x + i * sigma, baseline + sh.y + j * sigma,
                                        b.font_size, c, paint, &shadow, spacing, &run_face);
                    draw_mesh(shadow, paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());
                }
            }
        }
        Mesh text;
        build_text_geometry(b.text, x, baseline, b.font_size, text_color, paint, &text, spacing,
                            &run_face);
        // `background-clip: text`: the run's element paints its gradient
        // through the glyphs. Each glyph vertex takes the gradient's colour
        // at its position over the run — right for a run-wide gradient,
        // approximate for a multi-line one.
        if (clips_background_to_text(b.style)) {
            const std::vector<BackgroundLayer> layers = layers_of(b);
            const BackgroundLayer* grad = nullptr;
            for (const BackgroundLayer& l : layers) if (l.is_gradient) { grad = &l; break; }
            if (grad) {
                for (Vertex& v : text.vertices) {
                    float c[4];
                    sample_gradient(grad->gradient, v.position.x - x, v.position.y - y,
                                    std::max(1.0, b.width), std::max(1.0, b.height), ctx, b.font_size, c);
                    v.color = LinearColor(srgb_to_linear_f(c[0]), srgb_to_linear_f(c[1]),
                                          srgb_to_linear_f(c[2]), c[3]);
                }
            }
        }
        // The handle from the single up-front upload, never a fresh one: see
        // prepare_glyphs.
        draw_mesh(text, paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());
    }

    // `overflow` other than visible clips the children to the padding box
    // (§11.1.1) — a rectangle for now: the corners of a rounded scroller are
    // not rounded off.
    const std::optional<Recti> outer_scissor = state.scissor;
    if (decorated && b.style && clips_children(b.style)) {
        const double px0 = x + b.border_left, py0 = y + b.border_top;
        const double px1 = x + b.width - b.border_right, py1 = y + b.height - b.border_bottom;
        double cx0 = px0, cy0 = py0, cx1 = px1, cy1 = py1;
        if (xf) {
            // The clip follows the transform as the bounding box of the
            // transformed padding box: exact for translation and scale, a
            // superset under rotation.
            const double xs[4] = {px0, px1, px1, px0}, ys[4] = {py0, py0, py1, py1};
            cx0 = cy0 = 1e300;
            cx1 = cy1 = -1e300;
            for (int k = 0; k < 4; ++k) {
                double tx = 0, ty = 0;
                xf->apply(xs[k], ys[k], &tx, &ty);
                cx0 = std::min(cx0, tx); cy0 = std::min(cy0, ty);
                cx1 = std::max(cx1, tx); cy1 = std::max(cy1, ty);
            }
        }
        Recti r{static_cast<int>(std::floor(cx0)), static_cast<int>(std::floor(cy0)),
                std::max(0, static_cast<int>(std::ceil(cx1)) - static_cast<int>(std::floor(cx0))),
                std::max(0, static_cast<int>(std::ceil(cy1)) - static_cast<int>(std::floor(cy0)))};
        if (state.scissor) r = intersect(*state.scissor, r);
        state.scissor = r;
        paint.backend->set_scissor(&r);
        // A rounded scroller clips to its rounded padding box (§11.1.1 with
        // Backgrounds §5.3): the corners are cut geometrically, since the
        // scissor is only the bounding rectangle.
        const BorderRadii inner =
            inset_radii(radii, b.border_top, b.border_right, b.border_bottom, b.border_left);
        if (!inner.top_left.is_zero() || !inner.top_right.is_zero() ||
            !inner.bottom_right.is_zero() || !inner.bottom_left.is_zero()) {
            push_clip(&state, rounded_rect_outline(Rect(px0, py0, px1 - px0, py1 - py0), inner));
        }
    }

    // CSS 2.1 Appendix E, per container as BoxToPaintConverter does it:
    // negative-z stacking contexts, then in-flow children in tree order, then
    // positioned z:auto/0 children in tree order, then positive z ascending
    // (ties by tree order). A ring's `::after` cover painted over its
    // `z-index: 1` number until this; a badge's `position: absolute`
    // sibling painted under a later in-flow one.
    struct ChildEntry {
        BoxId id;
        int z;
        int order;
    };
    std::vector<ChildEntry> negative, in_flow, positioned, positive;
    {
        int order = 0;
        const DisplayKind pd = b.display;
        const bool items_stack = pd == DisplayKind::Flex || pd == DisplayKind::InlineFlex ||
                                 pd == DisplayKind::Grid || pd == DisplayKind::InlineGrid;
        for (BoxId c : tree.children(id)) {
            const Box& cb = tree[c];
            const bool is_positioned =
                cb.style && cb.kind == BoxKind::Block && cb.position != PositionType::Static;
            int z = 0;
            if (cb.z_index && is_positioned) {
                z = *cb.z_index;
            } else if (items_stack && cb.style && cb.kind == BoxKind::Block) {
                // A flex/grid item stacks by z-index without being positioned
                // (Flexbox §4.3, Grid §6.4); layout stamps z only on the
                // positioned, so it is read here.
                const std::string_view zr = get(cb.style, "z-index");
                if (!zr.empty() && !ci_equal(zr, "auto")) z = std::atoi(std::string(zr).c_str());
            }
            const ChildEntry e{c, z, order++};
            if (z < 0) negative.push_back(e);
            else if (z > 0) positive.push_back(e);
            else if (is_positioned) positioned.push_back(e);
            else in_flow.push_back(e);
        }
    }
    const auto by_z = [](const ChildEntry& a, const ChildEntry& c) {
        return a.z != c.z ? a.z < c.z : a.order < c.order;
    };
    std::stable_sort(negative.begin(), negative.end(), by_z);
    std::stable_sort(positive.begin(), positive.end(), by_z);
    for (const auto* bucket : {&negative, &in_flow, &positioned, &positive}) {
        for (const ChildEntry& e : *bucket) {
            paint_recursive(tree, e.id, ctx, x, y, paint, atlas_texture, canvas_owner, state);
        }
    }

    if (state.scissor.has_value() != outer_scissor.has_value() ||
        (state.scissor && outer_scissor &&
         (state.scissor->x != outer_scissor->x || state.scissor->y != outer_scissor->y ||
          state.scissor->width != outer_scissor->width ||
          state.scissor->height != outer_scissor->height))) {
        paint.backend->set_scissor(outer_scissor ? &*outer_scissor : nullptr);
    }
}

BoxId child_element(const BoxTree& tree, BoxId parent, std::string_view tag) {
    if (parent == kNoBox) return kNoBox;
    for (BoxId c : tree.children(parent)) {
        const Box& b = tree[c];
        if (b.kind == BoxKind::Block && b.element && b.element->tag_name() == tag) return c;
    }
    return kNoBox;
}

bool has_background(const Box& b) {
    if (!b.style) return false;
    if (resolve_color(b.style, "background-color").a > 0) return true;
    return has_gradient_layer(layers_of(b));
}

// CSS 2.1 §14.2: the root element's background covers the whole canvas; when
// the root has none, the body's is used instead and the body paints none of
// its own. Returns the box whose background was taken.
BoxId paint_canvas(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                   const PaintContext& paint) {
    BoxId html = child_element(tree, root, "html");
    if (html == kNoBox && tree[root].element && tree[root].element->tag_name() == "html") html = root;
    const BoxId body = child_element(tree, html, "body");
    BoxId owner = kNoBox;
    if (html != kNoBox && has_background(tree[html])) owner = html;
    else if (body != kNoBox && has_background(tree[body])) owner = body;
    if (owner == kNoBox) return kNoBox;

    const Box& b = tree[owner];
    const BoxId parent = b.parent;
    const ComputedStyle* ps = parent == kNoBox ? nullptr : tree[parent].style;
    const double fs = font_size_px(b.style, ps, ctx);
    const Rect canvas(0, 0, ctx.viewport_width_px, ctx.viewport_height_px);
    const LinearColor color = resolve_color(b.style, "background-color");
    if (!paint_layered_background(layers_of(b), color, canvas, BorderRadii::zero(), ctx, fs, paint) &&
        color.a > 0) {
        Mesh mesh;
        tessellate_rect(canvas, color, &mesh);
        draw_mesh(mesh, paint.backend, {});
    }
    return owner;
}

} // namespace

LinearColor resolve_color(const ComputedStyle* style, std::string_view property) {
    const std::string_view raw = get(style, property);
    if (raw.empty()) return LinearColor::transparent();
    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v || v->kind() != CssValueKind::Color) return LinearColor::transparent();
    const auto& c = static_cast<const CssColor&>(*v);
    return LinearColor::from_srgb(c.r, c.g, c.b, c.a);
}

BorderRadii resolve_border_radii(const ComputedStyle* style, double width, double height,
                                 const LayoutContext& ctx, double font_size) {
    if (!style) return BorderRadii::zero();
    return BorderRadii(corner(style, "border-top-left-radius", ctx, font_size, width, height),
                       corner(style, "border-top-right-radius", ctx, font_size, width, height),
                       corner(style, "border-bottom-right-radius", ctx, font_size, width, height),
                       corner(style, "border-bottom-left-radius", ctx, font_size, width, height));
}

void paint_box_decorations(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                           double origin_x, double origin_y, Mesh* out, bool with_background) {
    const Box& b = tree[id];
    if (!b.style || b.width <= 0 || b.height <= 0) return;

    const BoxId parent = b.parent;
    const ComputedStyle* ps = parent == kNoBox ? nullptr : tree[parent].style;
    const double fs = font_size_px(b.style, ps, ctx);
    const Rect border_box(origin_x, origin_y, b.width, b.height);
    const BorderRadii radii = resolve_border_radii(b.style, b.width, b.height, ctx, fs);

    // The background paints under the border, out to the border box: a
    // semi-transparent border shows the background through it.
    const LinearColor bg = resolve_color(b.style, "background-color");
    if (with_background && bg.a > 0) tessellate_rounded_rect(border_box, radii, bg, out);

    if (b.border_top > 0 || b.border_right > 0 || b.border_bottom > 0 || b.border_left > 0) {
        // An unset border-color is `currentColor`, which is what makes a
        // border follow the text colour by default.
        const auto side = [&](std::string_view property) {
            const std::string_view raw = get(b.style, property);
            if (raw.empty() || raw == "currentcolor" || raw == "currentColor") {
                return resolve_color(b.style, "color");
            }
            return resolve_color(b.style, property);
        };
        const LinearColor colors[4] = {side("border-top-color"), side("border-right-color"),
                                       side("border-bottom-color"), side("border-left-color")};
        tessellate_border(border_box, radii, b.border_top, b.border_right, b.border_bottom,
                          b.border_left, colors, out);
    }
}

void build_text_geometry(std::string_view text, double x, double baseline_y, double font_size,
                         const LinearColor& color, const PaintContext& paint, Mesh* out,
                         double letter_spacing, const FaceHandle* face_override) {
    if (!paint.font || !paint.atlas || text.empty()) return;
    const FaceHandle face = face_override ? *face_override : paint.face;
    std::vector<ShapedGlyph> glyphs;
    paint.font->shape(face, text, font_size, &glyphs);

    double pen = x;
    for (const ShapedGlyph& g : glyphs) {
        const GlyphSlot* slot = paint.atlas->get(paint.font, face, g.glyph, font_size);
        // A glyph with no bitmap — a space, or one the face does not have —
        // still advances the pen. Skipping the advance would close the gaps
        // between words.
        if (slot) {
            const double gx = pen + g.x_offset + slot->bearing_x;
            // bearing_y measures UP from the baseline, so the quad's top edge
            // is above it.
            const double gy = baseline_y - g.y_offset - slot->bearing_y;
            const uint32_t base = static_cast<uint32_t>(out->vertices.size());
            // A colour glyph carries its own colours in the atlas; only the
            // text's alpha applies to it (CSS Fonts 4 §5.2 — `color` does not
            // tint a colour font's glyphs).
            const LinearColor glyph_color =
                slot->is_color ? LinearColor(1.f, 1.f, 1.f, color.a) : color;
            const auto v = [&](double px, double py, float u, float w) {
                Vertex vt;
                vt.position = {static_cast<float>(px), static_cast<float>(py)};
                vt.color = glyph_color;
                vt.tex_coord = {u, w};
                return vt;
            };
            out->vertices.push_back(v(gx, gy, slot->u0, slot->v0));
            out->vertices.push_back(v(gx + slot->width, gy, slot->u1, slot->v0));
            out->vertices.push_back(v(gx + slot->width, gy + slot->height, slot->u1, slot->v1));
            out->vertices.push_back(v(gx, gy + slot->height, slot->u0, slot->v1));
            for (uint32_t i : {0u, 1u, 2u, 0u, 2u, 3u}) out->indices.push_back(base + i);
        }
        pen += g.x_advance + letter_spacing;
    }
}

void paint_tree(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                RenderInterface* backend) {
    PaintContext p;
    p.backend = backend;
    paint_tree(tree, root, ctx, p);
}

void paint_tree(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                const PaintContext& paint) {
    if (!paint.backend || root == kNoBox) return;

    TextureHandle atlas_texture{};
    if (paint.atlas && paint.font) {
        prepare_glyphs(tree, root, ctx, paint);
        atlas_texture = paint.atlas->texture(paint.backend);
    }
    const BoxId canvas_owner = paint_canvas(tree, root, ctx, paint);
    paint_recursive(tree, root, ctx, 0, 0, paint, atlas_texture, canvas_owner, PaintState{});
}

} // namespace weva
