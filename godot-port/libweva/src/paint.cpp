#include "weva/paint.h"
#include "weva/grapheme.h"
#include "weva/form_values.h"
#include "weva/form_state.h"
#include "weva/range_track.h"
#include "weva/box_builder.h"

#include "weva/background.h"
#include "weva/border_image.h"
#include "weva/block_layout.h"
#include "weva/css_value.h"
#include "weva/css_token.h"
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
        // TEMPORARY probe: which key is missing.
        if (std::getenv("WEVA_TEXKEY_LOG")) {
            std::fprintf(stderr, "  MISS %.140s\n", key.c_str());
        }
        return {};
    }
    it->second.used = true;
    ++hits_;
    return it->second.texture;
}

void TextureCache::put(const std::string& key, TextureHandle texture) {
    const auto old = entries_.find(key);
    if (old != entries_.end()) by_texture_.erase(old->second.texture.id);
    entries_[key] = Entry{texture, true};
    by_texture_[texture.id] = &entries_[key];
}

void TextureCache::retain(TextureHandle texture) {
    const auto it = by_texture_.find(texture.id);
    if (it != by_texture_.end()) it->second->used = true;
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
        by_texture_.erase(it->second.texture.id);
        it = entries_.erase(it);
    }
}

void TextureCache::release_all(RenderInterface* backend) {
    if (backend) {
        for (auto& kv : entries_) backend->release_texture(kv.second.texture);
    }
    entries_.clear();
    by_texture_.clear();
}

