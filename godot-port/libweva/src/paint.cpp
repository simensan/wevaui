#include "weva/paint.h"

#include "weva/background.h"
#include "weva/block_layout.h"
#include "weva/css_value.h"
#include "weva/positioning.h"
#include "weva/scrollbar.h"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace weva {

TextureHandle TextureCache::get(const std::string& key) {
    auto it = entries_.find(key);
    if (it == entries_.end()) {
        ++misses_;
        return {};
    }
    it->second.used = true;
    ++hits_;
    return it->second.texture;
}

void TextureCache::put(const std::string& key, TextureHandle texture) {
    entries_[key] = Entry{texture, true};
}

void TextureCache::begin_pass() {
    hits_ = 0;
    misses_ = 0;
    for (auto& kv : entries_) kv.second.used = false;
}

void TextureCache::end_pass(RenderInterface* backend) {
    static const bool cache_log = std::getenv("WEVA_CACHE_LOG") != nullptr;
    if (cache_log)
        std::fprintf(stderr, "[cache] hits %d misses %d entries %zu\n", hits_, misses_,
                     entries_.size());

    for (auto it = entries_.begin(); it != entries_.end();) {
        if (it->second.used) {
            ++it;
            continue;
        }
        if (backend) backend->release_texture(it->second.texture);
        it = entries_.erase(it);
    }
}

void TextureCache::release_all(RenderInterface* backend) {
    if (backend) {
        for (auto& kv : entries_) backend->release_texture(kv.second.texture);
    }
    entries_.clear();
}

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
    // The polygon triangulated, bounded, and with its inscribed rectangle
    // found -- all of which depend on the polygon alone, so a rounded scroller
    // works them out once rather than once per box inside it.
    //
    // LAZILY, though, and that is not a detail. contains_box already turns
    // most draws away before any of this is needed, so preparing eagerly in
    // push_clip made layout-stress SLOWER -- 13.7 ms to 14.9 -- by paying for
    // work the common path never asks for. Measured both ways.
    mutable PreparedClip prepared;
    mutable bool prepared_ready = false;

    const PreparedClip& clip_shape() const {
        if (!prepared_ready) {
            prepared.polygon = polygon;
            prepared.prepare();
            prepared_ready = true;
        }
        return prepared;
    }

    // Bounds and winding, so a mesh that plainly needs no clipping can skip it.
    // A rounded `overflow: hidden` clips every descendant draw, and a blurred
    // box shadow inside one is up to 48 nested rings each cut against a
    // hundred-vertex polygon -- 90us a ring, and 10 ms of match3-endgame's
    // 15 ms update.
    double min_x = 0, min_y = 0, max_x = 0, max_y = 0;
    double orient = 1;   // sign of the signed area: which side of an edge is in

    void prepare() {
        min_x = min_y = 1e300;
        max_x = max_y = -1e300;
        double area2 = 0;
        for (size_t i = 0; i < polygon.size(); ++i) {
            const ClipPoint& a = polygon[i];
            const ClipPoint& b = polygon[(i + 1) % polygon.size()];
            min_x = std::min(min_x, a.x);
            min_y = std::min(min_y, a.y);
            max_x = std::max(max_x, a.x);
            max_y = std::max(max_y, a.y);
            area2 += a.x * b.y - b.x * a.y;
        }
        orient = area2 >= 0 ? 1.0 : -1.0;
    }

    bool intersects_box(double x0, double y0, double x1, double y1) const {
        return !(x1 < min_x || x0 > max_x || y1 < min_y || y0 > max_y);
    }

    // True when the whole box lies inside every edge's inward half-plane. That
    // region is the polygon's kernel, which is contained in the polygon for any
    // simple polygon -- so a true answer is sound, and a false one only means
    // the clip runs as before.
    //
    // The inscribed rectangle prepare() found would answer this in O(1) rather
    // than O(edges). Measured, and it is not worth it: the rectangle is the
    // more conservative test, so more meshes fall through to the per-triangle
    // path, and layout-stress came out very slightly SLOWER.
    bool contains_box(double x0, double y0, double x1, double y1) const {
        if (x0 < min_x || x1 > max_x || y0 < min_y || y1 > max_y) return false;
        const double cx[4] = {x0, x1, x1, x0};
        const double cy[4] = {y0, y0, y1, y1};
        for (size_t i = 0; i < polygon.size(); ++i) {
            const ClipPoint& a = polygon[i];
            const ClipPoint& b = polygon[(i + 1) % polygon.size()];
            const double dx = b.x - a.x, dy = b.y - a.y;
            for (int c = 0; c < 4; ++c) {
                if ((dx * (cy[c] - a.y) - dy * (cx[c] - a.x)) * orient < 0) return false;
            }
        }
        return true;
    }
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
        double x0 = 1e300, y0 = 1e300, x1 = -1e300, y1 = -1e300;
        const auto bounds = [&] {
            x0 = y0 = 1e300;
            x1 = y1 = -1e300;
            for (const Vertex& v : cur.vertices) {
                x0 = std::min<double>(x0, v.position.x);
                y0 = std::min<double>(y0, v.position.y);
                x1 = std::max<double>(x1, v.position.x);
                y1 = std::max<double>(y1, v.position.y);
            }
        };
        bounds();
        for (const ClipNode* n = clip; n; n = n->parent.get()) {
            // Nothing of the mesh survives, or all of it does: either way the
            // per-triangle cut is skipped. A pixel of slack for the coverage
            // ramp a feathered edge carries past its nominal bounds.
            if (!n->intersects_box(x0, y0, x1, y1)) return;
            if (n->contains_box(x0 - 1, y0 - 1, x1 + 1, y1 + 1)) continue;
            Mesh tmp;
            clip_triangles_polygon(cur.vertices, cur.indices, n->clip_shape(), &tmp);
            cur = std::move(tmp);
            if (cur.empty()) return;
            bounds();
        }
        if (opacity < 1) {
            for (Vertex& v : cur.vertices) v.color.a *= static_cast<float>(std::max(0.0, opacity));
        }
        backend->render_mesh(std::move(cur.vertices), std::move(cur.indices), {0, 0}, tex);
        return;
    }
    const Mesh& mesh = input;
    // Handed over rather than compiled: `cur` and `copy` are this function's
    // own and are moved, so the backend that keeps the geometry takes it
    // without a copy. A backend that batches will want geometry to outlive a
    // frame; that needs the paint cache, keyed on style and layout versions,
    // which is a later slice.
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
        backend->render_mesh(std::move(copy.vertices), std::move(copy.indices), {0, 0}, tex);
        return;
    }
    // The last one copies: the mesh belongs to the caller, and the backend
    // keeps what it is given.
    backend->render_mesh(mesh.vertices, mesh.indices, {0, 0}, tex);
}