// A clip in force for a subtree: `clip-path`, or the rounded padding box of an
// `overflow: hidden` box. Chained through the parent so nested clips all
// apply; shared by the PaintState copies below rather than copied per box.
// Polygons are in SCREEN coordinates — mapped through the transform in force
// where the clip was pushed — because a descendant may add a transform of
// its own: a road rotated across a round map is clipped where it lands, not
// where it was laid out.
struct ClipNode {
    BoxId overflow_owner = kNoBox; // clip-path is never escaped by positioning.
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

struct OverflowClip {
    BoxId owner;
    Recti rect;
    double scroll_x, scroll_y;
    std::shared_ptr<const OverflowClip> parent;
};

LinearColor resolve_color(const ComputedStyle* style, std::string_view property);

bool PaintReplayInputs::operator==(const PaintReplayInputs& o) const {
    if (x != o.x || y != o.y || opacity != o.opacity || canvas_owner != o.canvas_owner ||
        transformed != o.transformed || (transformed && xform != o.xform) ||
        scissor.has_value() != o.scissor.has_value() || absolute_cb != o.absolute_cb || fixed_cb != o.fixed_cb ||
        blend != o.blend) return false;
    if (scissor && (scissor->x != o.scissor->x || scissor->y != o.scissor->y ||
                    scissor->width != o.scissor->width || scissor->height != o.scissor->height)) return false;
    const ClipNode* a = clip.get();
    const ClipNode* b = o.clip.get();
    while (a && b) {
        if (a == b) break;
        if (a->overflow_owner != b->overflow_owner || a->polygon.size() != b->polygon.size()) return false;
        for (size_t i = 0; i < a->polygon.size(); ++i)
            if (a->polygon[i].x != b->polygon[i].x || a->polygon[i].y != b->polygon[i].y) return false;
        a = a->parent.get();
        b = b->parent.get();
    }
    if (bool(a) != bool(b) || bool(filter) != bool(o.filter)) return false;
    const auto* oa = overflow.get();
    const auto* ob = o.overflow.get();
    while (oa && ob) {
        if (oa == ob) break;
        if (oa->owner != ob->owner || oa->rect.x != ob->rect.x || oa->rect.y != ob->rect.y ||
            oa->rect.width != ob->rect.width || oa->rect.height != ob->rect.height ||
            oa->scroll_x != ob->scroll_x || oa->scroll_y != ob->scroll_y) return false;
        oa = oa->parent.get(); ob = ob->parent.get();
    }
    if (bool(oa) != bool(ob)) return false;
    if (filter && filter != o.filter) {
        if (filter->alpha != o.filter->alpha) return false;
        for (int r = 0; r < 3; ++r) {
            if (filter->add[r] != o.filter->add[r]) return false;
            for (int c = 0; c < 3; ++c)
                if (filter->m[r][c] != o.filter->m[r][c]) return false;
        }
    }
    return true;
}

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

double radius_component(const CssValue* value, const LayoutContext& ctx, double font_size,
                        double basis) {
    const ResolvedLength r = resolve_length_value(value, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return std::max(0.0, r.pixels);
    if (r.kind == LengthKind::Percent) return std::max(0.0, basis * r.percent * 0.01);
    return 0;
}

// A corner radius may be one value (circular) or two (elliptical), and the two
// axes resolve against different basis lengths.
CornerRadius corner(const ComputedStyle* style, int property,
                    const LayoutContext& ctx, double font_size, double width, double height) {
    const std::string_view raw = style->get(property);
    // Do not populate a parse-cache slot for the overwhelmingly common zero.
    if (raw.empty() || raw == "0" || raw == "0px") return {};
    const CssValue* x = style->parsed(property);
    const CssValue* y = x;
    if (x && x->kind() == CssValueKind::List) {
        const auto& list = static_cast<const CssValueList&>(*x);
        if (list.separator != CssListSeparator::Space || list.items.size() != 2) return {};
        x = list.items[0].get();
        y = list.items[1].get();
    }
    // Cache syntax only. The same style can paint boxes of different sizes,
    // and percentages, font units and calc() must consume their current inputs.
    return CornerRadius(radius_component(x, ctx, font_size, width),
                        radius_component(y, ctx, font_size, height));
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

// WEVA_PAINT_LOG attributes a paint pass to its parts. Paint stayed the whole
// cost of an update after the texture cache landed, and a per-pass total does
// not say which part -- shadows, text or the backend -- to look at.
struct PaintProfile {
    double shadows = 0, text = 0, backgrounds = 0, other = 0;
    double shadow_tess = 0, shadow_draw = 0;
    int shadow_layers = 0;
    // The blurred-shadow texture, broken into its four steps. Charging the
    // whole of paint_blurred_box_shadow to `shadows` said glass spent 5 ms
    // there and nothing about which part, and reasoning about which part
    // guessed wrong twice.
    double shadow_raster = 0, shadow_blur = 0, shadow_punch = 0, shadow_upload = 0;
    int shadow_textures = 0;
    // Disjoint from backgrounds/shadows/text: filter:blur takes its own
    // background raster path. Its sub-scopes nest here, never in `rest`.
    double filters = 0, filter_raster = 0, filter_blur = 0, filter_upload = 0;
    int filter_textures = 0;
    // draw_mesh, the one path every draw goes through. Reported as "of which"
    // rather than subtracted from `rest`, because it NESTS inside the other
    // buckets -- a text draw is inside `text` AND inside this. Subtracting a
    // nested bucket would make the remainder a lie.
    double submit = 0;
    // Nested attribution, like submit: plain fills/borders do not run through
    // the layered-background scope, and color parsing occurs in many buckets.
    double decoration_build = 0, decoration_draw = 0, colors = 0;
    long submit_calls = 0;
    long submit_clipped = 0;
    int glyph_subtrees_reused = 0;
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
    ~ProfileScope() { close(); }
    // Ends the measurement early, for a region that finishes before its
    // enclosing block does.
    void close() {
        if (!slot) return;
        *slot += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0)
                     .count();
        slot = nullptr;
    }
};

// Consumes a temporary mesh. Callers must finish any geometry measurements
// before handing it over; retained draws own their buffers in the backend.
void draw_mesh(Mesh&& mesh, RenderInterface* backend, TextureHandle tex, double opacity = 1,
               const Transform2D* xform = nullptr, const ClipNode* clip = nullptr,
               const ColorFilter* filter = nullptr) {
    if (mesh.empty()) return;
    ProfileScope prof(&g_paint_profile.submit);
    if (g_paint_profile.on) {
        ++g_paint_profile.submit_calls;
        if (clip) ++g_paint_profile.submit_clipped;
    }
    // A colour filter rewrites the vertex colours: the whole story for solid
    // geometry and coverage text; textured draws had their texels filtered
    // where they were generated (see filter_rgba), and keep white vertices.
    if (filter) filter_vertices(&mesh.vertices, *filter);
    if (clip) {
        // Into screen space first, then geometric clipping, innermost clip
        // outwards; opacity last. The transformed path below is folded in
        // here so the polygons and the vertices meet in one space.
        Mesh& cur = mesh;
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
            static const bool clip_log = std::getenv("WEVA_CLIP_LOG") != nullptr;
            const auto clip_start = clip_log ? std::chrono::steady_clock::now() :
                                              std::chrono::steady_clock::time_point{};
            Mesh tmp;
            clip_triangles_polygon(cur.vertices, cur.indices, n->clip_shape(), &tmp);
            if (clip_log) {
                const double ms = std::chrono::duration<double,std::milli>(
                    std::chrono::steady_clock::now()-clip_start).count();
                std::fprintf(stderr,"clip: x %.3f y %.3f w %.3f h %.3f; %zu points; %zu -> %zu vertices; %zu triangles; %.4f ms\n",
                             n->min_x,n->min_y,n->max_x-n->min_x,n->max_y-n->min_y,
                             n->polygon.size(),cur.vertices.size(),tmp.vertices.size(),
                             tmp.indices.size()/3,ms);
            }
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
    if (opacity < 1 || xform) {
        for (Vertex& v : mesh.vertices) {
            if (opacity < 1) v.color.a *= static_cast<float>(std::max(0.0, opacity));
            if (xform) {
                double x = 0, y = 0;
                xform->apply(v.position.x, v.position.y, &x, &y);
                v.position = {static_cast<float>(x), static_cast<float>(y)};
            }
        }
    }
    backend->render_mesh(std::move(mesh.vertices), std::move(mesh.indices), {0, 0}, tex);
}

// Asks the backend to filter what it has already painted, inside `shape`.
//
// The shape goes through the same transform and the same clip stack a fill
// would, so a backdrop-filtered panel is confined by an ancestor's overflow
// exactly as its background is. Opacity is deliberately NOT folded in: it
// scales what the element paints, and the backdrop is not that.
void filter_backdrop(Mesh&& cur, RenderInterface* backend, const BackdropEffect& effect,
                     const Transform2D* xform = nullptr, const ClipNode* clip = nullptr) {
    if (cur.empty()) return;
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
        } else if ((n == "translate3d" && argc >= 2) || n == "translatez" || (n == "scale3d" && argc >= 2) ||
                   n == "scalez" || n == "rotatex" || n == "rotatey" || n == "rotatez" ||
                   (n == "rotate3d" && argc >= 4) || (n == "matrix3d" && argc >= 16) || n == "perspective") {
            // CSS Transforms 2 functions, projected onto the plane the way
            // Chrome shows them with no perspective: the z component and
            // depth are dropped, and a rotation about an in-plane axis
            // foreshortens by its cosine. perspective() itself is not affine
            // and stays a no-op, so a `translate3d(x, y, 0)` card still lands
            // where the author put it instead of not moving at all.
            const double kPi = 3.14159265358979323846;
            if (n == "translate3d") {
                t = Transform2D::translate(static_cast<float>(length(f.args[0], width)),
                                           static_cast<float>(length(f.args[1], height)));
            } else if (n == "scale3d") {
                t = Transform2D::scale(static_cast<float>(number(f.args[0], 1)),
                                       static_cast<float>(number(f.args[1], 1)));
            } else if (n == "rotatez" && argc >= 1) {
                t = Transform2D::rotate(angle(f.args[0]));
            } else if (n == "rotatex" && argc >= 1) {
                t = Transform2D::scale(1, static_cast<float>(std::cos(angle(f.args[0]) * kPi / 180)));
            } else if (n == "rotatey" && argc >= 1) {
                t = Transform2D::scale(static_cast<float>(std::cos(angle(f.args[0]) * kPi / 180)), 1);
            } else if (n == "rotate3d") {
                // Rodrigues' rotation about the unit axis, top-left 2x2 kept.
                double kx = number(f.args[0], 0), ky = number(f.args[1], 0), kz = number(f.args[2], 0);
                const double len = std::sqrt(kx * kx + ky * ky + kz * kz);
                if (len > 0) {
                    kx /= len; ky /= len; kz /= len;
                    const double rad = angle(f.args[3]) * kPi / 180;
                    const double c = std::cos(rad), sn = std::sin(rad), v = 1 - c;
                    t = Transform2D(static_cast<float>(c + kx * kx * v), static_cast<float>(ky * kx * v + kz * sn),
                                    static_cast<float>(kx * ky * v - kz * sn), static_cast<float>(c + ky * ky * v),
                                    0, 0);
                }
            } else if (n == "matrix3d") {
                // Column-major m11..m44; the affine 2D part is m11 m12 m21 m22
                // and the translation m41 m42.
                t = Transform2D(static_cast<float>(number(f.args[0], 1)), static_cast<float>(number(f.args[1], 0)),
                                static_cast<float>(number(f.args[4], 0)), static_cast<float>(number(f.args[5], 1)),
                                static_cast<float>(number(f.args[12], 0)), static_cast<float>(number(f.args[13], 0)));
            }
            // translateZ, scaleZ and perspective: identity.
        } else {
            continue;   // an unknown function: no effect
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
        ++g_paint_profile.shadow_textures;
        std::vector<uint8_t> rgba;
        {
            ProfileScope r(&g_paint_profile.shadow_raster);
            rasterize_background_padded({}, col, shape.width, shape.height, tex_w, tex_h, pad,
                                        &shape_radii, ctx, font_size, &rgba);
        }
        {
            ProfileScope b(&g_paint_profile.shadow_blur);
            blur_flat_rgba(&rgba, tex_w, tex_h, sigma * scale);
        }
        ProfileScope punch(&g_paint_profile.shadow_punch);

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

        punch.close();
        ProfileScope up(&g_paint_profile.shadow_upload);
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
    draw_mesh(std::move(mesh), paint.backend, tex, opacity, xf, clip, nullptr);
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
                draw_mesh(std::move(mesh), backend, {}, opacity, xf, clip, filter);
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
            // `e`, NOT `-e`, exactly as the outset loop above has it. A frame
            // of thickness `spread + e` reaches depth `e` past the spread, and
            // the Gaussian's value THERE is what the accumulated coverage
            // should reach -- faint for a deep frame, dense for a shallow one.
            //
            // Negated, the thickest frame came out fully dense on its first
            // iteration, every later frame failed the `target <= accumulated`
            // test below, and the whole blur collapsed to ONE hard-edged frame
            // a blur-radius thick. `inset 0 0 40px` drew a 40px rectangle with
            // a crisp inner edge instead of a soft falloff -- which is what the
            // two lines below already say it should not.
            const double target = sh.color.a * blurred_coverage(e, sigma);
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
            draw_mesh(std::move(mesh), backend, {}, opacity, xf, clip, filter);
        }
    }
}

bool clips_children(const ComputedStyle* style) {
    for (const char* prop : {"overflow-x", "overflow-y"}) {
        const std::string_view v = get(style, prop);
        if (v == "hidden" || v == "clip" || v == "auto" || v == "scroll") return true;
    }
    return false;
}


// Exact scalar encoding for cache inputs: rounding fractional sizes or filter
// values could alias textures that rasterize to different pixels.
template<class T> void append_texture_input(std::string& key, T value) {
    key.append(reinterpret_cast<const char*>(&value), sizeof(value));
}

void append_image_version(std::string& key, const PaintContext& paint) {
    append_texture_input(key, paint.images ? paint.images->content_version() : uint64_t{0});
}

// <img> uses the same rasterizer as layered backgrounds, but its inputs come
// from src/object-fit/object-position. Own the key; never retain image pointers.
std::string replaced_image_key(const BackgroundLayer& layer, const Rect& area,
                              int tex_w, int tex_h, const LayoutContext& ctx,
                              double font_size, const PaintContext& paint,
                              const ColorFilter* filter) {
    std::string key("img|");
    key.reserve(256 + layer.url.size());
    append_image_version(key, paint);
    const auto text = [&](std::string_view value) {
        append_texture_input(key, value.size());
        key.append(value);
    };
    text(layer.url); text(layer.pos_x); text(layer.pos_y);
    text(layer.size_x); text(layer.size_y);
    append_texture_input(key, area.width); append_texture_input(key, area.height);
    append_texture_input(key, tex_w); append_texture_input(key, tex_h);
    append_texture_input(key, font_size);
    append_texture_input(key, ctx.root_font_size_px);
    append_texture_input(key, ctx.root_line_height_px);
    append_texture_input(key, ctx.viewport_width_px);
    append_texture_input(key, ctx.viewport_height_px);
    append_texture_input(key, ctx.dpi_pixels_per_inch);
    append_texture_input(key, filter != nullptr);
    if (filter) {
        for (int r=0; r<3; ++r) {
            for (int c=0; c<3; ++c) append_texture_input(key, filter->m[r][c]);
            append_texture_input(key, filter->add[r]);
        }
        append_texture_input(key, filter->alpha);
    }
    return key;
}

// Everything that decides a rasterized layer's pixels, as a string. Cheap
// beside the rasterization it avoids -- which is a texel per pixel of the box,
// and viewport-sized for the canvas.
std::string background_key(const ComputedStyle* style, const LinearColor& color, double w,
                           double h, const BorderRadii& radii, double font_size, double blur,
                           const ColorFilter* filter,
                           const std::vector<BackgroundLayer>* layers, int tex_w, int tex_h) {
    std::string k;
    k.reserve(128);
    const auto num = [&](double v) {
        char buf[32];
        std::snprintf(buf, sizeof(buf), "%.3f;", v);
        k += buf;
    };
    // The texture's own resolution is always part of the key: two boxes that
    // rasterize to different texel grids are different textures whatever else
    // they share. Above 1024 it saturates, which is exactly where a texture is
    // expensive enough for the sharing to matter.
    num(tex_w);
    num(tex_h);
    // The box's SIZE is part of it only when the picture depends on the size.
    //
    // A gradient written entirely in percentages resolves to the same texels at
    // any box size, so carrying the width and height here turned every resize
    // into a full re-rasterization for nothing. A blurred background is the
    // exception: its padding and its corner coverage are in absolute pixels.
    const bool size_free =
        blur == 0 && layers && !layers->empty() && background_size_independent(*layers);
    if (!size_free) {
        num(w);
        num(h);
    }
    num(font_size);
    num(blur);
    num(color.r);
    num(color.g);
    num(color.b);
    num(color.a);
    // Only the blurred path rasterizes the corners into the texture; the plain
    // one gets its coverage from the mesh and never passes the radii to the
    // rasterizer at all, so keying on them there is a miss for a difference the
    // texels cannot have.
    if (blur > 0) {
        const CornerRadius corners[4] = {radii.top_left, radii.top_right, radii.bottom_right,
                                         radii.bottom_left};
        for (const CornerRadius& c : corners) {
            num(c.x_radius);
            num(c.y_radius);
        }
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
    std::shared_ptr<const OverflowClip> overflow;
    BoxId absolute_cb = kNoBox, fixed_cb = kNoBox;
    // `mix-blend-mode` in effect: an element's mode applies to every draw of
    // its subtree, which is the group-blend of CSS Compositing 1 §6 to the
    // extent per-draw blending can express it (exact where the subtree's
    // draws do not overlap each other).
    BlendMode blend = BlendMode::Normal;
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
        } else if (name == "hue-rotate") {
            // Filter Effects 1 §8.1 hue-rotate: the angle in deg, grad, rad or
            // turn (a bare number reads as degrees); the matrix is the spec's,
            // built on the same luminance weights as saturate.
            const auto strip = [](std::string_view t) {
                while (!t.empty() && (t.front() == ' ' || t.front() == '\t' || t.front() == '\n')) t.remove_prefix(1);
                while (!t.empty() && (t.back() == ' ' || t.back() == '\t' || t.back() == '\n')) t.remove_suffix(1);
                return t;
            };
            std::string a(strip(arg));
            double degrees = 0;
            if (!a.empty()) {
                char* end = nullptr;
                const double v = std::strtod(a.c_str(), &end);
                if (end != a.c_str()) {
                    const std::string_view unit = strip(std::string_view(a).substr(static_cast<size_t>(end - a.c_str())));
                    degrees = ci_equal(unit, "rad") ? v * 180.0 / 3.14159265358979323846
                            : ci_equal(unit, "grad") ? v * 0.9
                            : ci_equal(unit, "turn") ? v * 360.0
                            : v;
                }
            }
            const double t = degrees * 3.14159265358979323846 / 180.0;
            const float c = static_cast<float>(std::cos(t)), sn = static_cast<float>(std::sin(t));
            f.m[0][0] = 0.213f + c * 0.787f - sn * 0.213f; f.m[0][1] = 0.715f - c * 0.715f - sn * 0.715f; f.m[0][2] = 0.072f - c * 0.072f + sn * 0.928f;
            f.m[1][0] = 0.213f - c * 0.213f + sn * 0.143f; f.m[1][1] = 0.715f + c * 0.285f + sn * 0.140f; f.m[1][2] = 0.072f - c * 0.072f - sn * 0.283f;
            f.m[2][0] = 0.213f - c * 0.213f - sn * 0.787f; f.m[2][1] = 0.715f - c * 0.715f + sn * 0.715f; f.m[2][2] = 0.072f + c * 0.928f + sn * 0.072f;
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
void push_clip(PaintState* state, std::vector<ClipPoint> polygon, BoxId overflow_owner = kNoBox) {
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
    n->overflow_owner = overflow_owner;
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
// Use the same registered family for layout and paint. A metrics-only family
// or a face from another backend keeps the document's rendering fallback.
FaceHandle family_face(const ComputedStyle* style, const LayoutContext& ctx,
                       const PaintContext& paint) {
    if (ctx.fonts.empty()) return paint.face;
    const auto* metrics = ctx.font_for(get(style, "font-family"));
    const auto face = metrics ? metrics->rendering_face(paint.font) : FaceHandle{};
    return face.id ? face : paint.face;
}
FaceHandle face_for_run(const Box& b, const LayoutContext& ctx, const PaintContext& paint) {
    const auto face = family_face(b.style, ctx, paint);
    if (!paint.font) return face;
    const int weight = resolve_font_weight(b.style);
    const bool italic = resolve_font_italic(b.style);
    if (weight < 600 && !italic) return face;
    return paint.font->variant(face, weight, italic);
}

// A thick line as one quad: the segment's direction, its normal, and the four
// corners half a thickness either side. The engine tessellates rectangles and
// rounded rectangles and nothing else, and a checkmark is two strokes at an
// angle.
void stroke_segment(const Vec2& from, const Vec2& to, double thickness,
                    const LinearColor& color, Mesh* out) {
    const double dx = to.x - from.x, dy = to.y - from.y;
    const double length = std::sqrt(dx * dx + dy * dy);
    if (length <= 0 || thickness <= 0) return;
    const double nx = -dy / length * thickness * 0.5;
    const double ny = dx / length * thickness * 0.5;
    // Extended half a thickness at each end, so the two strokes of a tick meet
    // in a filled corner rather than a notch.
    const double ex = dx / length * thickness * 0.5;
    const double ey = dy / length * thickness * 0.5;
    const uint32_t base = static_cast<uint32_t>(out->vertices.size());
    const auto at = [](double px, double py) {
        return Vec2{static_cast<float>(px), static_cast<float>(py)};
    };
    const Vec2 corners[4] = {at(from.x + nx - ex, from.y + ny - ey),
                             at(to.x + nx + ex, to.y + ny + ey),
                             at(to.x - nx + ex, to.y - ny + ey),
                             at(from.x - nx - ex, from.y - ny - ey)};
    for (const Vec2& p : corners) out->vertices.push_back(Vertex{p, color, {0, 0}});
    for (uint32_t i : {0u, 1u, 2u, 0u, 2u, 3u}) out->indices.push_back(base + i);
}

// White on a dark accent, near-black on a light one -- so a tick stays visible
// whatever `accent-color` the page chose.
LinearColor tick_color_on(const LinearColor& accent) {
    const double luminance = 0.2126 * accent.r + 0.7152 * accent.g + 0.0722 * accent.b;
    return luminance > 0.35 ? LinearColor::from_srgb(20, 22, 26, 1.0f)
                            : LinearColor::from_srgb(255, 255, 255, 1.0f);
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
    return std::string(form_input_type(e));
}

// Selectedness is shared by painting, selectors and the control API.
const Element* selected_option(const Element& select) {
    for (const auto* option : form_options(select)) if (option->form_selected()) return option;
    return nullptr;
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
    std::string_view source;
    std::vector<size_t> source_boundaries;
    bool password = false;
    bool placeholder = false;   // painted faded
    bool centered = true;       // vertically, in the content box (single-line controls)

    size_t display_offset(size_t offset) const {
        if (!password) return std::min(offset, text.size());
        const auto at = std::upper_bound(source_boundaries.begin(), source_boundaries.end(), offset);
        return static_cast<size_t>(at - source_boundaries.begin() - 1) * 3; // U+2022 is three UTF-8 bytes.
    }
    size_t source_offset(size_t offset) const {
        if (!password) return std::min(offset, text.size());
        return source_boundaries[std::min(offset / 3, source_boundaries.size() - 1)];
    }
};

// Carets and selection edges follow advances, including spaces and glyphs
// without bitmaps. Ink bounds exclude those advances and can overhang them.
double text_advance(std::string_view text, double fs, const PaintContext& paint,
                    const FaceHandle& face, double spacing) {
    if (!paint.font || text.empty()) return 0;
    std::vector<ShapedGlyph> glyphs;
    paint.font->shape(face, text, fs, &glyphs);
    double advance = 0;
    for (const ShapedGlyph& glyph : glyphs) advance += glyph.x_advance + spacing;
    return advance;
}

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
        const std::string_view value = e.form_edit_value();
        out->source = value;
        if (type == "submit" || type == "button" || type == "reset") {
            out->text = !value.empty() ? std::string(value)
                        : type == "submit" ? "Submit" : type == "reset" ? "Reset" : "";
            return !out->text.empty();
        }
        if (!value.empty()) {
            if (type == "password") {
                out->password = true;
                out->text.clear();
                out->source_boundaries = {0};
                Graphemes clusters(value);
                size_t end = 0;
                while (clusters.next(&end)) {
                    out->source_boundaries.push_back(end);
                    out->text += "\xE2\x80\xA2";
                }
            } else {
                out->text = std::string(value);
            }
            return true;
        }
        const std::string_view ph = e.get_attribute("placeholder");
        if (ph.empty()) return true; // Empty editable fields still paint their caret.
        out->text = std::string(ph);
        out->placeholder = true;
        return true;
    }
    if (tag == "select") {
        if (select_is_listbox(e)) return false;
        const Element* opt = selected_option(e);
        if (!opt) return false;
        out->text = option_label(*opt);
        return !out->text.empty();
    }
    if (tag == "textarea") {
        out->centered = false;
        if (!e.form_edit_value().empty()) return false;
        const std::string_view ph = e.get_attribute("placeholder");
        if (ph.empty()) return false;
        out->text = std::string(ph);
        out->placeholder = true;
        return true;
    }
    return false;
}

bool is_form_control(const Box& b) {
    if (!b.element || b.kind != BoxKind::Block) return false;
    const std::string_view tag = b.element->tag_name();
    return tag == "input" || tag == "select" || tag == "textarea";
}

double control_text_offset(const Box& b, bool centered, double content_height, const FontMetrics& metrics, double fs) {
    if (!centered) return 0;
    const double offset = metrics.leading_above(content_height, fs);
    // Short text fields keep their line centered; buttons and selects retain
    // their non-negative content offset.
    return b.element && form_is_text_entry(*b.element) ? offset : std::max(0.0, offset);
}

// ---- text decorations (CSS Text Decoration L4) ---------------------------
//
// The UA stylesheet has asked for `text-decoration: underline` on <a> and <u>
// since the beginning and nothing drew it, so every link in every document was
// plain. This is the fourth UA rule this audit has found styling something the
// port never created.

enum DecorationFlags {
    kNoDecoration = 0,
    kUnderline = 1 << 0,
    kOverline = 1 << 1,
    kLineThrough = 1 << 2,
};

int decoration_flags_of(const ComputedStyle* style) {
    if (!style) return kNoDecoration;
    // The longhand first; the shorthand's other components (style, colour,
    // thickness) are read separately, and either may carry the line.
    std::string raw(get(style, "text-decoration-line"));
    if (raw.empty() || raw == "none") raw = std::string(get(style, "text-decoration"));
    if (raw.empty() || raw == "none") return kNoDecoration;
    int flags = kNoDecoration;
    if (raw.find("underline") != std::string::npos) flags |= kUnderline;
    if (raw.find("overline") != std::string::npos) flags |= kOverline;
    if (raw.find("line-through") != std::string::npos) flags |= kLineThrough;
    return flags;
}

// Solid, and everything else drawn as solid for now: a dashed or wavy rule is
// a pattern of rects, and getting the line there at all is the difference
// between a link that looks like one and a link that does not.
void paint_text_decorations(int flags, double x, double baseline, double width, double ascent,
                            double font_size, const ComputedStyle* style,
                            const LayoutContext& ctx, const LinearColor& text_color,
                            const PaintContext& paint, double opacity,
                            const Transform2D* xf, const ClipNode* clip,
                            const ColorFilter* filter) {
    if (flags == kNoDecoration || width <= 0) return;
    // CSS Text Decoration L4 3.2: `auto` is the font's own, and this is the
    // reference's fallback for a face that does not say.
    double thickness = std::max(1.0, ascent / 12.0);
    const std::string_view declared = get(style, "text-decoration-thickness");
    if (!declared.empty() && declared != "auto" && declared != "from-font") {
        const ResolvedLength r = resolve_length(declared, ctx, font_size, font_size);
        if (r.kind == LengthKind::Length && r.pixels > 0) thickness = r.pixels;
    }
    LinearColor color = text_color;
    const std::string_view decl_color = get(style, "text-decoration-color");
    if (!decl_color.empty() && decl_color != "currentcolor" && decl_color != "currentColor") {
        color = resolve_color(style, "text-decoration-color");
    }
    double extra = 0;
    const std::string_view offset = get(style, "text-underline-offset");
    if (!offset.empty() && offset != "auto") {
        const ResolvedLength r = resolve_length(offset, ctx, font_size, font_size);
        if (r.kind == LengthKind::Length) extra = std::max(0.0, r.pixels);
    }

    // `text-decoration-style`. The shapes are the reference's, so a rule set
    // by one engine and drawn by the other has the same dashes in the same
    // places -- and `wavy` is drawn as dashed by both, since a sine needs a
    // curve primitive neither renderer has.
    const std::string_view deco_style = get(style, "text-decoration-style");
    const auto segment = [&](double sx, double sy, double sw) {
        if (sw <= 0) return;
        Mesh m;
        tessellate_rect(Rect(sx, sy, sw, thickness), color, &m, false);
        draw_mesh(std::move(m), paint.backend, {}, opacity, xf, clip, filter);
    };
    const auto line = [&](double y) {
        if (deco_style == "double") {
            // Two parallel rules, a gap of one and a half thicknesses apart.
            segment(x, y, width);
            segment(x, y + thickness * 1.5, width);
            return;
        }
        if (deco_style == "dotted" || deco_style == "dashed" || deco_style == "wavy") {
            // Dots are one thickness with a gap of one; dashes are three with
            // a gap of three. The last one is cut to the run rather than
            // overrunning it.
            const double dot = deco_style == "dotted" ? std::max(1.0, thickness)
                                                      : std::max(2.0, thickness * 3.0);
            const double period = dot * 2.0;
            for (double cursor = x; cursor < x + width; cursor += period) {
                segment(cursor, y, std::min(dot, x + width - cursor));
            }
            return;
        }
        segment(x, y, width);
    };
    // The same three positions the reference uses, so a document set by one
    // engine and rendered by the other has its rules in the same places.
    if (flags & kUnderline) line(baseline + ascent / 8.0 + extra);
    if (flags & kOverline) line(baseline - ascent);
    if (flags & kLineThrough) line(baseline - ascent * 0.4);
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

// A paint-time pseudo-element style (::placeholder, ::selection) of a text
// field, or null when no author rule targets it on that host.
const ComputedStyle* pseudo_style_of(const PaintContext& paint, const Element* host,
                                     std::string_view name) {
    if (!paint.styles || !host) return nullptr;
    return paint.styles->pseudo_style_of(*host, name);
}

// The ::selection band: the pseudo's own `background-color` when an author
// gave one that is not transparent, else the UA's blue at an alpha that
// leaves the glyphs readable (the band goes BEHIND them).
LinearColor selection_band_color(const PaintContext& paint, const Element* host) {
    const LinearColor ua = LinearColor::from_srgb(51, 144, 255, 0.45f);
    const ComputedStyle* ps = pseudo_style_of(paint, host, "selection");
    if (!ps || !ps->contains_own("background-color")) return ua;
    const std::string_view raw = ps->get("background-color");
    if (raw.empty() || ci_equal(raw, "transparent")) return ua;
    const LinearColor c = resolve_color(ps, "background-color");
    return c.a > 0 ? c : ua;
}

// The placeholder's colour: the ::placeholder rule's own `color` (with its
// `opacity`, as Chrome's UA sheet fades it that way), else the host's text
// colour at half strength.
LinearColor placeholder_color(const PaintContext& paint, const Element* host,
                              const LinearColor& host_color) {
    const ComputedStyle* ps = pseudo_style_of(paint, host, "placeholder");
    if (!ps) return LinearColor(host_color.r * 0.5f, host_color.g * 0.5f, host_color.b * 0.5f, host_color.a * 0.5f);
    LinearColor c = host_color;
    bool styled = false;
    if (ps->contains_own("color")) {
        const std::string_view raw = ps->get("color");
        if (!raw.empty() && !ci_equal(raw, "currentcolor")) { c = resolve_color(ps, "color"); styled = true; }
        else styled = true;   // currentcolor: the host's colour, at full strength
    }
    if (ps->contains_own("opacity")) {
        const std::string t(ps->get("opacity"));
        char* end = nullptr;
        const double o = std::strtod(t.c_str(), &end);
        if (end != t.c_str()) { c.a *= static_cast<float>(std::clamp(o, 0.0, 1.0)); styled = true; }
    }
    if (!styled) return LinearColor(host_color.r * 0.5f, host_color.g * 0.5f, host_color.b * 0.5f, host_color.a * 0.5f);
    return c;
}

// CSS Compositing 1 §6.1 `mix-blend-mode`, in the specification's order.
BlendMode blend_mode_of(std::string_view raw) {
    static const char* kNames[] = {"normal", "multiply", "screen", "overlay", "darken", "lighten",
                                   "color-dodge", "color-burn", "hard-light", "soft-light",
                                   "difference", "exclusion", "hue", "saturation", "color", "luminosity"};
    for (size_t i = 0; i < sizeof(kNames) / sizeof(kNames[0]); ++i) {
        if (ci_equal(raw, kNames[i])) return static_cast<BlendMode>(i);
    }
    return BlendMode::Normal;
}

// CSS UI 4 §5.4 caret-color; `auto` is currentcolor, as Chrome draws it (the
// contrast adjustment the spec permits is not done). `transparent` is a
// legitimate value -- a page that draws its own cursor hides the UA's.
LinearColor caret_color_of(const ComputedStyle* style) {
    const std::string_view raw = get(style, "caret-color");
    if (raw.empty() || ci_equal(raw, "auto") || ci_equal(raw, "currentcolor")) {
        return resolve_color(style, "color");
    }
    return resolve_color(style, "caret-color");
}

void fill_rounded(const Rect& r, double radius, const LinearColor& color, RenderInterface* backend,
                  double opacity, const Transform2D* xf, const ClipNode* clip = nullptr,
                  const ColorFilter* filter = nullptr) {
    if (r.width <= 0 || r.height <= 0 || !backend) return;
    Mesh mesh;
    const double rr = std::min(radius, std::min(r.width, r.height) * 0.5);
    const CornerRadius cr(rr);
    tessellate_rounded_rect(r, BorderRadii(cr, cr, cr, cr), color, &mesh);
    draw_mesh(std::move(mesh), backend, {}, opacity, xf, clip, filter);
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
    const bool skip_contents = b.style && b.style->get("content-visibility") == "hidden";
    const double cl = x + b.border_left + b.padding_left;
    const double ct = y + b.border_top + b.padding_top;
    const double cw = b.width - b.border_left - b.border_right - b.padding_left - b.padding_right;
    const double ch = b.height - b.border_top - b.border_bottom - b.padding_top - b.padding_bottom;
    if (tag == "input") {
        const std::string type = input_type(e);
        if (type == "checkbox") {
            if (!e.form_checked()) return;
            // Chrome fills the whole box with the accent colour and puts a
            // white tick on it. An accent-coloured square with nothing in it
            // reads as "some state", not as "checked" -- at 13px there is
            // nothing to tell it from an unchecked box that happens to be
            // filled, which is what this looked like before.
            const LinearColor accent = accent_color_of(b.style);
            fill_rounded(Rect(x, y, b.width, b.height), 2, accent, paint.backend, state.opacity,
                         xf, state.clip.get(), state.filter.get());
            const double w = b.width, h = b.height;
            const double thickness = std::max(1.5, w * 0.12);
            // Two strokes, in the proportions a checkmark is drawn in: down to
            // the low point, then up and out past it.
            const auto point = [](double px, double py) {
                return Vec2{static_cast<float>(px), static_cast<float>(py)};
            };
            const Vec2 a = point(x + w * 0.24, y + h * 0.52);
            const Vec2 mid = point(x + w * 0.42, y + h * 0.70);
            const Vec2 c = point(x + w * 0.76, y + h * 0.30);
            Mesh tick;
            const LinearColor ink = tick_color_on(accent);
            stroke_segment(a, mid, thickness, ink, &tick);
            stroke_segment(mid, c, thickness, ink, &tick);
            draw_mesh(std::move(tick), paint.backend, {}, state.opacity, xf, state.clip.get(),
                      state.filter.get());
            return;
        }
        if (type == "radio") {
            if (!e.form_checked()) return;
            const double inset = b.width * 0.25;
            const double d = b.width - 2 * inset;
            fill_rounded(Rect(x + inset, y + inset, d, d), d * 0.5, accent_color_of(b.style),
                         paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
            return;
        }
        if (type == "range") {
            const RangeValue range(e);
            double frac = range.max > range.min ? (range.value - range.min) / (range.max - range.min) : 0;
            if (!(frac >= 0)) frac = 0;
            if (frac > 1) frac = 1;
            const RangeTrack track(b, x, y);
            if (track.length <= 0 || track.cross <= 0) return;
            if (track.reversed) frac = 1 - frac;
            const LinearColor accent = accent_color_of(b.style);
            const double thumb = track.thumb;
            const double centre = thumb * .5 + frac * track.travel;
            const double cx = track.vertical ? cl + track.cross * .5 : cl + centre;
            const double cy = track.vertical ? ct + centre : ct + track.cross * .5;
            const auto rail_rect = [&](double start, double length) {
                return track.vertical
                    ? Rect(cl + (track.cross - track.rail) * .5, ct + start, track.rail, length)
                    : Rect(cl + start, ct + (track.cross - track.rail) * .5, length, track.rail);
            };
            LinearColor groove = accent;
            groove.a *= 0.3f;
            fill_rounded(rail_rect(0, track.length), track.rail * 0.5, groove, paint.backend,
                         state.opacity, xf, state.clip.get(), state.filter.get());
            const double fill_start = track.reversed ? centre : 0;
            const double fill_length = track.reversed ? track.length - centre : centre;
            if (fill_length > 0) {
                fill_rounded(rail_rect(fill_start, fill_length), track.rail * 0.5, accent, paint.backend,
                             state.opacity, xf, state.clip.get(), state.filter.get());
            }
            if (!skip_contents)
                fill_rounded(Rect(cx - thumb * 0.5, cy - thumb * 0.5, thumb, thumb), thumb * 0.5, accent,
                             paint.backend, state.opacity, xf, state.clip.get(), state.filter.get());
            return;
        }
    }

    ControlText t;
    const bool has_text = !skip_contents && form_control_text(b, &t);
    if (has_text && paint.font && paint.atlas && cw > 0 && ch > 0) {
        const FaceHandle face = face_for_run(b, ctx, paint);
        FontInterfaceMetrics default_metrics(paint.font, face);
        const FontMetrics* m = control_metrics(ctx, b.style);
        if (!m) m = &default_metrics;
        const double ascent = m->ascent(fs);
        const double line_h = m->line_height(fs);
        const double text_top = ct + control_text_offset(b, t.centered, ch, *m, fs);
        const double baseline = text_top + ascent;
        LinearColor color = resolve_color(b.style, "color");
        if (t.placeholder) color = placeholder_color(paint, &e, color);
        // Clip the overlay to its content box. A transformed input needs
        // the transformed polygon, including its caret and selection.
        PaintState text_state = state;
        if (xf) push_clip(&text_state, {{cl, ct}, {cl + cw, ct}, {cl + cw, ct + ch}, {cl, ct + ch}});
        const ClipNode* text_clip = text_state.clip.get();
        Recti clip{static_cast<int>(std::floor(cl)), static_cast<int>(std::floor(ct)),
                   static_cast<int>(std::ceil(cl + cw)) - static_cast<int>(std::floor(cl)),
                   static_cast<int>(std::ceil(ct + ch)) - static_cast<int>(std::floor(ct))};
        if (xf) {
            clip = {static_cast<int>(std::floor(text_clip->min_x)), static_cast<int>(std::floor(text_clip->min_y)),
                    static_cast<int>(std::ceil(text_clip->max_x)) - static_cast<int>(std::floor(text_clip->min_x)),
                    static_cast<int>(std::ceil(text_clip->max_y)) - static_cast<int>(std::floor(text_clip->min_y))};
        }
        if (state.scissor) clip = intersect(*state.scissor, clip);
        paint.backend->set_scissor(&clip);
        // The selection band, behind the glyphs. An <input> draws its own text,
        // so the range indexes it directly -- there are no runs to map through.
        const double spacing = letter_spacing_of(b.style, ctx, fs);
        const double text_left = cl - (paint.caret.element == &e && !t.placeholder ? paint.caret.text_scroll_x : 0);
        const auto advance_to = [&](size_t offset) {
            return text_advance(std::string_view(t.text).substr(0, t.display_offset(offset)), fs, paint, face, spacing);
        };
        if (!t.placeholder && paint.caret.element == &e &&
            paint.caret.selection_to > paint.caret.selection_from) {
            const size_t from = paint.caret.selection_from;
            const size_t to = paint.caret.selection_to;
            if (to > from) {
                const double start = advance_to(from), end = advance_to(to);
                if (end > start) {
                    Mesh band;
                    tessellate_rect(Rect(text_left + start, text_top, end - start, line_h),
                                    selection_band_color(paint, &e), &band, false);
                    draw_mesh(std::move(band), paint.backend, {}, state.opacity, xf, text_clip,
                              state.filter.get());
                }
            }
        }
        Mesh text;
        build_text_geometry(t.text, text_left, baseline, fs, color, paint, &text, spacing, &face);
        const int deco = decoration_flags_of(b.style);
        double run_w = 0;
        if (deco) {
            for (const Vertex& v : text.vertices)
                run_w = std::max<double>(run_w, v.position.x - text_left);
        }
        draw_mesh(std::move(text), paint.backend, atlas_texture, state.opacity, xf, text_clip, state.filter.get());
        if (!t.placeholder && paint.caret.element == &e &&
            paint.caret.composition_to > paint.caret.composition_from) {
            const double from = advance_to(paint.caret.composition_from);
            const double to = advance_to(paint.caret.composition_to);
            Mesh underline;
            tessellate_rect(Rect(text_left + from, text_top + line_h - 1, to - from, 1),
                            color, &underline, false);
            draw_mesh(std::move(underline), paint.backend, {}, state.opacity, xf, text_clip, state.filter.get());
        }
        if (deco) {
            paint_text_decorations(deco, text_left, baseline, run_w, ascent, fs, b.style, ctx, color, paint,
                                   state.opacity, xf, text_clip, state.filter.get());
        }

        // The caret, at the character the cursor sits before. Its x is the
        // width of the text up to that point, measured the same way the run
        // was laid out -- anything else and the bar drifts from the glyphs as
        // the value grows.
        if (paint.caret.visible && paint.caret.element == &e) {
            const double advance = t.placeholder ? 0 : advance_to(static_cast<size_t>(std::max(0, paint.caret.index)));
            // An empty prefix measures nothing, which is the left edge.
            const double caret_x = text_left + advance;
            Mesh bar;
            tessellate_rect(Rect(caret_x, text_top, 1.0, line_h), caret_color_of(b.style), &bar, false);
            draw_mesh(std::move(bar), paint.backend, {}, state.opacity, xf, text_clip,
                      state.filter.get());
        }
        paint.backend->set_scissor(state.scissor ? &*state.scissor : nullptr);
    }
    if (tag == "select" && !select_is_listbox(e)) {
        // The mark that says a list drops out of this: a triangle pointing
        // down, as every platform draws it. A grey dash -- which is what the
        // runtime's v1 drew and this inherited -- says nothing at all, and now
        // that the list really does open, the control should look like it.
        //
        // It takes the control's own text colour at three-quarter weight, so a
        // dark select gets a light arrow rather than a grey smudge.
        const double margin = 9, w = 9, h = 5;
        const double ax = x + b.width - margin - w, ay = y + (b.height - h) * 0.5;
        LinearColor ink = resolve_color(b.style, "color");
        if (ink.a <= 0) ink = LinearColor(0.6f, 0.6f, 0.6f, 1.f);
        ink.a *= 0.75f;
        const auto point = [](double px, double py) {
            return Vec2{static_cast<float>(px), static_cast<float>(py)};
        };
        Mesh arrow;
        arrow.vertices.push_back(Vertex{point(ax, ay), ink, {0, 0}});
        arrow.vertices.push_back(Vertex{point(ax + w, ay), ink, {0, 0}});
        arrow.vertices.push_back(Vertex{point(ax + w * 0.5, ay + h), ink, {0, 0}});
        arrow.indices = {0, 1, 2};
        draw_mesh(std::move(arrow), paint.backend, {}, state.opacity, xf, state.clip.get(),
                  state.filter.get());
    }
}

void prepare_glyphs(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                    const PaintContext& paint) {
    static const bool disable_reuse = std::getenv("WEVA_DISABLE_GLYPH_REUSE") != nullptr;
    if (!disable_reuse && paint.reuse && paint.reuse->reuse_glyphs(id)) {
        if (g_paint_profile.on) ++g_paint_profile.glyph_subtrees_reused;
        return;
    }
    const Box& b = tree[id];
    if (b.kind == BoxKind::Text && !b.preserved_tab && !b.text.empty() && paint.font && paint.atlas) {
        const FaceHandle face = face_for_run(b, ctx, paint);
        std::vector<ShapedGlyph> glyphs;
        paint.font->shape(face, b.text, b.font_size, &glyphs);
        for (const ShapedGlyph& g : glyphs) {
            paint.atlas->get(paint.font, face, g.glyph, b.font_size);
        }
    }
    // Control overlays draw text no box carries; their glyphs go into the
    // same single up-front upload.
    ControlText t;
    if (paint.font && paint.atlas && is_form_control(b) &&
        (!b.style || b.style->get("content-visibility") != "hidden") && form_control_text(b, &t)) {
        const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
        const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
        const FaceHandle face = face_for_run(b, ctx, paint);
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
struct PopupTextStyle {
    const ComputedStyle* style;
    FaceHandle face;
    double size;
};
PopupTextStyle popup_text_style(const Element& row, const Box& select,
                                double select_size, const LayoutContext& ctx, const PaintContext& paint) {
    const auto* style = paint.styles ? paint.styles->style_of(row) : select.style;
    const auto* parent = row.parent() && row.parent()->is_element() && paint.styles
        ? paint.styles->style_of(static_cast<const Element&>(*row.parent())) : select.style;
    const int weight = paint.styles ? resolve_font_weight(style) : row.tag_name() == "optgroup" ? 700 : resolve_font_weight(style);
    const auto base_face = family_face(style, ctx, paint);
    const auto face = paint.font ? paint.font->variant(base_face, weight, resolve_font_italic(style)) : base_face;
    return {style, face, paint.styles && style ? font_size_px(style, parent, ctx) : select_size};
}
void prepare_popup_glyphs(const BoxTree& tree, const LayoutContext& ctx,
                          const PaintContext& paint) {
    const Element* select = paint.popup.element;
    if (!select || !paint.font || !paint.atlas) return;
    for (int i = 0; i < tree.size(); ++i) {
        const Box& b = tree[i];
        if (b.element != select || b.kind != BoxKind::Block) continue;
        const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
        const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
        for (const Element* option : select_rows(*select)) {
            std::vector<ShapedGlyph> glyphs;
            const bool group = option->tag_name() == "optgroup";
            const auto text = popup_text_style(*option, b, fs, ctx, paint);
            const auto label = group ? std::string(option->get_attribute("label")) : option_label(*option);
            paint.font->shape(text.face, label, text.size, &glyphs);
            for (const ShapedGlyph& g : glyphs) paint.atlas->get(paint.font, text.face, g.glyph, text.size);
        }
        return;
    }
}

// Whether anything here needs a texture rasterized. Named for what it asks
// rather than for gradients: an image layer needs one just as much, and while
// this only counted gradients a `background-image: url(...)` took the plain
// colour path and never reached the rasterizer at all.
bool has_paintable_layer(const std::vector<BackgroundLayer>& layers) {
    for (const BackgroundLayer& l : layers) {
        if (l.is_gradient || l.image) return true;
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
                              const ComputedStyle* style = nullptr,
                              const BackgroundLayer* replaced = nullptr) {
    if (!has_paintable_layer(layers) || !paint.backend || area.width <= 0 || area.height <= 0) {
        return false;
    }
    ProfileScope prof(&g_paint_profile.backgrounds);
    // A texel per pixel, capped -- except for a background with no
    // discontinuity in it, which the bilinear filter reconstructs from far
    // fewer. WEVA_SMOOTH_TEXELS overrides the cap, for calibrating it.
    static const int smooth_cap = [] {
        const char* e = std::getenv("WEVA_SMOOTH_TEXELS");
        return e ? std::atoi(e) : kSmoothGradientTexels;
    }();
    const int detail = background_texture_detail(layers) ? smooth_cap : 0;
    const double cap = detail > 0 ? static_cast<double>(detail) : 1024.0;
    const int tex_w = static_cast<int>(std::min(cap, std::ceil(area.width)));
    const int tex_h = static_cast<int>(std::min(cap, std::ceil(area.height)));
    // Rasterizing is a texel per pixel of the box, so an unchanged background
    // is looked up rather than redrawn. See TextureCache.
    std::string key;
    TextureHandle tex;
    if (paint.texture_cache && (style || replaced)) {
        if (replaced) {
            key = replaced_image_key(*replaced, area, tex_w, tex_h, ctx, font_size, paint, filter);
        } else {
            key = background_key(style, color, area.width, area.height, radii, font_size, 0, filter,
                                 &layers, tex_w, tex_h);
            append_image_version(key, paint);
        }
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
    draw_mesh(std::move(mesh), paint.backend, tex, opacity, xf, clip, filter);
    return true;
}

// The image a `border-image-source` names, or null.
const DecodedImage* border_image_source(const Box& b, const PaintContext& paint) {
    if (!paint.images || !b.style) return nullptr;
    std::string_view raw = trim_view(get(b.style, "border-image-source"));
    if (raw.empty() || raw == "none") return nullptr;
    if (raw.size() > 5 && raw.substr(0, 4) == "url(") {
        raw = trim_view(raw.substr(4, raw.size() - 5));
        if (raw.size() >= 2 && (raw.front() == '"' || raw.front() == '\'')) {
            raw = raw.substr(1, raw.size() - 2);
        }
    }
    if (raw.empty()) return nullptr;
    return paint.images->get(raw);
}

// What decides a border image's pixels, as a string.
//
// The resolved BorderImage rather than only the raw CSS: `border-image-width`
// is a multiple of the used border width, so two boxes with identical
// declarations and different borders are different pictures. The source URL
// still comes from the CSS, since the decoded image behind it is cached by
// path and cannot change under us.
std::string border_image_key(const ComputedStyle* style, double w, double h,
                             const BorderImage& bi) {
    std::string k;
    k.reserve(160);
    const auto num = [&](double v) {
        char buf[32];
        std::snprintf(buf, sizeof(buf), "%.3f;", v);
        k += buf;
    };
    num(w);
    num(h);
    num(bi.slice.top);
    num(bi.slice.right);
    num(bi.slice.bottom);
    num(bi.slice.left);
    k += bi.slice.fill ? "f;" : ";";
    num(bi.width.top);
    num(bi.width.right);
    num(bi.width.bottom);
    num(bi.width.left);
    num(static_cast<double>(static_cast<int>(bi.repeat_x)));
    num(static_cast<double>(static_cast<int>(bi.repeat_y)));
    k += get(style, "border-image-source");
    k += '|';
    return k;
}

// A nine-sliced border image over the border box, as one textured quad.
//
// CSS Backgrounds L3 s6.1: when a border image is drawn it REPLACES the border
// style beneath it, which is why the idiom is `border: 16px solid transparent`
// -- the border reserves the space and the image fills it.
//
// Returns false when there is nothing to draw, and the caller paints the
// ordinary border instead.
bool paint_border_image(const Box& b, const Rect& border_box, const LayoutContext& ctx,
                        double font_size, const PaintContext& paint, double opacity,
                        const Transform2D* xf, const ClipNode* clip, const ColorFilter* filter) {
    const DecodedImage* source = border_image_source(b, paint);
    if (!source || !paint.backend || border_box.width <= 0 || border_box.height <= 0) return false;

    BorderImage bi;
    if (!resolve_border_image(b.style, source, b.border_top, b.border_right, b.border_bottom,
                              b.border_left, b.width, b.height, ctx, font_size, &bi)) {
        return false;
    }

    const int tex_w = static_cast<int>(std::min(1024.0, std::ceil(border_box.width)));
    const int tex_h = static_cast<int>(std::min(1024.0, std::ceil(border_box.height)));

    // Cached, like a rasterized background and for the same reason, which
    // measurement made unarguable: twelve 9-sliced panels on a page with an
    // animation running cost 5.014 ms a frame without this and 0.072 ms with
    // an ordinary border. A border image does not change while its box does
    // not, and re-slicing it sixty times a second is the whole cost.
    std::string key;
    TextureHandle tex;
    if (paint.texture_cache) {
        key = border_image_key(b.style, border_box.width, border_box.height, bi);
        append_image_version(key, paint);
        tex = paint.texture_cache->get(key);
    }
    if (!tex) {
        std::vector<uint8_t> rgba;
        rasterize_border_image(bi, border_box.width, border_box.height, tex_w, tex_h, &rgba);
        if (rgba.empty()) return false;
        tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
        if (paint.texture_cache && !key.empty()) paint.texture_cache->put(key, tex);
        else if (paint.owned_textures) paint.owned_textures->push_back(tex);
    }

    Mesh mesh;
    // Square corners: the border image carries its own shape in its alpha, and
    // a rounded mesh would cut the sprite's corners off.
    tessellate_rounded_rect(border_box, BorderRadii::zero(), LinearColor::white(), &mesh);
    for (Vertex& v : mesh.vertices) {
        v.tex_coord = {static_cast<float>((v.position.x - border_box.x) / border_box.width),
                       static_cast<float>((v.position.y - border_box.y) / border_box.height)};
    }
    draw_mesh(std::move(mesh), paint.backend, tex, opacity, xf, clip, filter);
    return true;
}

// An <img>'s pixels, expressed as a background layer on its content box.
//
// Deliberately not a new painting path. `object-fit` and `background-size` are
// the same four behaviours under different names, and `object-position` and
// `background-position` are the same placement, so the layer machinery that
// already rasterizes, tiles, clips and caches is exactly what an image needs.
// The two differences are spelled out below rather than papered over.
std::vector<BackgroundLayer> replaced_layer(const Box& b, const PaintContext& paint) {
    if (!paint.images || !b.element || !b.style) return {};
    if (b.element->tag_name() != "img") return {};
    const std::string_view src = b.element->get_attribute("src");
    if (src.empty()) return {};
    const DecodedImage* image = paint.images->get(src);
    if (!image) return {};

    BackgroundLayer layer;
    layer.is_gradient = false;
    layer.image = image;
    layer.url.assign(src);
    // An image is drawn once, never tiled: `background-repeat` has no
    // object-fit counterpart and repeating one would be nothing a browser does.
    layer.repeat_x = false;
    layer.repeat_y = false;

    const std::string_view fit = get(b.style, "object-fit");
    if (fit == "contain" || fit == "scale-down") {
        layer.size_x = "contain";
    } else if (fit == "cover") {
        layer.size_x = "cover";
    } else if (fit == "none") {
        layer.size_x = "auto";
        layer.size_y = "auto";
    } else {
        // `fill` is the initial value and stretches to the box, ignoring the
        // aspect ratio -- which `background-size: 100% 100%` says exactly.
        layer.size_x = "100%";
        layer.size_y = "100%";
    }

    // The other difference: object-position centres by default where
    // background-position starts at the top left. It is what makes `cover`
    // crop evenly instead of off the bottom right.
    layer.pos_x = "50%";
    layer.pos_y = "50%";
    const std::string_view pos = get(b.style, "object-position");
    if (!pos.empty()) {
        const std::string_view trimmed = trim_view(pos);
        const size_t space = trimmed.find(' ');
        if (space == std::string_view::npos) {
            // One value sets the horizontal position; the vertical stays
            // centred, per CSS Images L3.
            layer.pos_x = std::string(trimmed);
        } else {
            layer.pos_x = std::string(trim_view(trimmed.substr(0, space)));
            layer.pos_y = std::string(trim_view(trimmed.substr(space + 1)));
        }
    }
    return {layer};
}

// The box's background image layers, resolved against its own colour -- and
// against the image store, which is what turns a `url(...)` from a string the
// parser kept into pixels the rasterizer can composite.
std::vector<BackgroundLayer> layers_of(const Box& b, const PaintContext& paint) {
    if (!b.style) return {};
    std::vector<BackgroundLayer> layers =
        resolve_background_layers(b.style, resolve_color(b.style, "color"));
    if (paint.images) {
        for (BackgroundLayer& l : layers) {
            if (!l.is_gradient && !l.url.empty()) l.image = paint.images->get(l.url);
        }
    }
    return layers;
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
        draw_mesh(std::move(mesh), paint.backend, cached, opacity, xf, clip, nullptr);
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
    blur_flat_rgba(&rgba, w, h, sigma);

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
    draw_mesh(std::move(mesh), paint.backend, tex, opacity, xf, clip, nullptr);
    return true;
}

// How wide `text` is when laid out the way `run` was. Used for both ends of a
// selection band and for where the cursor sits: measuring rather than guessing
// keeps the band on the glyphs at any letter-spacing.
double measured_width(std::string_view text, const Box& run, const LinearColor&,
                      const PaintContext& paint, const FaceHandle& face, double spacing) {
    if (run.preserved_tab) return text.empty() ? 0 : run.width;
    return text_advance(text, run.font_size, paint, face, spacing);
}

// The band behind the selected part of one run. `from`/`to` are byte offsets
// into the run's own text, already clipped to it.
void paint_selection_band(const Box& b, double x, double y, size_t from, size_t to,
                          const LinearColor& text_color, const PaintContext& paint,
                          const FaceHandle& face, double spacing, RenderInterface* backend,
                          double opacity, const Transform2D* xf, const ClipNode* clip,
                          const ColorFilter* filter, bool underline = false) {
    if (from >= to || to > b.text.size()) return;
    const double start = measured_width(b.text.substr(0, from), b, text_color, paint, face, spacing);
    const double end = measured_width(b.text.substr(0, to), b, text_color, paint, face, spacing);
    if (end <= start) return;
    // The ::selection colour of the field the run belongs to; the caret's
    // element is that field, since a band is only drawn in the focused one.
    const LinearColor band = selection_band_color(paint, paint.caret.element);
    Mesh mesh;
    const double height = b.height > 0 ? b.height : b.font_size;
    tessellate_rect(Rect(x + start, underline ? y + height - 1 : y, end - start,
                        underline ? 1 : height), underline ? text_color : band, &mesh, false);
    draw_mesh(std::move(mesh), backend, {}, opacity, xf, clip, filter);
}

// CSS UI L4 3: the outline, a ring OUTSIDE the border box, offset by
// `outline-offset` and following the border's curvature. It takes no layout
// space, which is why it can be drawn from the box's own geometry with nothing
// else to consult.
//
// The UA stylesheet has drawn a focus ring on `:focus-visible` since the sheet
// was written; nothing painted it, so tabbing through a document moved a focus
// nobody could see, and an author using `outline` for emphasis got silence.
void paint_outline(const Box& b, double x, double y, const LayoutContext& ctx,
                   const BorderRadii& radii, const PaintContext& paint, double opacity,
                   const Transform2D* xf, const ClipNode* clip, const ColorFilter* filter) {
    if (!b.style) return;
    const std::string_view style = get(b.style, "outline-style");
    if (style.empty() || ci_equal(style, "none") || ci_equal(style, "hidden")) return;
    const double fs = b.font_size > 0 ? b.font_size : ctx.root_font_size_px;
    double width = resolve_length(b.style, "outline-width", ctx, fs).pixels;
    // `auto` is a ring the UA picks; ours is a hairline, which is what the
    // shorthand leaves behind when only a colour and `auto` were given.
    if (width <= 0 && ci_equal(style, "auto")) width = 1;
    if (width <= 0) return;
    const double offset = resolve_length(b.style, "outline-offset", ctx, fs).pixels;

    // `outline-color` defaults to the text colour, as `currentColor` does, and
    // that is also what `auto` means in practice.
    LinearColor color = resolve_color(b.style, "outline-color");
    if (color.a <= 0) color = resolve_color(b.style, "color");
    if (color.a <= 0) return;

    const double grow = offset + width;
    const Rect outer(x - grow, y - grow, b.width + grow * 2, b.height + grow * 2);
    if (outer.width <= 0 || outer.height <= 0) return;
    // The curve grows with the ring, so a rounded box gets a rounded outline
    // rather than a square one standing off its corners.
    const auto grown = [&](const CornerRadius& c) {
        return CornerRadius(c.x_radius > 0 ? c.x_radius + grow : 0,
                            c.y_radius > 0 ? c.y_radius + grow : 0);
    };
    const BorderRadii outer_radii(grown(radii.top_left), grown(radii.top_right),
                                  grown(radii.bottom_right), grown(radii.bottom_left));
    const LinearColor colors[4] = {color, color, color, color};
    Mesh mesh;
    tessellate_border(outer, outer_radii, width, width, width, width, colors, &mesh);
    draw_mesh(std::move(mesh), paint.backend, {}, opacity, xf, clip, filter);
}

// CSS permits UA-dependent inset/outset shading. Match the softened bevels
// measured in Chrome 152: change sRGB brightness, preserving hue and alpha,
// with extra contrast for near-black borders. Do not shade linear RGB directly.
LinearColor bevel_color(const ComputedStyle* style, std::string_view property, bool dark) {
    ProfileScope prof(&g_paint_profile.colors);
    const auto* value=style->parsed(CssPropertyRegistry::instance().id_of(property));
    if (!value || value->kind()!=CssValueKind::Color) return LinearColor::transparent();
    const auto& source=static_cast<const CssColor&>(*value);
    const double luminance=.2126*srgb_byte_to_linear(source.r)+
                           .7152*srgb_byte_to_linear(source.g)+.0722*srgb_byte_to_linear(source.b);
    const auto adjust = [](const CssColor& input, bool lighter) {
        CssColor result=input;
        const float maximum=std::max({input.r,input.g,input.b})/255.f;
        if (maximum==0) {
            result.r=result.g=result.b=lighter ? 84 : 0;
            return result;
        }
        const float factor=(lighter ? std::min(1.f,maximum+.33f) : std::max(0.f,maximum-.33f))/maximum;
        const auto channel = [&](uint8_t byte) {
            const float normalized=std::clamp((byte/255.f)*factor,0.f,1.f);
            // 256 bins, with 1.0 kept in the final bin (Chrome's color quantizer).
            return static_cast<uint8_t>(normalized*std::nextafter(256.f,0.f));
        };
        result.r=channel(input.r); result.g=channel(input.g); result.b=channel(input.b);
        return result;
    };
    CssColor result=source;
    if (luminance<=.014443845) {
        result=adjust(source,true);
        if (!dark) result=adjust(result,true);
    } else if (dark || luminance<=.83077) {
        result=adjust(source,!dark);
    }
    return LinearColor::from_srgb(result.r,result.g,result.b,result.a);
}

bool border_keyword(std::string_view value, std::string_view keyword) {
    if (value.size()!=keyword.size()) return false;
    for (size_t i=0; i<value.size(); ++i) {
        char c=value[i];
        if (c>='A' && c<='Z') c=static_cast<char>(c-'A'+'a');
        if (c!=keyword[i]) return false;
    }
    return true;
}

bool collapsed_table_part(const Box& b) {
    return is_table_display(b.display) && b.display != DisplayKind::TableCaption &&
           border_keyword(get(b.style, "border-collapse"), "collapse");
}

void paint_table_borders(const std::vector<TableBorderSegment>& segments, double x, double y,
                         const PaintContext& paint, const PaintState& state, const Transform2D* xf) {
    Mesh mesh;
    static const char* colors[] = {"border-top-color", "border-right-color", "border-bottom-color", "border-left-color"};
    for (const auto& segment : segments) {
        const auto* style = paint.styles && segment.element ? paint.styles->style_of(*segment.element) : segment.fallback_style;
        if (!style) continue;
        auto property = std::string_view(colors[static_cast<int>(segment.border.side)]);
        if (get(style, property).empty() || border_keyword(get(style, property), "currentcolor")) property = "color";
        const auto color = resolve_color(style, property);
        const double width = segment.border.used_width();
        if (width <= 0 || color.a <= 0) continue;
        const auto rect = [&](double along, double across, double length, double thickness) {
            return segment.horizontal ? Rect(x + segment.x + along, y + segment.y + across, length, thickness)
                                      : Rect(x + segment.x + across, y + segment.y + along, thickness, length);
        };
        const auto fill = [&](double across, double thickness, const LinearColor& tint) {
            tessellate_rect(rect(-segment.start_extension, across, segment.length + segment.start_extension + segment.end_extension, thickness), tint, &mesh, false);
        };
        const auto kind = segment.border.style;
        if (kind == TableBorderStyle::Double && width >= 3) {
            const double stripe = width / 3;
            fill(-width * .5, stripe, color);
            fill(width * .5 - stripe, stripe, color);
        } else if (kind == TableBorderStyle::Dotted || kind == TableBorderStyle::Dashed) {
            const double dash = kind == TableBorderStyle::Dotted ? width : 3 * width;
            for (double at = 0; at < segment.length; at += dash * 2) {
                const Rect r = rect(at, -width * .5, std::min(dash, segment.length - at), width);
                if (kind == TableBorderStyle::Dotted) {
                    BorderRadii radius;
                    radius.top_left = radius.top_right = radius.bottom_left = radius.bottom_right = {width * .5, width * .5};
                    tessellate_rounded_rect(r, radius, color, &mesh);
                } else tessellate_rect(r, color, &mesh, false);
            }
        } else if (kind == TableBorderStyle::Inset || kind == TableBorderStyle::Ridge ||
                   kind == TableBorderStyle::Outset || kind == TableBorderStyle::Groove) {
            const bool ridge = kind == TableBorderStyle::Inset || kind == TableBorderStyle::Ridge;
            fill(-width * .5, width * .5, bevel_color(style, property, !ridge));
            fill(0, width * .5, bevel_color(style, property, ridge));
        } else fill(-width * .5, width, color);
    }
    draw_mesh(std::move(mesh), paint.backend, {}, state.opacity, xf, state.clip.get(), state.filter.get());
}

// The recursive walk already resolved this box's radii and background color
// for effects and layered backgrounds. Keep one decoration implementation,
// accepting those values so an ordinary fill/border does not resolve them again.
void paint_resolved_decorations(const Box& b, const Rect& border_box, const BorderRadii& radii,
                                const LinearColor& bg, Mesh* out, bool with_background,
                                bool skip_border) {
    ProfileScope prof(&g_paint_profile.decoration_build);
    if (!b.style || b.width <= 0 || b.height <= 0) return;
    // Background under the border: transparent borders reveal it.
    if (with_background && bg.a > 0) tessellate_rounded_rect(border_box, radii, bg, out);
    // A border image replaces the ordinary border, including transparent texels.
    if (!skip_border && !collapsed_table_part(b) &&
        (b.border_top > 0 || b.border_right > 0 || b.border_bottom > 0 || b.border_left > 0)) {
        const auto side = [&](std::string_view property, std::string_view style_property, bool leading) {
            const std::string_view raw = get(b.style, property);
            if (raw.empty() || border_keyword(raw,"currentcolor"))
                property="color";
            const std::string_view border_style=get(b.style,style_property);
            const bool inset=border_keyword(border_style,"inset");
            if (inset || border_keyword(border_style,"outset"))
                return bevel_color(b.style,property,inset ? leading : !leading);
            return resolve_color(b.style, property);
        };
        const LinearColor colors[4] = {side("border-top-color","border-top-style",true),
                                       side("border-right-color","border-right-style",false),
                                       side("border-bottom-color","border-bottom-style",false),
                                       side("border-left-color","border-left-style",true)};
        tessellate_border(border_box, radii, b.border_top, b.border_right, b.border_bottom,
                          b.border_left, colors, out);
    }
}

// The element a box belongs to: itself if it has one, otherwise the nearest
// ancestor that does. A text run has none of its own.
const Element* owner_element(const BoxTree& tree, BoxId id) {
    for (BoxId b = id; b != kNoBox; b = tree[b].parent) {
        if (tree[b].element) return tree[b].element;
    }
    return nullptr;
}

struct TablePositionedPaint {
    BoxId id;
    double origin_x, origin_y;
    PaintState state;
    int rank;
    size_t sequence;
    bool own_only = false;
};

std::shared_ptr<const OverflowClip> applicable_overflow(const BoxTree& tree, BoxId cb,
        const std::shared_ptr<const OverflowClip>& node, double* scroll_x, double* scroll_y) {
    if (!node) return {};
    auto parent = applicable_overflow(tree, cb, node->parent, scroll_x, scroll_y);
    if (!overflow_clip_applies(tree, node->owner, cb)) {
        *scroll_x += node->scroll_x; *scroll_y += node->scroll_y;
        return parent;
    }
    if (parent == node->parent) return node;
    auto copy = std::make_shared<OverflowClip>(*node);
    copy->parent = std::move(parent);
    return copy;
}

std::shared_ptr<const ClipNode> applicable_clip_polygons(const BoxTree& tree, BoxId cb,
        const std::shared_ptr<const ClipNode>& node) {
    if (!node) return {};
    auto parent = applicable_clip_polygons(tree, cb, node->parent);
    if (node->overflow_owner != kNoBox && !overflow_clip_applies(tree, node->overflow_owner, cb)) return parent;
    if (parent == node->parent) return node;
    auto copy = std::make_shared<ClipNode>(*node);
    copy->parent = std::move(parent);
    return copy;
}

// Only ancestors whose draw ranges are split by a promoted descendant lose
// inner replay. Unrelated cells retain their complete ranges and the table's
// outer capture still contains every draw in final order.
class TablePositionedReuse final : public PaintReuse {
public:
    TablePositionedReuse(PaintReuse* source, const BoxTree& tree, BoxId table) : source_(source) {
        for (const BoxId child : tree.children(table)) collect(tree, child);
        std::sort(split_.begin(), split_.end());
    }
    void begin_paint(TextureHandle atlas) override { source_->begin_paint(atlas); }
    bool replay(BoxId id) override { return complete(id) && source_->replay(id); }
    bool tracks_inputs(BoxId id) const override { return complete(id) && source_->tracks_inputs(id); }
    bool replay(BoxId id, const PaintReplayInputs& inputs) override {
        return complete(id) && source_->replay(id, inputs);
    }
    void begin_box(BoxId id) override { if (complete(id)) source_->begin_box(id); }
    void end_box(BoxId id) override { if (complete(id)) source_->end_box(id); }
private:
    bool collect(const BoxTree& tree, BoxId id) {
        const Box& box = tree[id];
        if (box.position != PositionType::Static && box.z_index && *box.z_index < 0) return true;
        bool split = table_positioned_layer(box);
        if (!split) for (const BoxId child : tree.children(id)) split = collect(tree, child) || split;
        if (split) split_.push_back(id);
        return split;
    }
    bool complete(BoxId id) const { return !std::binary_search(split_.begin(), split_.end(), id); }
    PaintReuse* source_;
    std::vector<BoxId> split_;
};

void paint_recursive(const BoxTree& tree, BoxId id, const LayoutContext& ctx, double origin_x,
                     double origin_y, const PaintContext& paint, TextureHandle atlas_texture,
                     BoxId canvas_owner, PaintState state,
                     std::vector<TablePositionedPaint>* table_positioned = nullptr,
                     std::optional<int> table_layer = std::nullopt, bool table_isolation = false,
                     bool own_only = false, bool negative_scan = false,
                     std::vector<TablePositionedPaint>* table_negative = nullptr) {
    const Box& b = tree[id];
    struct RestoreScissor {
        RenderInterface* backend;
        std::optional<Recti> before;
        bool changed = false;
        ~RestoreScissor() { if (changed && backend) backend->set_scissor(before ? &*before : nullptr); }
    } restore_scissor{paint.backend, state.scissor};
    if (state.overflow && (b.position == PositionType::Absolute || b.position == PositionType::Fixed)) {
        const BoxId cb = (b.position == PositionType::Absolute
            ? resolve_absolute_containing_block(tree, id, ctx) : resolve_fixed_containing_block(tree, id, ctx)).box;
        double scroll_x = 0, scroll_y = 0;
        auto overflow = applicable_overflow(tree, cb, state.overflow, &scroll_x, &scroll_y);
        if (overflow != state.overflow) {
            origin_x += scroll_x; origin_y += scroll_y;
            state.overflow = std::move(overflow);
            state.scissor.reset();
            for (auto node = state.overflow; node; node = node->parent)
                state.scissor = state.scissor ? intersect(*state.scissor, node->rect) : node->rect;
            state.clip = applicable_clip_polygons(tree, cb, state.clip);
            restore_scissor.changed = true;
            if (paint.backend) paint.backend->set_scissor(state.scissor ? &*state.scissor : nullptr);
        }
    }
    if (table_negative && b.position != PositionType::Static && b.z_index && *b.z_index < 0) {
        if (negative_scan)
            table_negative->push_back({id, origin_x, origin_y, state, *b.z_index, table_negative->size()});
        return;
    }
    if (negative_scan && table_positioned_isolation(b)) return;
    const bool table_decoration = collapsed_table_part(b) && b.display != DisplayKind::Table &&
                                  b.display != DisplayKind::InlineTable;
    bool descendants_only = negative_scan;
    if (table_positioned && !table_decoration && (table_layer || table_positioned_layer(b))) {
        const bool split = !table_isolation && !table_positioned_isolation(b) && has_table_positioned_layer(tree,id);
        const int rank = table_isolation ? table_layer.value_or(0) :
            (b.position != PositionType::Static && b.z_index ? *b.z_index : table_layer.value_or(0));
        table_positioned->push_back({id, origin_x, origin_y, state, rank, table_positioned->size(), split});
        if (!split) return;
        descendants_only = true;
    }
    // A positioned cell's decorations remain in the table layer. Only a real
    // stacking context isolates the rank of its positioned descendants.
    if (table_decoration && table_positioned_layer(b) && !table_layer) {
        table_layer = b.z_index.value_or(0);
        table_isolation = table_positioned_isolation(b);
    }
    // A sticky box is drawn where the sticky pass pinned it; its natural
    // position stays in the tree.
    const double x = origin_x + b.x + b.sticky_offset_x;
    const double y = origin_y + b.y + b.sticky_offset_y;
    if (paint.reuse && !own_only && !descendants_only) {
        if (paint.reuse->tracks_inputs(id)) {
            const PaintReplayInputs inputs{x, y, state.opacity, state.scissor, state.transformed,
                                            state.xform, state.clip, state.filter, canvas_owner,
                                            state.overflow, state.absolute_cb, state.fixed_cb, state.blend};
            if (paint.reuse->replay(id, inputs)) return;
        } else if (paint.reuse->replay(id)) return;
    }
    struct Capture {
        PaintReuse* reuse;
        BoxId id;
        Capture(PaintReuse* r, BoxId b) : reuse(r), id(b) { if (reuse) reuse->begin_box(id); }
        ~Capture() { if (reuse) reuse->end_box(id); }
    } capture(own_only || descendants_only ? nullptr : paint.reuse, id);

    // Nothing this box or anything under it can paint reaches the clip it is
    // inside, so the whole subtree is skipped here rather than walked and
    // thrown away a mesh at a time.
    //
    // This is what makes a long list cheap. The clip already rejected the
    // meshes, but building them cost 2 microseconds a box -- so scrolling a
    // 2,000-row list, 46,000 boxes to show twenty of them, took 100 ms a frame.
    // The rectangle comes from compute_visual_overflow, which stops the union
    // at a clipping box so a scroll never invalidates it.
    if (state.scissor || state.clip) {
        const double cx0 = x + b.vis_x0;
        const double cy0 = y + b.vis_y0;
        const double cx1 = x + b.vis_x1;
        const double cy1 = y + b.vis_y1;
        // The scissor first, because a plain rectangular scroller is ONLY a
        // scissor -- the ClipNode chain is pushed for rounded corners, so a
        // test that looked at the clip alone never fired for the ordinary
        // case, which is the one that matters.
        if (state.scissor) {
            const Recti& r = *state.scissor;
            if ((cx1 < r.x || cx0 > r.x + r.width || cy1 < r.y || cy0 > r.y + r.height) &&
                !subtree_has_overflow_escape(tree, id, ctx)) return;
        }
        for (const ClipNode* n = state.clip.get(); n; n = n->parent.get()) {
            if (!n->intersects_box(cx0, cy0, cx1, cy1) &&
                (n->overflow_owner == kNoBox || !subtree_has_overflow_escape(tree, id, ctx))) return;
        }
    }

    // Line boxes carry their container's style for inline layout's sake and
    // anonymous boxes are not elements: neither has a background or border of
    // its own to paint. Painting a line box with its <th>'s background drew a
    // bar behind every header's text.
    const bool decorated = b.kind != BoxKind::Line && b.kind != BoxKind::AnonymousBlock &&
                           b.kind != BoxKind::AnonymousInline && b.kind != BoxKind::Text;
    if (establishes_absolute_containing_block(b)) state.absolute_cb = id;
    if (establishes_fixed_containing_block(b)) state.fixed_cb = id;
    if (decorated && b.style) state.opacity *= resolve_opacity(b.style);
    // `mix-blend-mode`: told to the backend once per box, and again when the
    // box's subtree is done, so a sibling painted after it is back to the
    // parent's mode.
    const BlendMode inherited_blend = state.blend;
    if (decorated && b.style) {
        const BlendMode own = blend_mode_of(get(b.style, "mix-blend-mode"));
        if (own != BlendMode::Normal) state.blend = own;
    }
    struct RestoreBlend {
        RenderInterface* backend;
        BlendMode before;
        ~RestoreBlend() { if (backend) backend->set_blend_mode(before); }
    } restore_blend{paint.backend, inherited_blend};
    if (paint.backend) paint.backend->set_blend_mode(state.blend);
    // `visibility: hidden` paints nothing of the box itself; its children
    // inherit the value and paint nothing either unless they override it.
    const bool hidden = descendants_only || (b.style && get(b.style, "visibility") == "hidden");

    const BoxId parent = b.parent;
    const ComputedStyle* ps = parent == kNoBox ? nullptr : tree[parent].style;
    const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
    Rect border_box(x, y, b.width, b.height);
    if (b.display == DisplayKind::Table || b.display == DisplayKind::InlineTable) {
        double top_captions = 0, bottom_captions = 0;
        for (BoxId child : tree.children(id)) {
            const Box& caption = tree[child];
            if (caption.display != DisplayKind::TableCaption) continue;
            const double extent = caption.margin_top + caption.height + caption.margin_bottom;
            if (get(caption.style, "caption-side") == "bottom") bottom_captions += extent;
            else top_captions += extent;
        }
        // Layout retains the wrapper (including captions) for flow and input.
        // The table's own background and border decorate only its grid frame.
        border_box.y += top_captions;
        border_box.height = std::max(0.0, border_box.height - top_captions - bottom_captions);
    }
    const BorderRadii radii =
        decorated && b.style ? resolve_border_radii(b.style, border_box.width, border_box.height, ctx, fs)
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
    if (blur > 0 && !hidden && border_box.width > 0 && border_box.height > 0 && paint.backend && id != canvas_owner) {
        ProfileScope prof(&g_paint_profile.filters);
        const std::vector<BackgroundLayer> layers = layers_of(b, paint);
        const LinearColor bg = resolve_color(b.style, "background-color");
        if (bg.a > 0 || has_paintable_layer(layers)) {
            const double pad_px = 3 * blur;
            const double full_w = border_box.width + 2 * pad_px, full_h = border_box.height + 2 * pad_px;
            const double scale = std::min(1.0, 1024.0 / std::max(full_w, full_h));
            const int tex_w = std::max(1, static_cast<int>(std::ceil(full_w * scale)));
            const int tex_h = std::max(1, static_cast<int>(std::ceil(full_h * scale)));
            const int pad = static_cast<int>(std::round(pad_px * scale));
            // Blurring is the most expensive thing paint does, so a box whose
            // blurred image has not changed reuses it. See TextureCache.
            std::string key;
            TextureHandle tex;
            if (paint.texture_cache && b.style) {
                key = background_key(b.style, bg, border_box.width, border_box.height, radii, fs, blur, nullptr,
                                     &layers, tex_w, tex_h);
                append_image_version(key, paint);
                tex = paint.texture_cache->get(key);
            }
            if (!tex) {
                if (g_paint_profile.on) ++g_paint_profile.filter_textures;
                std::vector<uint8_t> rgba;
                {
                    ProfileScope raster(&g_paint_profile.filter_raster);
                    rasterize_background_padded(layers, bg, border_box.width, border_box.height, tex_w, tex_h, pad, &radii,
                                                ctx, fs, &rgba);
                }
                {
                    ProfileScope convolution(&g_paint_profile.filter_blur);
                    blur_rgba(&rgba, tex_w, tex_h, blur * scale);
                }
                {
                    ProfileScope upload(&g_paint_profile.filter_upload);
                    tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
                }
                if (paint.texture_cache && !key.empty()) paint.texture_cache->put(key, tex);
                else if (paint.owned_textures) paint.owned_textures->push_back(tex);
            }
            const Rect area(border_box.x - pad_px, border_box.y - pad_px, full_w, full_h);
            Mesh mesh;
            // The blurred image supplies its own soft edge; a feather would
            // only blur an already-blurred boundary.
            tessellate_rect(area, LinearColor::white(), &mesh, false);
            for (Vertex& v : mesh.vertices) {
                v.tex_coord = {static_cast<float>((v.position.x - area.x) / area.width),
                               static_cast<float>((v.position.y - area.y) / area.height)};
            }
            draw_mesh(std::move(mesh), paint.backend, tex, state.opacity, xf, state.clip.get(), state.filter.get());
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
            filter_backdrop(std::move(shape), paint.backend, effect, xf, state.clip.get());
        }
    }

    // A box whose background went onto the canvas (§14.2) does not paint it
    // again; a box with gradient layers paints them as one texture and only
    // its border through the mesh; one that clips its background to its text
    // paints it through the glyphs instead (see the text branch).
    const bool clip_text = decorated && b.style && clips_background_to_text(b.style);
    bool background_done = id == canvas_owner || !decorated || hidden || blurred || clip_text;
    if (!background_done && b.style && b.width > 0 && b.height > 0) {
        const std::vector<BackgroundLayer> layers = layers_of(b, paint);
        if (has_paintable_layer(layers)) {
            background_done = paint_layered_background(
                layers, resolve_color(b.style, "background-color"), border_box, radii, ctx, fs, paint,
                state.opacity, xf, state.clip.get(), state.filter.get(), b.style);
        }
    }

    // A border image, which replaces the border drawn below.
    const bool border_image_drawn =
        decorated && !hidden && !blurred && !collapsed_table_part(b) && b.width > 0 && b.height > 0 &&
        paint_border_image(b, border_box, ctx, fs, paint, state.opacity, xf, state.clip.get(),
                           state.filter.get());

    // The <img>'s own content. Above the background, below the border, and
    // inside the CONTENT box rather than the border box -- padding on an
    // image insets the picture, it does not scale it.
    if (decorated && !hidden && !blurred && b.width > 0 && b.height > 0) {
        const std::vector<BackgroundLayer> replaced = replaced_layer(b, paint);
        if (!replaced.empty()) {
            const double inset_l = b.border_left + b.padding_left;
            const double inset_t = b.border_top + b.padding_top;
            const Rect content_box(
                x + inset_l, y + inset_t,
                std::max(0.0, b.width - inset_l - b.border_right - b.padding_right),
                std::max(0.0, b.height - inset_t - b.border_bottom - b.padding_bottom));
            if (content_box.width > 0 && content_box.height > 0) {
                paint_layered_background(replaced, LinearColor::transparent(), content_box,
                                         BorderRadii::zero(), ctx, fs, paint, state.opacity, xf,
                                         state.clip.get(), state.filter.get(), nullptr, &replaced.front());
            }
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
            paint.backend->render_rounded_rect_owned(shape, std::move(fallback.vertices), std::move(fallback.indices));
            background_done = true;
        }

        Mesh mesh;
        paint_resolved_decorations(b, border_box, radii, bg_color, &mesh, !background_done,
                                   border_image_drawn);
        {
            ProfileScope decoration_draw(&g_paint_profile.decoration_draw);
            draw_mesh(std::move(mesh), paint.backend, {}, state.opacity, xf, state.clip.get(), state.filter.get());
        }
        // Outside the border box, and taking no layout space -- so it is drawn
        // from this box's own geometry, over its border.
        paint_outline(b, x, y, ctx, radii, paint, state.opacity, xf, state.clip.get(),
                      state.filter.get());
        if (!shadows.empty()) {
            const Rect padding_box(border_box.x + b.border_left, border_box.y + b.border_top,
                                   border_box.width - b.border_left - b.border_right,
                                   border_box.height - b.border_top - b.border_bottom);
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
    if (decorated && !hidden && paint.backend && paint.active_option && b.element == paint.active_option &&
        b.width > 2 && b.height > 2) {
        // The keyboard row can differ from selectedness under Ctrl+arrows.
        // Draw inside the row so overflow clipping retains the entire cue.
        const auto color = resolve_color(b.style, "color");
        Mesh ring;
        tessellate_rect(Rect(x + 1, y + 1, b.width - 2, 1), color, &ring, false);
        tessellate_rect(Rect(x + 1, y + b.height - 2, b.width - 2, 1), color, &ring, false);
        tessellate_rect(Rect(x + 1, y + 2, 1, b.height - 4), color, &ring, false);
        tessellate_rect(Rect(x + b.width - 2, y + 2, 1, b.height - 4), color, &ring, false);
        draw_mesh(std::move(ring), paint.backend, {}, state.opacity, xf, state.clip.get(), state.filter.get());
    }

    // The selection band uses the run's source mapping, including styled
    // display buffers that no longer view the editable value directly.
    if (b.kind == BoxKind::Text && !b.text.empty() && !hidden && paint.font && paint.atlas &&
        (paint.caret.selection_to > paint.caret.selection_from ||
         paint.caret.composition_to > paint.caret.composition_from) && !paint.caret.source.empty()) {
        const auto source_at = text_source_offset(b, paint.caret.source);
        if (source_at &&
            owner_element(tree, id) == paint.caret.element) {
            const size_t off = *source_at;
            const size_t from = source_to_display(b, paint.caret.selection_from > off ? paint.caret.selection_from - off : 0);
            const size_t to = source_to_display(b, paint.caret.selection_to > off ? paint.caret.selection_to - off : 0);
            paint_selection_band(b, x, y, from, to, resolve_color(b.style, "color"), paint,
                                 face_for_run(b, ctx, paint),
                                 letter_spacing_of(b.style, ctx, b.font_size) +
                                     b.justify_letter_spacing,
                                 paint.backend, state.opacity, xf, state.clip.get(),
                                 state.filter.get());
            const size_t composition_from = source_to_display(b, paint.caret.composition_from > off ? paint.caret.composition_from - off : 0);
            const size_t composition_to = source_to_display(b, paint.caret.composition_to > off ? paint.caret.composition_to - off : 0);
            paint_selection_band(b, x, y, composition_from, composition_to, resolve_color(b.style, "color"), paint,
                                 face_for_run(b, ctx, paint),
                                 letter_spacing_of(b.style, ctx, b.font_size) + b.justify_letter_spacing,
                                 paint.backend, state.opacity, xf, state.clip.get(), state.filter.get(), true);
        }
    }

    // A text run's own y is its top; the baseline is where the glyphs sit, and
    // the line box put it there.
    if (b.kind == BoxKind::Text && !b.preserved_tab && !b.text.empty() && paint.font && paint.atlas && !hidden) {
        const BoxId line = b.parent;
        const double baseline =
            line != kNoBox && tree[line].kind == BoxKind::Line ? origin_y + tree[line].baseline
                                                               : y + b.height;
        const double spacing =
            letter_spacing_of(b.style, ctx, b.font_size) + b.justify_letter_spacing;
        const LinearColor text_color = resolve_color(b.style, "color");
        const FaceHandle run_face = face_for_run(b, ctx, paint);
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
                draw_mesh(std::move(shadow), paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());
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
                    draw_mesh(std::move(shadow), paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());
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
            const std::vector<BackgroundLayer> layers = layers_of(b, paint);
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
        // The rules over the glyphs, so a line-through reads as struck rather
        // than as a rule the text happens to sit near.
        if (const int deco = decoration_flags_of(b.style)) {
            const FontMetrics* dm = control_metrics(ctx, b.style);
            const double dascent = dm ? dm->ascent(b.font_size) : b.font_size * 0.8;
            double run_w = 0;
            for (const Vertex& v : text.vertices) run_w = std::max<double>(run_w, v.position.x - x);
            if (run_w <= 0) run_w = b.width;
            paint_text_decorations(deco, x, baseline, run_w, dascent, b.font_size, b.style, ctx,
                                   text_color, paint, state.opacity, xf, state.clip.get(),
                                   state.filter.get());
        }
        // The handle from the single up-front upload, never a fresh one: see
        // prepare_glyphs.
        draw_mesh(std::move(text), paint.backend, atlas_texture, state.opacity, xf, state.clip.get(), state.filter.get());
    }

    // A preserved tab has no glyph ink, but text decoration spans its advance.
    if (b.kind == BoxKind::Text && b.preserved_tab && !hidden && paint.font && paint.atlas) {
        if (const int deco = decoration_flags_of(b.style)) {
            const BoxId line = b.parent;
            const double baseline = line != kNoBox && tree[line].kind == BoxKind::Line
                ? origin_y + tree[line].baseline : y + b.height;
            const FontMetrics* dm = control_metrics(ctx, b.style);
            const double ascent = dm ? dm->ascent(b.font_size) : b.font_size * 0.8;
            paint_text_decorations(deco, x, baseline, b.width, ascent, b.font_size,
                b.style, ctx, resolve_color(b.style, "color"), paint, state.opacity,
                xf, state.clip.get(), state.filter.get());
        }
    }

    // The caret, in the run that was found to hold it. Which run, and how far
    // into it, was settled after layout: paint cannot work that out per run
    // without missing the cursor at a line end, where the newline belongs to
    // no run at all.
    if (b.kind == BoxKind::Text && id == paint.caret.run && paint.caret.visible && !hidden &&
        paint.font && paint.atlas) {
        const LinearColor caret_color = caret_color_of(b.style);
        const FaceHandle caret_face = face_for_run(b, ctx, paint);
        const double caret_spacing =
            letter_spacing_of(b.style, ctx, b.font_size) + b.justify_letter_spacing;
        const double advance = measured_width(b.text.substr(0, std::min(paint.caret.run_offset, b.text.size())),
                                           b, caret_color, paint, caret_face, caret_spacing);
        Mesh bar;
        tessellate_rect(Rect(x + advance, y, 1.0, b.height > 0 ? b.height : b.font_size),
                        caret_color, &bar, false);
        draw_mesh(std::move(bar), paint.backend, {}, state.opacity, xf, state.clip.get(), state.filter.get());
    }

    // `overflow` other than visible clips the children to the padding box
    // (§11.1.1) — a rectangle for now: the corners of a rounded scroller are
    // not rounded off.
    const auto* table_segments = (b.display == DisplayKind::Table || b.display == DisplayKind::InlineTable)
        ? tree.table_borders(id) : nullptr;
    std::optional<PaintState> table_border_state;
    if (table_segments) table_border_state = state;
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
        state.overflow = std::make_shared<OverflowClip>(OverflowClip{id, r, b.scroll_x, b.scroll_y, state.overflow});
        if (state.scissor) r = intersect(*state.scissor, r);
        state.scissor = r;
        paint.backend->set_scissor(&r);
        // A rounded scroller clips to its rounded padding box (§11.1.1 with
        // Backgrounds §5.3): the corners are cut geometrically, since the
        // scissor is only the bounding rectangle.
        const BorderRadii inner =
            inset_radii(clamp_radii_to_rect(radii, b.width, b.height), b.border_top, b.border_right, b.border_bottom, b.border_left);
        if (!inner.top_left.is_zero() || !inner.top_right.is_zero() ||
            !inner.bottom_right.is_zero() || !inner.bottom_left.is_zero()) {
            push_clip(&state, rounded_rect_outline(Rect(px0, py0, px1 - px0, py1 - py0), inner), id);
        }
    }

    // CSS 2.1 Appendix E, per container: negative-z stacking contexts, then
    // in-flow children in tree order, then positioned z:auto/0 children in
    // tree order, then positive z ascending (ties by tree order). A ring's
    // `::after` cover painted over its `z-index: 1` number until this; a
    // badge's `position: absolute` sibling painted under a later in-flow one.
    //
    // Shared with HIT TESTING, which has to agree with it: the two had
    // separate implementations and disagreed, so a positioned element drawn on
    // top of a later sibling could not be clicked.
    const ChildPaintOrder order(tree, id);
    const double child_x = x - b.scroll_x, child_y = y - b.scroll_y;
    std::vector<TablePositionedPaint> local_positioned;
    const bool owns_positioned = !own_only && !negative_scan && !table_positioned && table_segments && has_table_positioned_layer(tree, id);
    auto* positioned = owns_positioned ? &local_positioned : table_positioned;
    if (b.position != PositionType::Static && b.z_index && *b.z_index < 0) positioned = nullptr;
    std::optional<PaintContext> table_paint;
    std::optional<TablePositionedReuse> table_reuse;
    if (owns_positioned && paint.reuse) {
        table_reuse.emplace(paint.reuse, tree, id);
        table_paint.emplace(paint);
        table_paint->reuse = &*table_reuse;
    }
    const PaintContext& child_paint = table_paint ? *table_paint : paint;
    std::vector<TablePositionedPaint> local_negative;
    auto* negative = table_negative;
    if (owns_positioned && has_table_negative_layer(tree,id)) {
        negative = &local_negative;
        for (const BoxId c : order)
            paint_recursive(tree,c,ctx,child_x,child_y,child_paint,atlas_texture,canvas_owner,state,
                            nullptr,std::nullopt,false,false,true,negative);
        std::sort(local_negative.begin(),local_negative.end(),[](const auto& a,const auto& c) {
            return a.rank != c.rank ? a.rank < c.rank : a.sequence < c.sequence;
        });
        for (const auto& entry : local_negative) {
            if (paint.backend) paint.backend->set_scissor(entry.state.scissor ? &*entry.state.scissor : nullptr);
            paint_recursive(tree,entry.id,ctx,entry.origin_x,entry.origin_y,paint,atlas_texture,canvas_owner,entry.state);
        }
        if (paint.backend) paint.backend->set_scissor(state.scissor ? &*state.scissor : nullptr);
    }
    for (const BoxId c : order) {
        if (own_only) break;
        paint_recursive(tree, c, ctx, child_x, child_y, child_paint, atlas_texture, canvas_owner, state, positioned, table_layer, table_isolation, false, negative_scan, negative);
    }

    if (!hidden && !blurred && paint.backend && table_segments) {
        paint.backend->set_scissor(outer_scissor ? &*outer_scissor : nullptr);
        paint_table_borders(*table_segments, x, y, paint, *table_border_state, xf);
        paint.backend->set_scissor(state.scissor ? &*state.scissor : nullptr);
    }
    if (owns_positioned) {
        std::sort(local_positioned.begin(), local_positioned.end(), [](const auto& a, const auto& c) {
            return a.rank != c.rank ? a.rank < c.rank : a.sequence < c.sequence;
        });
        for (const auto& entry : local_positioned) {
            if (paint.backend) paint.backend->set_scissor(entry.state.scissor ? &*entry.state.scissor : nullptr);
            paint_recursive(tree, entry.id, ctx, entry.origin_x, entry.origin_y,
                            paint, atlas_texture, canvas_owner, entry.state, nullptr, std::nullopt, false, entry.own_only);
        }
        if (paint.backend) paint.backend->set_scissor(state.scissor ? &*state.scissor : nullptr);
    }


    // The scrollbars, over the content and inside the container's own clip:
    // a list you can scroll with no bar on it gives no sign that there is more
    // of it, which reads as a list that is simply cut off.
    if (!descendants_only && decorated && b.style && clips_children(b.style)) {
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

bool has_background(const Box& b, const PaintContext& paint) {
    if (!b.style) return false;
    if (resolve_color(b.style, "background-color").a > 0) return true;
    return has_paintable_layer(layers_of(b, paint));
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
    if (html != kNoBox && has_background(tree[html], paint)) owner = html;
    else if (body != kNoBox && has_background(tree[body], paint)) owner = body;
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
    if (!paint_layered_background(layers_of(b, paint), color, canvas, BorderRadii::zero(), ctx, fs, paint,
                                  1, nullptr, nullptr, nullptr, b.style) &&
        color.a > 0) {
        Mesh mesh;
        tessellate_rect(canvas, color, &mesh);
        draw_mesh(std::move(mesh), paint.backend, {});
    }
    return owner;
}

} // namespace

double resolve_opacity(const ComputedStyle* style) {
    std::string_view raw = style ? style->get("opacity") : std::string_view();
    const auto space = [](char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f'; };
    while (!raw.empty() && space(raw.front())) raw.remove_prefix(1);
    while (!raw.empty() && space(raw.back())) raw.remove_suffix(1);
    if (raw.empty()) return 1;
    const bool percent = raw.back() == '%';
    if (percent) raw.remove_suffix(1);
    double value = 1;
    if (!css_parse_double(raw,&value) || !std::isfinite(value)) return 1;
    return std::clamp(percent ? value / 100.0 : value,0.0,1.0);
}

LinearColor resolve_color(const ComputedStyle* style, std::string_view property) {
    ProfileScope prof(&g_paint_profile.colors);
    if (!style) return LinearColor::transparent();
    const int id = CssPropertyRegistry::instance().id_of(property);
    CssValuePtr custom;
    const CssValue* v;
    if (id == kCustomPropertyId) {
        // Public callers can resolve an unregistered/custom property too;
        // those have no parsed slot, so retain the on-demand behavior.
        custom = parse_css_value(style->get(property), nullptr);
        v = custom.get();
    } else {
        // Reuse the style's existing slot memo. Writes/unsets invalidate it,
        // and inherited reads share the current ancestor's entry.
        v = style->parsed(id);
    }
    if (!v || v->kind() != CssValueKind::Color) return LinearColor::transparent();
    const auto& c = static_cast<const CssColor&>(*v);
    return LinearColor::from_srgb(c.r, c.g, c.b, c.a);
}

bool resolve_transform(const ComputedStyle* style, const LayoutContext& ctx,
                       double font_size, double width, double height, Transform2D* out) {
    return parse_transform(style, ctx, font_size, width, height, out);
}

BorderRadii resolve_border_radii(const ComputedStyle* style, double width, double height,
                                 const LayoutContext& ctx, double font_size) {
    if (!style) return BorderRadii::zero();
    static const auto& registry = CssPropertyRegistry::instance();
    static const int top_left = registry.id_of("border-top-left-radius");
    static const int top_right = registry.id_of("border-top-right-radius");
    static const int bottom_right = registry.id_of("border-bottom-right-radius");
    static const int bottom_left = registry.id_of("border-bottom-left-radius");
    return BorderRadii(corner(style, top_left, ctx, font_size, width, height),
                       corner(style, top_right, ctx, font_size, width, height),
                       corner(style, bottom_right, ctx, font_size, width, height),
                       corner(style, bottom_left, ctx, font_size, width, height));
}

void paint_box_decorations(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                           double origin_x, double origin_y, Mesh* out, bool with_background,
                           bool skip_border) {
    const Box& b = tree[id];
    if (!b.style || b.width <= 0 || b.height <= 0) return;

    const BoxId parent = b.parent;
    const ComputedStyle* ps = parent == kNoBox ? nullptr : tree[parent].style;
    const double fs = font_size_px(b.style, ps, ctx);
    const Rect border_box(origin_x, origin_y, b.width, b.height);
    const BorderRadii radii = resolve_border_radii(b.style, b.width, b.height, ctx, fs);

    const LinearColor bg = resolve_color(b.style, "background-color");
    paint_resolved_decorations(b, border_box, radii, bg, out, with_background, skip_border);
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
    // Atlas entries live in stable map nodes; get() never erases them. A
    // bounded stack batch resolves each slot once, then reserves exactly the
    // geometry that has ink. Long whitespace runs do not reserve empty quads.
    constexpr size_t batch_size = 64;
    std::array<const GlyphSlot*, batch_size> slots;
    for (size_t start = 0; start < glyphs.size(); start += batch_size) {
        const size_t count = std::min(batch_size, glyphs.size() - start);
        size_t visible = 0;
        for (size_t i = 0; i < count; ++i) {
            slots[i] = paint.atlas->get(paint.font, face, glyphs[start + i].glyph, font_size);
            if (slots[i]) ++visible;
        }
        out->reserve_append(visible * 4, visible * 6);
        for (size_t index = 0; index < count; ++index) {
            const ShapedGlyph& g = glyphs[start + index];
            const GlyphSlot* slot = slots[index];
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
    std::vector<size_t> boundaries{0};
    Graphemes clusters(text, GraphemeProfile::BrowserCaret);
    size_t end = 0;
    while (clusters.next(&end)) boundaries.push_back(end);
    const auto width = [&](size_t index) {
        return text_advance(text.substr(0, boundaries[index]), font_size, paint, face, spacing);
    };
    size_t low = 0, high = boundaries.size() - 1;
    if (dx >= width(high)) return text.size();
    // LTR text advances are ordered. Binary search avoids shaping every
    // prefix of a long value while dragging its selection.
    while (high - low > 1) {
        const size_t mid = low + (high - low) / 2;
        if (width(mid) <= dx) low = mid;
        else high = mid;
    }
    return dx - width(low) < width(high) - dx ? boundaries[low] : boundaries[high];
}

}   // namespace

double input_text_scroll(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                         const PaintContext& paint, bool reveal_caret, double* out_maximum) {
    if (out_maximum) *out_maximum = 0;
    if (!tree.valid(box)) return 0;
    const Box& b = tree[box];
    if (!b.element || b.element != paint.caret.element || b.element->tag_name() != "input") return 0;
    ControlText text;
    if (!form_control_text(b, &text) || text.placeholder) return 0;
    const double width = b.width - b.border_left - b.border_right - b.padding_left - b.padding_right;
    if (width <= 1) return 0;
    const auto* parent_style = b.parent == kNoBox ? nullptr : tree[b.parent].style;
    const double fs = font_size_px(b.style, parent_style, ctx);
    const auto face = face_for_run(b, ctx, paint);
    const double spacing = letter_spacing_of(b.style, ctx, fs);
    const double total = text_advance(text.text, fs, paint, face, spacing);
    const double maximum = std::max(0.0, total - width + 1);
    if (out_maximum) *out_maximum = maximum;
    double scroll = std::clamp(paint.caret.text_scroll_x, 0.0, maximum);
    if (!reveal_caret) return scroll;
    const size_t offset = text.display_offset(static_cast<size_t>(std::max(0, paint.caret.index)));
    const double caret = text_advance(std::string_view(text.text).substr(0, offset), fs, paint, face, spacing);
    if (caret < scroll) scroll = caret;
    else if (caret - scroll > width - 1) scroll = caret - width + 1;
    return std::clamp(scroll, 0.0, maximum);
}

static Rect transform_layout_rect(const BoxTree& tree, BoxId target, const LayoutContext& ctx, Rect rect) {
    double x = 0, y = 0;
    Transform2D transform;
    for (BoxId id = target; id != kNoBox; id = tree[id].parent) {
        const Box& ancestor = tree[id];
        if (ancestor.kind == BoxKind::Text || ancestor.kind == BoxKind::Line ||
            ancestor.kind == BoxKind::AnonymousBlock || ancestor.kind == BoxKind::AnonymousInline || !ancestor.style) continue;
        const auto* parent_style = ancestor.parent == kNoBox ? nullptr : tree[ancestor.parent].style;
        const double fs = font_size_px(ancestor.style, parent_style, ctx);
        Transform2D local;
        if (!parse_transform(ancestor.style, ctx, fs, ancestor.width, ancestor.height, &local)) continue;
        visual_position(tree, id, &x, &y);
        transform = transform.multiply(Transform2D::translate(static_cast<float>(-x), static_cast<float>(-y))
            .multiply(local).multiply(Transform2D::translate(static_cast<float>(x), static_cast<float>(y))));
    }
    const double xs[] = {rect.x, rect.x + rect.width, rect.x + rect.width, rect.x};
    const double ys[] = {rect.y, rect.y, rect.y + rect.height, rect.y + rect.height};
    double left = 0, top = 0, right = 0, bottom = 0;
    for (int i = 0; i < 4; ++i) {
        transform.apply(xs[i], ys[i], &x, &y);
        if (i == 0) { left = right = x; top = bottom = y; }
        else { left = std::min(left, x); right = std::max(right, x); top = std::min(top, y); bottom = std::max(bottom, y); }
    }
    rect = Rect(left, top, right - left, bottom - top);
    return rect;
}

bool text_caret_bounds(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                       const PaintContext& paint, Rect* out) {
    if (!out || !tree.valid(box) || !paint.font || !paint.atlas) return false;
    const Box& field = tree[box];
    if (!field.element || field.width <= 0 || field.height <= 0) return false;
    const BoxId target = tree.valid(paint.caret.run) ? paint.caret.run : box;
    const Box& b = tree[target];
    double x = 0, y = 0;
    visual_position(tree, target, &x, &y);
    const auto color = resolve_color(b.style, "color");
    const auto face = face_for_run(b, ctx, paint);
    if (target != box) {
        const size_t offset = std::min(paint.caret.run_offset, b.text.size());
        x += measured_width(b.text.substr(0, offset), b, color, paint, face,
                            letter_spacing_of(b.style, ctx, b.font_size) + b.justify_letter_spacing);
        *out = Rect(x, y, 1, b.height > 0 ? b.height : b.font_size);
    } else {
        const auto* parent_style = b.parent == kNoBox ? nullptr : tree[b.parent].style;
        const double fs = font_size_px(b.style, parent_style, ctx);
        FontInterfaceMetrics default_metrics(paint.font, face);
        const FontMetrics* metrics = control_metrics(ctx, b.style);
        if (!metrics) metrics = &default_metrics;
        const double line_height = metrics->line_height(fs);
        const double content_height = b.height - b.border_top - b.border_bottom - b.padding_top - b.padding_bottom;
        ControlText text;
        form_control_text(b, &text);
        const size_t offset = text.placeholder ? 0 : text.display_offset(static_cast<size_t>(std::max(0, paint.caret.index)));
        const double advance = text_advance(std::string_view(text.text).substr(0, offset), fs, paint, face,
                                            letter_spacing_of(b.style, ctx, fs));
        const double top = control_text_offset(b, text.centered, content_height, *metrics, fs);
        const double visible_top = std::max(0.0, top);
        const double visible_bottom = std::min(content_height, top + line_height);
        *out = Rect(x + b.border_left + b.padding_left + advance - paint.caret.text_scroll_x,
                    y + b.border_top + b.padding_top + visible_top,
                    1, std::max(0.0, visible_bottom - visible_top));
    }
    *out = transform_layout_rect(tree, target, ctx, *out);
    return out->height > 0;
}

static size_t mapped_offset_nearest(const Box& run, double x, double spacing,
                                    FaceHandle face, const PaintContext& paint) {
    if (run.preserved_tab) return x < run.width * .5 ? 0 : 1;
    return offset_nearest(run.text, x, run.font_size, spacing, face, paint);
}

bool navigate_text_line(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                        const PaintContext& paint, int direction, double* preferred_x,
                        size_t* offset, bool* downstream) {
    if (!tree.valid(box) || !tree[box].element) return false;
    const std::string_view source = tree[box].element->form_edit_value();
    if (source.empty()) { *offset = 0; *downstream = false; return true; }
    struct Run { BoxId id, line; double x; size_t start; };
    std::vector<Run> runs;
    const auto collect = [&](auto&& self, BoxId id, double x) -> void {
        for (BoxId child = tree[id].first_child; child != kNoBox; child = tree[child].next_sibling) {
            const auto& b = tree[child];
            const double cx = x + b.x;
            const auto source_at = text_source_offset(b, source);
            if (b.kind == BoxKind::Text && tree[id].kind == BoxKind::Line &&
                b.source_control == tree[box].element && source_at)
                runs.push_back({child, id, cx, *source_at});
            self(self, child, cx);
        }
    };
    collect(collect, box, 0);
    if (runs.empty()) return false;
    const size_t index = std::min(source.size(), size_t(std::max(0, paint.caret.index)));
    size_t current = 0;
    bool found = false;
    for (size_t i = 0; i < runs.size(); ++i) {
        const auto& r = runs[i];
        if (index >= r.start && index <= r.start + text_source_length(tree[r.id])) {
            current = i; found = true;
            if (!paint.caret.downstream) break;
        } else if (!found && r.start <= index) current = i;
    }
    const Run& active = runs[current];
    const Box& active_box = tree[active.id];
    if (!std::isfinite(*preferred_x)) {
        const auto face = face_for_run(active_box, ctx, paint);
        *preferred_x = active.x + measured_width(active_box.text.substr(0, source_to_display(active_box, index > active.start ? index - active.start : 0)),
            active_box, {}, paint, face,
            letter_spacing_of(active_box.style, ctx, active_box.font_size) + active_box.justify_letter_spacing);
    }
    size_t first = current, last = current;
    while (first > 0 && runs[first - 1].line == active.line) --first;
    while (last + 1 < runs.size() && runs[last + 1].line == active.line) ++last;
    if (direction == -2) { *offset = runs[first].start; *downstream = true; return true; }
    if (direction == 2) {
        *offset = runs[last].start + text_source_length(tree[runs[last].id]);
        *downstream = false; return true;
    }
    if (direction == -3) {
        const size_t boundary = index == 0 ? std::string_view::npos : source.rfind('\n', index - 1);
        if (boundary == std::string_view::npos) { *offset = 0; *downstream = true; return true; }
        last = 0;
        while (last + 1 < runs.size() && runs[last + 1].start <= boundary) ++last;
        first = last;
        while (first > 0 && runs[first - 1].line == runs[last].line) --first;
    } else if (direction == 3) {
        const size_t boundary = source.find('\n', index);
        if (boundary == std::string_view::npos) { *offset = source.size(); *downstream = false; return true; }
        first = 0;
        while (first + 1 < runs.size() && runs[first].start < boundary + 1) ++first;
        last = first;
        while (last + 1 < runs.size() && runs[last + 1].line == runs[first].line) ++last;
    } else if (direction < 0) {
        if (first == 0) { *offset = 0; *downstream = true; return true; }
        last = first - 1; first = last;
        while (first > 0 && runs[first - 1].line == runs[last].line) --first;
    } else {
        if (last + 1 == runs.size()) { *offset = source.size(); *downstream = false; return true; }
        first = last + 1; last = first;
        while (last + 1 < runs.size() && runs[last + 1].line == runs[first].line) ++last;
    }
    size_t best = first;
    double distance = 1e300;
    for (size_t i = first; i <= last; ++i) {
        const auto& r = runs[i];
        const double d = std::max({r.x - *preferred_x, *preferred_x - r.x - tree[r.id].width, 0.0});
        if (d < distance) { distance = d; best = i; }
    }
    const Run& target = runs[best];
    const Box& run = tree[target.id];
    *offset = target.start + mapped_offset_nearest(run, *preferred_x - target.x,
        letter_spacing_of(run.style, ctx, run.font_size) + run.justify_letter_spacing,
        face_for_run(run, ctx, paint), paint);
    *downstream = *offset == runs[first].start;
    return true;
}

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
    const double scroll = paint.caret.element == b.element ? paint.caret.text_scroll_x : 0;
    return t.source_offset(offset_nearest(t.text, x - content_left + scroll, fs,
                         letter_spacing_of(b.style, ctx, fs), face_for_run(b, ctx, paint), paint));
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
            const auto source_at = text_source_offset(cb, source);
            if (cb.kind == BoxKind::Text && tree[cb.parent].kind == BoxKind::Line &&
                source_at) {
                runs.push_back({c, cx, cy, cb.height,
                                *source_at});
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
    double best_x_distance = 1e300;
    for (const Candidate& r : runs) {
        const double centre = r.y + r.height * 0.5;
        const double distance = std::fabs(y - centre);
        const double x_distance = std::max({r.x - x, x - r.x - tree[r.id].width, 0.0});
        if (distance < best_distance || (distance == best_distance && x_distance < best_x_distance)) {
            best_distance = distance;
            best_x_distance = x_distance;
            best = &r;
        }
    }
    // Then the character within that line's run, or the one nearest on it.
    const Box& run = tree[best->id];
    return best->offset + mapped_offset_nearest(run, x - best->x,
                                         letter_spacing_of(run.style, ctx, run.font_size) +
                                             run.justify_letter_spacing,
                                         face_for_run(run, ctx, paint), paint);
}

std::vector<const Element*> select_options(const Element& select) {
    return form_options(select);
}

SelectListGeometry select_list_geometry(const BoxTree& tree, BoxId select_box,
                                        const LayoutContext& ctx, const Element& select) {
    SelectListGeometry g;
    if (!tree.valid(select_box)) return g;
    const Box& b = tree[select_box];
    const std::vector<const Element*> options = select_rows(select);
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
    // hundred options do not run off the bottom of the world. Past the cap the
    // list SCROLLS rather than hiding what does not fit, which is the whole
    // difference between a long list and a truncated one.
    const double height = std::min(g.row_height * g.count, 320.0);
    g.rows = std::max(1, static_cast<int>(height / g.row_height));
    const Rect anchor = transform_layout_rect(tree, select_box, ctx, Rect(x, y, b.width, b.height));
    if (anchor.width <= 0 || anchor.height <= 0) return g;
    g.box = Rect(anchor.x, anchor.bottom(), anchor.width, height);
    // Flipped above when there is no room below, the way a native list does.
    if (g.box.bottom() > ctx.viewport_height_px && anchor.y - height >= 0) {
        g.box.y = anchor.y - height;
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
    const auto rows = select_rows(*select);
    const auto* highlighted = paint.popup.highlighted >= 0 && paint.popup.highlighted < static_cast<int>(options.size())
        ? options[static_cast<size_t>(paint.popup.highlighted)] : nullptr;

    // Nothing above it clips it: the list is drawn over whatever it opens on
    // top of, which is the whole point of a dropdown.
    paint.backend->set_scissor(nullptr);
    const Box& b = tree[box];
    const ComputedStyle* ps = b.parent == kNoBox ? nullptr : tree[b.parent].style;
    const double fs = b.style ? font_size_px(b.style, ps, ctx) : ctx.root_font_size_px;
    // The control's own colours, so a styled select gets a list that matches
    // rather than a white box in the middle of a dark page.
    LinearColor background = resolve_color(b.style, "background-color");
    if (background.a < 0.9f) background = LinearColor::from_srgb(255, 255, 255, 1.0f);
    const LinearColor text = resolve_color(b.style, "color");
    const LinearColor border = LinearColor(text.r, text.g, text.b, 0.35f);

    Mesh panel;
    tessellate_rect(g.box, background, &panel, false);
    draw_mesh(std::move(panel), paint.backend, {}, 1.0, nullptr, nullptr, nullptr);
    // A hairline border, so the list reads as being above the page.
    for (const Rect& edge : {Rect(g.box.x, g.box.y, g.box.width, 1),
                             Rect(g.box.x, g.box.bottom() - 1, g.box.width, 1),
                             Rect(g.box.x, g.box.y, 1, g.box.height),
                             Rect(g.box.right() - 1, g.box.y, 1, g.box.height)}) {
        Mesh line;
        tessellate_rect(edge, border, &line, false);
        draw_mesh(std::move(line), paint.backend, {}, 1.0, nullptr, nullptr, nullptr);
    }

    const Recti clip{static_cast<int>(std::ceil(g.box.x + 1)), static_cast<int>(std::ceil(g.box.y + 1)),
                     std::max(0, static_cast<int>(std::floor(g.box.width - 2))),
                     std::max(0, static_cast<int>(std::floor(g.box.height - 2)))};
    paint.backend->set_scissor(&clip);
    const int first = std::clamp(paint.popup.first_row, 0, std::max(0, g.count - g.rows));
    for (int row = 0; row < g.rows && first + row < g.count; ++row) {
        const int i = first + row;
        const auto* entry = rows[static_cast<size_t>(i)];
        const bool group = entry->tag_name() == "optgroup";
        const bool disabled = group ? entry->has_attribute("disabled") : option_disabled(*entry);
        const bool active = entry == highlighted && !group && !disabled;
        const double row_y = g.box.y + row * g.row_height;
        if (active) {
            Mesh band;
            tessellate_rect(Rect(g.box.x + 1, row_y, g.box.width - 2, g.row_height),
                            LinearColor::from_srgb(51, 144, 255, 0.85f), &band, false);
            draw_mesh(std::move(band), paint.backend, {}, 1.0, nullptr, nullptr, nullptr);
        }
        const std::string label = group ? std::string(entry->get_attribute("label")) : option_label(*entry);
        if (label.empty()) continue;
        const auto row_text = popup_text_style(*entry, b, fs, ctx, paint);
        const FontMetrics* metrics = control_metrics(ctx, row_text.style);
        const double ascent = metrics ? metrics->ascent(row_text.size) : row_text.size * 0.8;
        LinearColor ink = active ? LinearColor::from_srgb(255, 255, 255, 1.f) : resolve_color(row_text.style, "color");
        if (disabled) ink.a *= 0.5f;
        const double indent = !group && entry->parent() != select ? 20 : 6;
        Mesh glyphs;
        build_text_geometry(label, g.box.x + indent, row_y + (g.row_height - row_text.size) * 0.5 + ascent, row_text.size, ink,
                            paint, &glyphs, letter_spacing_of(row_text.style, ctx, row_text.size), &row_text.face);
        draw_mesh(std::move(glyphs), paint.backend, atlas_texture, 1.0, nullptr, nullptr, nullptr);
    }
    paint.backend->set_scissor(nullptr);
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
        if (paint.reuse) paint.reuse->begin_glyphs(*paint.atlas);
        prepare_glyphs(tree, root, ctx, paint);
        prepare_popup_glyphs(tree, ctx, paint);
        atlas_texture = paint.atlas->texture(paint.backend);
        glyphs_ms =
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    }
    if (paint.reuse) paint.reuse->begin_paint(atlas_texture);
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
                     "layers)  text %6.2f  backgrounds %6.2f  filters %6.2f  rest %6.2f  (total %6.2f ms)"
                     "  [submit %6.2f in %ld draws, %ld clipped]"
                     "  [%d shadow tex: raster %5.2f blur %5.2f punch %5.2f upload %5.2f]"
                     "  [%d filter tex: raster %5.2f blur %5.2f upload %5.2f]"
                     "  [of which: decoration build %5.2f draw %5.2f; colors %5.2f]"
                     "  [%d glyph subtrees reused]\n",
                     glyphs_ms, p.shadows, p.shadow_tess, p.shadow_draw, p.shadow_layers, p.text,
                     p.backgrounds, p.filters,
                     total - glyphs_ms - p.shadows - p.text - p.backgrounds - p.filters, total,
                     p.submit, p.submit_calls, p.submit_clipped, p.shadow_textures,
                     p.shadow_raster, p.shadow_blur, p.shadow_punch, p.shadow_upload,
                     p.filter_textures, p.filter_raster, p.filter_blur, p.filter_upload,
                     p.decoration_build, p.decoration_draw, p.colors, p.glyph_subtrees_reused);
    }
}

} // namespace weva