// Asks the backend to filter what it has already painted, inside `shape`.
//
// The shape goes through the same transform and the same clip stack a fill
// would, so a backdrop-filtered panel is confined by an ancestor's overflow
// exactly as its background is. Opacity is deliberately NOT folded in: it
// scales what the element paints, and the backdrop is not that.
void filter_backdrop(const Mesh& shape, RenderInterface* backend, const BackdropEffect& effect,
                     const Transform2D* xform = nullptr, const ClipNode* clip = nullptr) {
    if (shape.empty()) return;
    Mesh cur = shape;
    if (xform) {
        for (Vertex& v : cur.vertices) {
            double x = 0, y = 0;
            xform->apply(v.position.x, v.position.y, &x, &y);
            v.position = {static_cast<float>(x), static_cast<float>(y)};
        }
    }
    for (const ClipNode* n = clip; n; n = n->parent.get()) {
        Mesh tmp;
        clip_triangles_polygon(cur.vertices, cur.indices, n->clip_shape(), &tmp);
        cur = std::move(tmp);
        if (cur.empty()) return;
    }
    backend->filter_backdrop(cur.vertices, cur.indices, effect);
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
// `property` is "filter" or "backdrop-filter", which share a grammar.
double blur_filter_radius(const ComputedStyle* style, const LayoutContext& ctx, double font_size,
                          const char* property = "filter") {
    const std::string_view raw = get(style, property);
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

// WEVA_PAINT_LOG attributes a paint pass to its parts. Paint stayed the whole
// cost of an update after the texture cache landed, and a per-pass total does
// not say which part -- shadows, text or the backend -- to look at.
struct PaintProfile {
    double shadows = 0, text = 0, backgrounds = 0, other = 0;
    double shadow_tess = 0, shadow_draw = 0;
    int shadow_layers = 0;
    bool on = false;
};
// Thread-local: two documents can paint at once, and a diagnostic must not
// introduce a data race to collect its numbers.
thread_local PaintProfile g_paint_profile;

struct ProfileScope {
    double* slot;
    std::chrono::steady_clock::time_point t0;
    explicit ProfileScope(double* s)
        : slot(g_paint_profile.on ? s : nullptr) {
        if (slot) t0 = std::chrono::steady_clock::now();
    }
    ~ProfileScope() {
        if (!slot) return;
        *slot += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0)
                     .count();
    }
};

// One blurred outer shadow, as a single textured quad.
//
// The alternative below draws it as up to 48 nested rings, each tessellated,
// clipped and copied into a draw of its own. That was over half of a paint
// pass on a page with shadows -- hud spends 1.0 ms of its 1.9 ms there, and 80
// of its 187 draws are shadow rings -- and it BANDS, which is why the ring
// count had to be raised until quests' `0 34px 90px` stopped showing twelve
// grey steps.
//
// A real Gaussian costs one rasterize, one blur and one quad, and the result is
// cached on everything that decides its texels, so a repaint that did not
// change the shadow does not redo any of it.
//
// Returns false when it cannot -- no backend, a degenerate box, a texture that
// would be absurd -- and the ring path takes over.
bool paint_blurred_box_shadow(const Shadow& sh, const Rect& border_box, const BorderRadii& radii,
                              const LayoutContext& ctx, double font_size,
                              const PaintContext& paint, double opacity, const Transform2D* xf,
                              const ClipNode* clip, const ColorFilter* filter) {
    if (!paint.backend || sh.blur <= 0) return false;
    if (border_box.width <= 0 || border_box.height <= 0) return false;

    // The shape the shadow is cast from: the border box, moved by the offset
    // and grown by the spread, with its radii grown to match.
    const double grow = sh.spread;
    const Rect shape(border_box.x + sh.x - grow, border_box.y + sh.y - grow,
                     border_box.width + 2 * grow, border_box.height + 2 * grow);
    if (shape.width <= 0 || shape.height <= 0) return false;
    const BorderRadii shape_radii =
        clamp_radii_to_rect(grow_radii(radii, grow), shape.width, shape.height);
    // And the shape that gets punched out of it, clamped to the box it belongs
    // to. Clamping is not a tidy-up here: a radius larger than the box -- a
    // pill states `border-radius: 999px` -- makes the corner ellipse enormous,
    // and the coverage test then reports the box's own CENTRE as outside it.
    // Nothing was knocked out at all, and every neon panel wore its glow as a
    // flat wash across its face.
    const BorderRadii box_radii =
        clamp_radii_to_rect(radii, border_box.width, border_box.height);

    // A filter rewrites the shadow's colour; it is flat, so filtering it here
    // is the same as filtering every texel and costs one call.
    LinearColor col = sh.color;
    if (filter) {
        float a = col.a;
        filter->apply_srgb(&col.r, &col.g, &col.b, &a);
        col.a = a;
    }
    if (col.a <= 0) return true;   // nothing to draw, but handled

    const double sigma = sh.blur * 0.5;
    const double pad_px = std::ceil(3 * sigma);
    const double full_w = shape.width + 2 * pad_px, full_h = shape.height + 2 * pad_px;
    const double scale = std::min(1.0, 1024.0 / std::max(full_w, full_h));
    const int tex_w = std::max(1, static_cast<int>(std::ceil(full_w * scale)));
    const int tex_h = std::max(1, static_cast<int>(std::ceil(full_h * scale)));
    const int pad = static_cast<int>(std::round(pad_px * scale));
    if (tex_w - 2 * pad < 1 || tex_h - 2 * pad < 1) return false;

    std::string key;
    TextureHandle tex;
    if (paint.texture_cache) {
        char buf[256];
        std::snprintf(buf, sizeof(buf),
                      "bs|%.3f;%.3f;%.3f;%.3f;%.3f;%.3f;%.4f;%.4f;%.4f;%.4f;%d;%d;%d|",
                      border_box.width, border_box.height, sh.x, sh.y, sh.blur, sh.spread, col.r,
                      col.g, col.b, col.a, tex_w, tex_h, pad);
        key = buf;
        for (const CornerRadius* c : {&radii.top_left, &radii.top_right, &radii.bottom_right,
                                      &radii.bottom_left}) {
            std::snprintf(buf, sizeof(buf), "%.3f,%.3f;", c->x_radius, c->y_radius);
            key += buf;
        }
        tex = paint.texture_cache->get(key);
    }

    if (!tex) {
        std::vector<uint8_t> rgba;
        rasterize_background_padded({}, col, shape.width, shape.height, tex_w, tex_h, pad,
                                    &shape_radii, ctx, font_size, &rgba);
        blur_rgba(&rgba, tex_w, tex_h, sigma * scale);

        // CSS Backgrounds L3 §7.1: an outer shadow is not painted inside the
        // border box. The ring path did this by never drawing there; here the
        // blurred image is punched through, which is the same thing and keeps
        // the soft edge the blur put on it.
        const double inv = scale > 0 ? 1.0 / scale : 0.0;
        for (int ty = 0; ty < tex_h; ++ty) {
            const double sy = (ty - pad + 0.5) * inv;
            const double by = sy + sh.y - grow;
            if (by < -1 || by > border_box.height + 1) continue;
            for (int tx = 0; tx < tex_w; ++tx) {
                const double sx = (tx - pad + 0.5) * inv;
                const double bx = sx + sh.x - grow;
                const double cov = rounded_rect_coverage(bx, by, border_box.width,
                                                         border_box.height, &box_radii);
                if (cov <= 0) continue;
                uint8_t& a = rgba[(static_cast<size_t>(ty) * tex_w + tx) * 4 + 3];
                a = static_cast<uint8_t>(a * (1.0 - cov) + 0.5);
            }
        }

        tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
        if (paint.texture_cache && !key.empty()) paint.texture_cache->put(key, tex);
        else if (paint.owned_textures) paint.owned_textures->push_back(tex);
    }

    const Rect area(shape.x - pad_px, shape.y - pad_px, full_w, full_h);
    Mesh mesh;
    // The blurred image carries its own soft edge; a feather would blur an
    // already blurred boundary.
    tessellate_rect(area, LinearColor::white(), &mesh, false);
    for (Vertex& v : mesh.vertices) {
        v.tex_coord = {static_cast<float>((v.position.x - area.x) / area.width),
                       static_cast<float>((v.position.y - area.y) / area.height)};
    }
    draw_mesh(mesh, paint.backend, tex, opacity, xf, clip, nullptr);
    return true;
}

void paint_outer_shadows(const std::vector<Shadow>& shadows, const Rect& border_box,
                         const BorderRadii& radii, const LayoutContext& ctx, double font_size,
                         const PaintContext& paint, double opacity,
                         const Transform2D* xf = nullptr, const ClipNode* clip = nullptr,
                         const ColorFilter* filter = nullptr) {
    RenderInterface* backend = paint.backend;
    ProfileScope prof(&g_paint_profile.shadows);
    for (size_t s = shadows.size(); s-- > 0;) {   // first shadow on top
        const Shadow& sh = shadows[s];
        if (sh.inset) continue;
        if (paint_blurred_box_shadow(sh, border_box, radii, ctx, font_size, paint, opacity, xf,
                                     clip, filter)) {
            continue;
        }
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

            // CSS Backgrounds L3 §7.1: an outer shadow is drawn OUTSIDE the
            // border edge only. Filling the whole rect is invisible under an
            // opaque background but wrong under anything else — vendor's cards
            // are `rgba(22, 16, 40, 0.92)`, so 8% of the shadow showed through
            // and tinted each one by its rarity glow, where Chrome renders them
            // flat.
            //
            // The knockout is the ring a border already tessellates. The widths
            // are how far the shadow rect reaches past the border box on each
            // side, which an offset makes asymmetric and can drive to zero on
            // the side the shadow moves away from.
            //
            // This failed twice before, losing every shadow OUTSIDE the box as
            // well. The cause was not here: tessellate_border returned an EMPTY
            // mesh whenever a corner was rounded on the outer outline and square
            // on the inner one, which is exactly this shape, since the grown
            // radius equals the width. Fixed in tessellate.cpp.
            const double left = std::max(0.0, border_box.x - r.x);
            const double top = std::max(0.0, border_box.y - r.y);
            const double right =
                std::max(0.0, (r.x + r.width) - (border_box.x + border_box.width));
            const double bottom =
                std::max(0.0, (r.y + r.height) - (border_box.y + border_box.height));
            if (left <= 0 && top <= 0 && right <= 0 && bottom <= 0) continue;
            const LinearColor ring[4] = {c, c, c, c};
            Mesh mesh;
            ++g_paint_profile.shadow_layers;
            {
                ProfileScope t(&g_paint_profile.shadow_tess);
                tessellate_border(r, clamp_radii_to_rect(grow_radii(radii, grow), r.width, r.height),
                                  top, right, bottom, left, ring, &mesh);
            }
            {
                ProfileScope d(&g_paint_profile.shadow_draw);
                draw_mesh(mesh, backend, {}, opacity, xf, clip, filter);
            }
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


// Everything that decides a rasterized layer's pixels, as a string. Cheap
// beside the rasterization it avoids -- which is a texel per pixel of the box,
// and viewport-sized for the canvas.
std::string background_key(const ComputedStyle* style, const LinearColor& color, double w,
                           double h, const BorderRadii& radii, double font_size, double blur,
                           const ColorFilter* filter) {
    std::string k;
    k.reserve(128);
    const auto num = [&](double v) {
        char buf[32];
        std::snprintf(buf, sizeof(buf), "%.3f;", v);
        k += buf;
    };
    num(w);
    num(h);
    num(font_size);
    num(blur);
    num(color.r);
    num(color.g);
    num(color.b);
    num(color.a);
    const CornerRadius corners[4] = {radii.top_left, radii.top_right, radii.bottom_right,
                                     radii.bottom_left};
    for (const CornerRadius& c : corners) {
        num(c.x_radius);
        num(c.y_radius);
    }
    // The raw CSS decides the layers, and it is already a string. Keying on the
    // parsed form would mean serialising every gradient stop by hand and
    // getting it wrong the first time a property grew a field.
    for (const char* prop : {"background-image", "background-position", "background-size",
                             "background-repeat", "color"}) {
        k += get(style, prop);
        k += '|';
    }
    // A colour filter rewrites the texels, so two boxes alike but for their
    // filter are not the same texture.
    if (filter) {
        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) num(filter->m[r][c]);
            num(filter->add[r]);
        }
        num(filter->alpha);
    }
    return k;
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
                        ColorFilter* out, std::vector<Shadow>* drop_shadows,
                        const char* property = "filter") {
    const std::string_view raw = get(style, property);
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
    n->prepare();
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
        // The selection band, behind the glyphs. An <input> draws its own text,
        // so the range indexes it directly -- there are no runs to map through.
        const double spacing = letter_spacing_of(b.style, ctx, fs);
        if (!t.placeholder && paint.caret.element == &e &&
            paint.caret.selection_to > paint.caret.selection_from) {
            const size_t from = std::min(paint.caret.selection_from, t.text.size());
            const size_t to = std::min(paint.caret.selection_to, t.text.size());
            if (to > from) {
                Mesh before;
                build_text_geometry(t.text.substr(0, from), 0, 0, fs, color, paint, &before,
                                    spacing, &face);
                Mesh through;
                build_text_geometry(t.text.substr(0, to), 0, 0, fs, color, paint, &through, spacing,
                                    &face);
                double start = 0, end = 0;
                for (const Vertex& v : before.vertices) start = std::max<double>(start, v.position.x);
                for (const Vertex& v : through.vertices) end = std::max<double>(end, v.position.x);
                if (from == 0) start = 0;
                if (end > start) {
                    const double top = t.centered ? ct + std::max(0.0, (ch - line_h) * 0.5) : ct;
                    Mesh band;
                    tessellate_rect(Rect(cl + start, top, end - start, std::min(ch, line_h)),
                                    LinearColor::from_srgb(51, 144, 255, 0.45f), &band, false);
                    draw_mesh(band, paint.backend, {}, state.opacity, xf, state.clip.get(),
                              state.filter.get());
                }
            }
        }
        Mesh text;
        build_text_geometry(t.text, cl, baseline, fs, color, paint, &text, spacing, &face);
        draw_mesh(text, paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());

        // The caret, at the character the cursor sits before. Its x is the
        // width of the text up to that point, measured the same way the run
        // was laid out -- anything else and the bar drifts from the glyphs as
        // the value grows.
        if (paint.caret.visible && paint.caret.element == &e && !t.placeholder) {
            const size_t at = std::min(static_cast<size_t>(std::max(0, paint.caret.index)),
                                       t.text.size());
            const std::string_view prefix(t.text.data(), at);
            Mesh measure;
            build_text_geometry(prefix, 0, 0, fs, color, paint, &measure,
                                letter_spacing_of(b.style, ctx, fs), &face);
            double advance = 0;
            for (const Vertex& v : measure.vertices) {
                advance = std::max<double>(advance, v.position.x);
            }
            // An empty prefix measures nothing, which is the left edge.
            const double caret_x = cl + (at == 0 ? 0.0 : advance);
            const double top = t.centered ? ct + std::max(0.0, (ch - line_h) * 0.5) : ct;
            Mesh bar;
            tessellate_rect(Rect(caret_x, top, 1.0, std::min(ch, line_h)), color, &bar, false);
            draw_mesh(bar, paint.backend, {}, state.opacity, xf, state.clip.get(),
                      state.filter.get());
        }
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

// The open list's labels, into the same up-front upload. Without this the
// popup's glyphs go into the atlas AFTER the tree's draws have referenced it,
// and the re-pack turns every word on the page into a smear of the wrong
// texels -- which is exactly what a screenshot with a dropdown open showed.
void prepare_popup_glyphs(const BoxTree& tree, const LayoutContext& ctx,
                          const PaintContext& paint) {
    const Element* select = paint.popup.element;
    if (!select || !paint.font || !paint.atlas) return;
    for (int i = 0; i < tree.size(); ++i) {
        const Box& b = tree[i];
        if (b.element != select || b.kind != BoxKind::Block) continue;
        const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
        const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
        for (const Element* option : select_options(*select)) {
            std::vector<ShapedGlyph> glyphs;
            paint.font->shape(paint.face, trimmed_text_of(*option), fs, &glyphs);
            for (const ShapedGlyph& g : glyphs) paint.atlas->get(paint.font, paint.face, g.glyph, fs);
        }
        return;
    }
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
                              const ColorFilter* filter = nullptr,
                              const ComputedStyle* style = nullptr) {
    if (!has_gradient_layer(layers) || !paint.backend || area.width <= 0 || area.height <= 0) {
        return false;
    }
    ProfileScope prof(&g_paint_profile.backgrounds);
    const int tex_w = static_cast<int>(std::min(1024.0, std::ceil(area.width)));
    const int tex_h = static_cast<int>(std::min(1024.0, std::ceil(area.height)));
    // Rasterizing is a texel per pixel of the box, so an unchanged background
    // is looked up rather than redrawn. See TextureCache.
    std::string key;
    TextureHandle tex;
    if (paint.texture_cache && style) {
        key = background_key(style, color, area.width, area.height, radii, font_size, 0, filter);
        tex = paint.texture_cache->get(key);
    }
    if (!tex) {
        std::vector<uint8_t> rgba;
        rasterize_background(layers, color, area.width, area.height, tex_w, tex_h, ctx, font_size,
                             &rgba);
        if (filter) filter_rgba(&rgba, *filter);
        tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
        if (paint.texture_cache && !key.empty()) paint.texture_cache->put(key, tex);
        else if (paint.owned_textures) paint.owned_textures->push_back(tex);
    }

    Mesh mesh;
    // Antialiased like any other fill. It is tempting to think the rasterized
    // layer already carries the corner's coverage in its own alpha -- it is
    // what rounded_coverage() exists for -- but that only happens in
    // rasterize_background_PADDED, and this path calls the plain one. The
    // corner here is the mesh's, so it needs the mesh's coverage ramp, and
    // without it every gradient-filled rounded box had a hard staircase edge.
    // A conic-gradient ring showed it worst, having nothing but curve.
    //
    // The feather vertices take their UVs from position like the rest, so they
    // land just outside [0,1]; both backends clamp.
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

// A blurred text-shadow, done by blurring the run rather than stacking copies
// of it.
//
// This used to draw the glyphs 25 times on a 5x5 grid spaced one sigma apart,
// weighted like a Gaussian. At a small blur that passes for one; at a large one
// the copies simply do not overlap, and a 110px glyph with a 24px blur came out
// as a visible lattice of ghosts around the letter rather than a glow.
//
// So the run is composited into its own buffer from the glyph atlas, blurred
// with the same blur_rgba() that `filter: blur()` uses, and drawn as ONE
// textured quad. Better and cheaper: one draw instead of twenty-five.
//
// Returns false when the run has no ink to blur, and the caller falls back.
bool paint_blurred_text_shadow(std::string_view text, double x, double baseline_y,
                               double font_size, double letter_spacing, const FaceHandle& face,
                               const TextShadow& sh, const PaintContext& paint, double opacity,
                               const Transform2D* xf, const ClipNode* clip,
                               const ColorFilter* filter) {
    if (!paint.font || !paint.atlas || !paint.backend || text.empty()) return false;

    // The blurred image depends on the run and the shadow, not on where the
    // run sits, so a glow that has not changed is blurred once. Neon-style
    // pages stack several of these and they were the whole cost of a pass.
    std::string key;
    if (paint.texture_cache) {
        char buf[128];
        std::snprintf(buf, sizeof(buf), "ts|%.3f;%.3f;%llu;%.3f;%.4f;%.4f;%.4f;%.4f;%.3f;%.3f|",
                      font_size, letter_spacing, static_cast<unsigned long long>(face.id), sh.blur,
                      sh.color.r, sh.color.g, sh.color.b, sh.color.a, sh.x, sh.y);
        key = buf;
        if (filter) {
            for (int r = 0; r < 3; ++r) {
                for (int c = 0; c < 3; ++c) {
                    std::snprintf(buf, sizeof(buf), "%.4f;", filter->m[r][c]);
                    key += buf;
                }
                std::snprintf(buf, sizeof(buf), "%.4f;", filter->add[r]);
                key += buf;
            }
            std::snprintf(buf, sizeof(buf), "%.4f|", filter->alpha);
            key += buf;
        }
        key.append(text);
    }

    std::vector<ShapedGlyph> glyphs;
    paint.font->shape(face, text, font_size, &glyphs);
    if (glyphs.empty()) return false;

    // Where the run's ink actually is, so the buffer is the run's size rather
    // than the page's.
    struct Placed { const GlyphSlot* slot; double gx, gy; };
    std::vector<Placed> placed;
    double lo_x = 1e300, lo_y = 1e300, hi_x = -1e300, hi_y = -1e300;
    double pen = 0;
    for (const ShapedGlyph& g : glyphs) {
        const GlyphSlot* slot = paint.atlas->get(paint.font, face, g.glyph, font_size);
        if (slot && slot->width > 0 && slot->height > 0) {
            const double gx = pen + g.x_offset + slot->bearing_x;
            const double gy = -g.y_offset - slot->bearing_y;
            placed.push_back({slot, gx, gy});
            lo_x = std::min(lo_x, gx);
            lo_y = std::min(lo_y, gy);
            hi_x = std::max(hi_x, gx + slot->width);
            hi_y = std::max(hi_y, gy + slot->height);
        }
        pen += g.x_advance + letter_spacing;
    }
    if (placed.empty()) return false;

    const double sigma = sh.blur * 0.5;
    const int pad = std::max(1, static_cast<int>(std::ceil(sigma * 3.0)));
    const int w = static_cast<int>(std::ceil(hi_x - lo_x)) + 2 * pad;
    const int h = static_cast<int>(std::ceil(hi_y - lo_y)) + 2 * pad;
    // A run wider than the atlas is not worth a buffer this size; the caller's
    // fallback is wrong but bounded, which is better than a huge allocation.
    if (w <= 0 || h <= 0 || static_cast<long long>(w) * h > 16LL * 1024 * 1024) return false;

    TextureHandle cached;
    if (!key.empty()) cached = paint.texture_cache->get(key);
    if (cached) {
        const Rect area(x + sh.x + lo_x - pad, baseline_y + sh.y + lo_y - pad, w, h);
        Mesh mesh;
        tessellate_rect(area, LinearColor::white(), &mesh, false);
        for (Vertex& v : mesh.vertices) {
            v.tex_coord = {static_cast<float>((v.position.x - area.x) / area.width),
                           static_cast<float>((v.position.y - area.y) / area.height)};
        }
        draw_mesh(mesh, paint.backend, cached, opacity, xf, clip, nullptr);
        return true;
    }

    std::vector<uint8_t> rgba(static_cast<size_t>(w) * h * 4, 0);
    const std::vector<uint8_t>& atlas = paint.atlas->pixels();
    const int aw = paint.atlas->width();
    LinearColor col = sh.color;
    if (filter) {
        float a = col.a;
        filter->apply_srgb(&col.r, &col.g, &col.b, &a);
        col.a = a;
    }
    const auto byte = [](float v) {
        return static_cast<uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255.0f));
    };
    // Straight alpha, which is what blur_rgba expects; the shadow's colour is
    // flat and only its coverage varies.
    const uint8_t cr = byte(col.r), cg = byte(col.g), cb = byte(col.b);
    for (const Placed& p : placed) {
        for (int gy = 0; gy < p.slot->height; ++gy) {
            const int dy = static_cast<int>(std::lround(p.gy - lo_y)) + pad + gy;
            if (dy < 0 || dy >= h) continue;
            for (int gx = 0; gx < p.slot->width; ++gx) {
                const int dx = static_cast<int>(std::lround(p.gx - lo_x)) + pad + gx;
                if (dx < 0 || dx >= w) continue;
                const size_t src = (static_cast<size_t>(p.slot->y + gy) * aw + p.slot->x + gx) * 4;
                if (src + 3 >= atlas.size()) continue;
                const uint8_t cov = atlas[src + 3];
                if (cov == 0) continue;
                uint8_t* d = rgba.data() + (static_cast<size_t>(dy) * w + dx) * 4;
                // Overlapping glyphs keep the strongest coverage rather than
                // summing, or a kerned pair darkens where it overlaps.
                const uint8_t a = static_cast<uint8_t>(cov * col.a);
                if (a <= d[3]) continue;
                d[0] = cr;
                d[1] = cg;
                d[2] = cb;
                d[3] = a;
            }
        }
    }
    blur_rgba(&rgba, w, h, sigma);

    const TextureHandle tex = paint.backend->generate_texture(rgba, {w, h});
    if (paint.texture_cache && !key.empty()) paint.texture_cache->put(key, tex);
    else if (paint.owned_textures) paint.owned_textures->push_back(tex);
    const Rect area(x + sh.x + lo_x - pad, baseline_y + sh.y + lo_y - pad, w, h);
    Mesh mesh;
    tessellate_rect(area, LinearColor::white(), &mesh, false);
    for (Vertex& v : mesh.vertices) {
        v.tex_coord = {static_cast<float>((v.position.x - area.x) / area.width),
                       static_cast<float>((v.position.y - area.y) / area.height)};
    }
    draw_mesh(mesh, paint.backend, tex, opacity, xf, clip, nullptr);
    return true;
}

// How wide `text` is when laid out the way `run` was. Used for both ends of a
// selection band and for where the cursor sits: measuring rather than guessing
// keeps the band on the glyphs at any letter-spacing.
double measured_width(std::string_view text, const Box& run, const LinearColor& color,
                      const PaintContext& paint, const FaceHandle& face, double spacing) {
    if (text.empty()) return 0;
    Mesh measure;
    build_text_geometry(text, 0, 0, run.font_size, color, paint, &measure, spacing, &face);
    double advance = 0;
    for (const Vertex& v : measure.vertices) advance = std::max<double>(advance, v.position.x);
    return advance;
}

// The band behind the selected part of one run. `from`/`to` are byte offsets
// into the run's own text, already clipped to it.
void paint_selection_band(const Box& b, double x, double y, size_t from, size_t to,
                          const LinearColor& text_color, const PaintContext& paint,
                          const FaceHandle& face, double spacing, RenderInterface* backend,
                          double opacity, const Transform2D* xf, const ClipNode* clip,
                          const ColorFilter* filter) {
    if (from >= to || to > b.text.size()) return;
    const double start = measured_width(b.text.substr(0, from), b, text_color, paint, face, spacing);
    const double end = measured_width(b.text.substr(0, to), b, text_color, paint, face, spacing);
    if (end <= start) return;
    // A blue a browser would recognise, at an alpha that leaves the glyphs
    // readable: the band goes BEHIND them, and the engine has no ::selection to
    // ask for a colour yet.
    const LinearColor band = LinearColor::from_srgb(51, 144, 255, 0.45f);
    Mesh mesh;
    tessellate_rect(Rect(x + start, y, end - start, b.height > 0 ? b.height : b.font_size), band,
                    &mesh, false);
    draw_mesh(mesh, backend, {}, opacity, xf, clip, filter);
}

// The element a box belongs to: itself if it has one, otherwise the nearest
// ancestor that does. A text run has none of its own.
const Element* owner_element(const BoxTree& tree, BoxId id) {
    for (BoxId b = id; b != kNoBox; b = tree[b].parent) {
        if (tree[b].element) return tree[b].element;
    }
    return nullptr;
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
            // Blurring is the most expensive thing paint does, so a box whose
            // blurred image has not changed reuses it. See TextureCache.
            std::string key;
            TextureHandle tex;
            if (paint.texture_cache && b.style) {
                key = background_key(b.style, bg, b.width, b.height, radii, fs, blur, nullptr);
                tex = paint.texture_cache->get(key);
            }
            if (!tex) {
                std::vector<uint8_t> rgba;
                rasterize_background_padded(layers, bg, b.width, b.height, tex_w, tex_h, pad, &radii,
                                            ctx, fs, &rgba);
                blur_rgba(&rgba, tex_w, tex_h, blur * scale);
                tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
                if (paint.texture_cache && !key.empty()) paint.texture_cache->put(key, tex);
                else if (paint.owned_textures) paint.owned_textures->push_back(tex);
            }
            const Rect area(x - pad_px, y - pad_px, full_w, full_h);
            Mesh mesh;
            // The blurred image supplies its own soft edge; a feather would
            // only blur an already-blurred boundary.
            tessellate_rect(area, LinearColor::white(), &mesh, false);
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
            paint_outer_shadows(drop_shadows, border_box, radii, ctx, fs, paint, state.opacity, xf,
                                state.clip.get(), state.filter.get());
        }
        paint_outer_shadows(shadows, border_box, radii, ctx, fs, paint, state.opacity, xf,
                            state.clip.get(), state.filter.get());
    }

    // `backdrop-filter` (Filter Effects L2 §2): filter everything already
    // painted behind the box, inside its border box, then let the box paint
    // over the result. It goes here — after the shadows, before the background
    // — because the shadows are cast onto the backdrop and the background is
    // the first thing that sits ON the filtered image.
    //
    // This is the only effect the core cannot decompose into triangles, since
    // it reads the destination; see RenderInterface::filter_backdrop. A backend
    // that does not implement it renders the box without its material.
    if (decorated && b.style && !hidden && b.width > 0 && b.height > 0) {
        BackdropEffect effect;
        effect.blur_radius = blur_filter_radius(b.style, ctx, fs, "backdrop-filter");
        ColorFilter cf;
        std::vector<Shadow> ignored;   // drop-shadow() in a backdrop-filter is not painted
        if (parse_color_filter(b.style, ctx, fs, &cf, &ignored, "backdrop-filter")) {
            for (int r = 0; r < 3; ++r) {
                for (int c = 0; c < 3; ++c) effect.color.m[r][c] = cf.m[r][c];
                effect.color.add[r] = cf.add[r];
            }
            effect.color.alpha = cf.alpha;
        }
        if (effect.blur_radius > 0 || !effect.color.is_identity()) {
            // The shape is emitted TRANSPARENT on purpose. It is a region, not
            // something to paint, and a host that does not know this draw kind
            // will hand it to its rasterizer like any other: transparent, that
            // draws nothing, which is the degradation the interface promises.
            // Opaque, it painted 20 white rectangles over glass.
            //
            // Tessellated opaque and cleared afterwards, because the
            // tessellator declines to build a mesh for an invisible fill —
            // which is right for a fill and would leave this with no shape.
            // No coverage ramp: this is a REGION, and a backend reads its
            // coverage from where the triangles land rather than from their
            // alpha. A ramp would only push the filter half a pixel past the
            // border box.
            Mesh shape;
            tessellate_rounded_rect(border_box, radii, LinearColor::white(), &shape, 8, false);
            for (Vertex& v : shape.vertices) v.color = LinearColor::transparent();
            filter_backdrop(shape, paint.backend, effect, xf, state.clip.get());
        }
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
                state.opacity, xf, state.clip.get(), state.filter.get(), b.style);
        }
    }

    if (decorated && !hidden && !blurred) {
        // A plain rounded box -- a background colour, a radius, no border --
        // is offered to the backend as a SHAPE as well as triangles. It is the
        // commonest thing in any document, and a backend that can evaluate a
        // rounded box per pixel gets exact coverage from it instead of the
        // half-pixel ramp, off two triangles rather than a fan and a ring.
        //
        // Only where the geometry is the whole story: a transform or a clip
        // means the triangles have been moved or cut and the description no
        // longer matches them, so those keep the mesh. (Passing the transform
        // through is what would let a backend beat the mesh under rotation
        // too, which the ramp cannot antialias at all.)
        const LinearColor bg_color =
            b.style ? resolve_color(b.style, "background-color") : LinearColor::transparent();
        const bool no_border = b.border_top <= 0 && b.border_right <= 0 &&
                               b.border_bottom <= 0 && b.border_left <= 0;
        if (!background_done && no_border && !xf && !state.clip && bg_color.a > 0 &&
            !radii.is_zero() && b.width > 0 && b.height > 0) {
            LinearColor c = bg_color;
            c.a *= static_cast<float>(std::max(0.0, state.opacity));
            if (state.filter) {
                float a = c.a;
                state.filter->apply_srgb(&c.r, &c.g, &c.b, &a);
                c.a = a;
            }
            RoundedRect shape;
            shape.x = border_box.x;
            shape.y = border_box.y;
            shape.width = border_box.width;
            shape.height = border_box.height;
            const BorderRadii cr = clamp_radii_to_rect(radii, b.width, b.height);
            const CornerRadius corners[4] = {cr.top_left, cr.top_right, cr.bottom_right,
                                             cr.bottom_left};
            for (int i = 0; i < 4; ++i) {
                shape.radii[i][0] = corners[i].x_radius;
                shape.radii[i][1] = corners[i].y_radius;
            }
            shape.color = c;
            Mesh fallback;
            tessellate_rounded_rect(border_box, radii, c, &fallback);
            paint.backend->render_rounded_rect(shape, fallback.vertices, fallback.indices);
            background_done = true;
        }

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

    // The selection band, behind the glyphs of this run. Each run views into
    // the value's own buffer, so the difference of the pointers says which
    // slice of the value it holds, and the part of that slice inside the
    // selected range is the part to paint.
    if (b.kind == BoxKind::Text && !b.text.empty() && !hidden && paint.font && paint.atlas &&
        paint.caret.selection_to > paint.caret.selection_from && !paint.caret.source.empty()) {
        const char* base = paint.caret.source.data();
        const char* run = b.text.data();
        if (run >= base && run + b.text.size() <= base + paint.caret.source.size() &&
            owner_element(tree, id) == paint.caret.element) {
            const size_t off = static_cast<size_t>(run - base);
            const size_t from = paint.caret.selection_from > off ? paint.caret.selection_from - off
                                                                 : 0;
            const size_t to = paint.caret.selection_to > off
                                  ? std::min(paint.caret.selection_to - off, b.text.size())
                                  : 0;
            paint_selection_band(b, x, y, from, to, resolve_color(b.style, "color"), paint,
                                 face_for_run(b, paint),
                                 letter_spacing_of(b.style, ctx, b.font_size) +
                                     b.justify_letter_spacing,
                                 paint.backend, state.opacity, xf, state.clip.get(),
                                 state.filter.get());
        }
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
            if (paint_blurred_text_shadow(b.text, x, baseline, b.font_size, spacing, run_face, sh,
                                          paint, state.opacity, xf, state.clip.get(),
                                          state.filter.get())) {
                continue;
            }
            // Fallback for a run with no ink to blur, or one too large to
            // buffer: the old stack of copies, which is a poor blur but never
            // nothing.
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

    // The caret, in the run that was found to hold it. Which run, and how far
    // into it, was settled after layout: paint cannot work that out per run
    // without missing the cursor at a line end, where the newline belongs to
    // no run at all.
    if (b.kind == BoxKind::Text && id == paint.caret.run && paint.caret.visible && !hidden &&
        paint.font && paint.atlas) {
        const LinearColor caret_color = resolve_color(b.style, "color");
        const FaceHandle caret_face = face_for_run(b, paint);
        const double caret_spacing =
            letter_spacing_of(b.style, ctx, b.font_size) + b.justify_letter_spacing;
        double advance = 0;
        if (paint.caret.run_offset > 0 && paint.caret.run_offset <= b.text.size()) {
            Mesh measure;
            build_text_geometry(b.text.substr(0, paint.caret.run_offset), 0, 0, b.font_size,
                                caret_color, paint, &measure, caret_spacing, &caret_face);
            for (const Vertex& v : measure.vertices) {
                advance = std::max<double>(advance, v.position.x);
            }
        }
        Mesh bar;
        tessellate_rect(Rect(x + advance, y, 1.0, b.height > 0 ? b.height : b.font_size),
                        caret_color, &bar, false);
        draw_mesh(bar, paint.backend, {}, state.opacity, xf, state.clip.get(), state.filter.get());
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
    // A scroll container draws its own background and border where it sits and
    // its contents shifted by the offset -- which is the whole of scrolling,
    // the clip above being what makes the shifted-away part disappear.
    const double child_x = x - b.scroll_x, child_y = y - b.scroll_y;
    for (const auto* bucket : {&negative, &in_flow, &positioned, &positive}) {
        for (const ChildEntry& e : *bucket) {
            paint_recursive(tree, e.id, ctx, child_x, child_y, paint, atlas_texture, canvas_owner,
                            state);
        }
    }

    // The scrollbars, over the content and inside the container's own clip:
    // a list you can scroll with no bar on it gives no sign that there is more
    // of it, which reads as a list that is simply cut off.
    if (decorated && b.style && clips_children(b.style)) {
        for (const bool vertical : {false, true}) {
            const Scrollbar bar = scrollbar_of(tree, id, vertical, x, y);
            if (!bar.visible) continue;
            if (bar.track_color.a > 0) {
                fill_rounded(bar.track, 0, bar.track_color, paint.backend, state.opacity, xf,
                             state.clip.get(), state.filter.get());
            }
            fill_rounded(bar.thumb, bar.radius, bar.thumb_color, paint.backend, state.opacity, xf,
                         state.clip.get(), state.filter.get());
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
    // The style is passed so the canvas can be cached: it is the one texture
    // that is always viewport-sized, and re-rasterizing it was the single
    // largest cost in an update on every page that has a gradient body.
    if (!paint_layered_background(layers_of(b), color, canvas, BorderRadii::zero(), ctx, fs, paint,
                                  1, nullptr, nullptr, nullptr, b.style) &&
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
    ProfileScope prof(&g_paint_profile.text);
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
            // SNAPPED to whole pixels. The atlas holds one bitmap per glyph,
            // rasterised on the pixel grid, and the quad spans exactly its
            // texels — so if the quad starts at a fraction, every texel column
            // straddles two pixels. A backend sampling the atlas with nearest
            // filtering (which is what keeps text crisp, and what both of ours
            // do) then drops some columns and doubles others: stems come out
            // 1px here and 2px there inside one word, and diagonals break up.
            //
            // Placing it at a fraction would only be right with a bitmap per
            // subpixel phase, which is what a browser rasterises and this atlas
            // does not. The pen keeps its full precision, so spacing is still
            // accumulated exactly; only the bitmap is snapped.
            const double gx = std::round(pen + g.x_offset + slot->bearing_x);
            // bearing_y measures UP from the baseline, so the quad's top edge
            // is above it.
            const double gy = std::round(baseline_y - g.y_offset - slot->bearing_y);
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

namespace {

// The character boundary in `text` nearest `dx` pixels from its start, where
// `dx` is measured the way the run was drawn. Rounds to the nearer edge of the
// character it lands in: clicking the right half of a glyph puts the cursor
// after it, which is what every text box does.
size_t offset_nearest(std::string_view text, double dx, double font_size, double spacing,
                      const FaceHandle& face, const PaintContext& paint) {
    if (dx <= 0 || text.empty()) return 0;
    const LinearColor ignored;
    double previous = 0;
    size_t i = 0;
    while (i < text.size()) {
        // One codepoint at a time: a cursor between the bytes of one is not a
        // position at all.
        size_t next = i + 1;
        while (next < text.size() && (static_cast<unsigned char>(text[next]) & 0xC0) == 0x80) {
            ++next;
        }
        Mesh measure;
        build_text_geometry(text.substr(0, next), 0, 0, font_size, ignored, paint, &measure,
                            spacing, &face);
        double width = 0;
        for (const Vertex& v : measure.vertices) width = std::max<double>(width, v.position.x);
        if (dx < width) return dx - previous < width - dx ? i : next;
        previous = width;
        i = next;
    }
    return text.size();
}

}   // namespace

size_t control_text_offset_at(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                              const PaintContext& paint, double x) {
    if (!tree.valid(box)) return 0;
    const Box& b = tree[box];
    ControlText t;
    if (!form_control_text(b, &t) || t.placeholder) return 0;
    const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
    const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
    double ox = 0, oy = 0;
    visual_position(tree, box, &ox, &oy);
    const double content_left = ox + b.border_left + b.padding_left;
    return offset_nearest(t.text, x - content_left, fs, letter_spacing_of(b.style, ctx, fs),
                          face_for_run(b, paint), paint);
}

size_t run_text_offset_at(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                          const PaintContext& paint, std::string_view source, double origin_x,
                          double origin_y, double x, double y) {
    if (!tree.valid(box) || source.empty()) return 0;
    // The runs of this element, with where each sits: fragments in line boxes,
    // never the unsplit run they came from.
    struct Candidate {
        BoxId id;
        double x, y, height;
        size_t offset;
    };
    std::vector<Candidate> runs;
    const std::function<void(BoxId, double, double)> collect = [&](BoxId id, double ox, double oy) {
        for (BoxId c = tree[id].first_child; c != kNoBox; c = tree[c].next_sibling) {
            const Box& cb = tree[c];
            const double cx = ox + cb.x, cy = oy + cb.y;
            if (cb.kind == BoxKind::Text && !cb.text.empty() && tree[cb.parent].kind == BoxKind::Line &&
                cb.text.data() >= source.data() &&
                cb.text.data() + cb.text.size() <= source.data() + source.size()) {
                runs.push_back({c, cx, cy, cb.height,
                                static_cast<size_t>(cb.text.data() - source.data())});
            }
            collect(c, cx - cb.scroll_x, cy - cb.scroll_y);
        }
    };
    const Box& b = tree[box];
    collect(box, origin_x - b.scroll_x, origin_y - b.scroll_y);
    if (runs.empty()) return 0;

    // The line first: the run whose band of the page the point is in, or the
    // nearest one above or below when the point is past the text.
    const Candidate* best = &runs.front();
    double best_distance = 1e300;
    for (const Candidate& r : runs) {
        const double centre = r.y + r.height * 0.5;
        const double distance = std::fabs(y - centre);
        if (distance < best_distance) {
            best_distance = distance;
            best = &r;
        }
    }
    // Then the character within that line's run, or the one nearest on it.
    const Box& run = tree[best->id];
    return best->offset + offset_nearest(run.text, x - best->x, run.font_size,
                                         letter_spacing_of(run.style, ctx, run.font_size) +
                                             run.justify_letter_spacing,
                                         face_for_run(run, paint), paint);
}

std::vector<const Element*> select_options(const Element& select) {
    std::vector<const Element*> out;
    for (const Ref<Node>& c : select.children()) {
        if (c->node_type() != NodeType::Element) continue;
        const auto& e = static_cast<const Element&>(*c);
        if (e.tag_name() == "option") {
            out.push_back(&e);
        } else if (e.tag_name() == "optgroup") {
            for (const Ref<Node>& g : e.children()) {
                if (g->node_type() == NodeType::Element &&
                    static_cast<const Element&>(*g).tag_name() == "option") {
                    out.push_back(&static_cast<const Element&>(*g));
                }
            }
        }
    }
    return out;
}

SelectListGeometry select_list_geometry(const BoxTree& tree, BoxId select_box,
                                        const LayoutContext& ctx, const Element& select) {
    SelectListGeometry g;
    if (!tree.valid(select_box)) return g;
    const Box& b = tree[select_box];
    const std::vector<const Element*> options = select_options(select);
    if (options.empty()) return g;
    const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
    const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
    const FontMetrics* m = control_metrics(ctx, b.style);
    const double line = m ? m->line_height(fs) : fs * kDefaultLineHeightFactor;
    g.row_height = std::ceil(line) + 6;   // a little air, as a native list has
    g.count = static_cast<int>(options.size());
    double x = 0, y = 0;
    visual_position(tree, select_box, &x, &y);
    // Below the control, its width, and as tall as it needs -- capped so a
    // hundred options do not run off the bottom of the world.
    const double height = std::min(g.row_height * g.count, 320.0);
    g.box = Rect(x, y + b.height, b.width, height);
    // Flipped above when there is no room below, the way a native list does.
    if (g.box.bottom() > ctx.viewport_height_px && y - height >= 0) {
        g.box.y = y - height;
    }
    g.visible = true;
    return g;
}

// The open list, painted after the tree so it covers what it opens over.
void paint_select_popup(const BoxTree& tree, const LayoutContext& ctx, const PaintContext& paint,
                        TextureHandle atlas_texture) {
    const Element* select = paint.popup.element;
    if (!select || !paint.backend) return;
    BoxId box = kNoBox;
    for (int i = 0; i < tree.size(); ++i) {
        if (tree[i].element == select && tree[i].kind == BoxKind::Block) {
            box = i;
            break;
        }
    }
    if (box == kNoBox) return;
    const SelectListGeometry g = select_list_geometry(tree, box, ctx, *select);
    if (!g.visible) return;
    const std::vector<const Element*> options = select_options(*select);

    // Nothing above it clips it: the list is drawn over whatever it opens on
    // top of, which is the whole point of a dropdown.
    paint.backend->set_scissor(nullptr);
    const Box& b = tree[box];
    const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
    const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
    const FontMetrics* m = control_metrics(ctx, b.style);
    const double ascent = m ? m->ascent(fs) : fs * 0.8;
    const FaceHandle face = paint.face;
    // The control's own colours, so a styled select gets a list that matches
    // rather than a white box in the middle of a dark page.
    LinearColor background = resolve_color(b.style, "background-color");
    if (background.a < 0.9f) background = LinearColor::from_srgb(255, 255, 255, 1.0f);
    const LinearColor text = resolve_color(b.style, "color");
    const LinearColor border = LinearColor(text.r, text.g, text.b, 0.35f);

    Mesh panel;
    tessellate_rect(g.box, background, &panel, false);
    draw_mesh(panel, paint.backend, {}, 1.0, nullptr, nullptr, nullptr);
    // A hairline border, so the list reads as being above the page.
    for (const Rect& edge : {Rect(g.box.x, g.box.y, g.box.width, 1),
                             Rect(g.box.x, g.box.bottom() - 1, g.box.width, 1),
                             Rect(g.box.x, g.box.y, 1, g.box.height),
                             Rect(g.box.right() - 1, g.box.y, 1, g.box.height)}) {
        Mesh line;
        tessellate_rect(edge, border, &line, false);
        draw_mesh(line, paint.backend, {}, 1.0, nullptr, nullptr, nullptr);
    }

    const int visible_rows = static_cast<int>(g.box.height / g.row_height);
    for (int i = 0; i < g.count && i < visible_rows; ++i) {
        const double row_y = g.box.y + i * g.row_height;
        if (i == paint.popup.highlighted) {
            Mesh band;
            tessellate_rect(Rect(g.box.x + 1, row_y, g.box.width - 2, g.row_height),
                            LinearColor::from_srgb(51, 144, 255, 0.85f), &band, false);
            draw_mesh(band, paint.backend, {}, 1.0, nullptr, nullptr, nullptr);
        }
        const std::string label = trimmed_text_of(*options[static_cast<size_t>(i)]);
        if (label.empty()) continue;
        Mesh glyphs;
        build_text_geometry(label, g.box.x + 6, row_y + (g.row_height - fs) * 0.5 + ascent, fs,
                            i == paint.popup.highlighted ? LinearColor::from_srgb(255, 255, 255, 1.f)
                                                         : text,
                            paint, &glyphs, letter_spacing_of(b.style, ctx, fs), &face);
        draw_mesh(glyphs, paint.backend, atlas_texture, 1.0, nullptr, nullptr, nullptr);
    }
}

void paint_tree(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                const PaintContext& paint) {
    if (!paint.backend || root == kNoBox) return;

    g_paint_profile = PaintProfile{};
    static const bool paint_log = std::getenv("WEVA_PAINT_LOG") != nullptr;
    g_paint_profile.on = paint_log;
    const auto pass_start = std::chrono::steady_clock::now();

    TextureHandle atlas_texture{};
    double glyphs_ms = 0;
    if (paint.atlas && paint.font) {
        const auto t0 = std::chrono::steady_clock::now();
        prepare_glyphs(tree, root, ctx, paint);
        prepare_popup_glyphs(tree, ctx, paint);
        atlas_texture = paint.atlas->texture(paint.backend);
        glyphs_ms =
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    }
    const BoxId canvas_owner = paint_canvas(tree, root, ctx, paint);
    paint_recursive(tree, root, ctx, 0, 0, paint, atlas_texture, canvas_owner, PaintState{});
    // Last, and over everything: an open dropdown is not in the box tree.
    paint_select_popup(tree, ctx, paint, atlas_texture);

    if (g_paint_profile.on) {
        const double total =
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - pass_start)
                .count();
        const PaintProfile& p = g_paint_profile;
        std::fprintf(stderr,
                     "    paint: glyphs %6.2f  shadows %6.2f (tess %5.2f draw %5.2f over %d "
                     "layers)  text %6.2f  backgrounds %6.2f  rest %6.2f  (total %6.2f ms)\n",
                     glyphs_ms, p.shadows, p.shadow_tess, p.shadow_draw, p.shadow_layers, p.text,
                     p.backgrounds, total - glyphs_ms - p.shadows - p.text - p.backgrounds, total);
    }
}

} // namespace weva
