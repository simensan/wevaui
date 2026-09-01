#include "weva/paint.h"

#include "weva/background.h"
#include "weva/block_layout.h"
#include "weva/css_value.h"

#include <algorithm>
#include <cmath>
#include <optional>
#include <vector>

namespace weva {

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
void draw_mesh(const Mesh& mesh, RenderInterface* backend, TextureHandle tex, double opacity = 1,
               const Transform2D* xform = nullptr) {
    if (mesh.empty()) return;
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
    if (sigma <= 0) return e < 0 ? 1.0 : 0.0;
    return 0.5 * std::erfc(e / (sigma * 1.4142135623730951));
}

// The blur is approximated with K nested shapes drawn outermost first, each
// with the alpha that brings the accumulated coverage to the Gaussian's value
// at its edge. No blur pass exists in the canvas; K = 12 reads as smooth at
// the blur radii the samples use (6-24px).
constexpr int kShadowLayers = 12;

void paint_outer_shadows(const std::vector<Shadow>& shadows, const Rect& border_box,
                         const BorderRadii& radii, RenderInterface* backend, double opacity,
                         const Transform2D* xf = nullptr) {
    for (size_t s = shadows.size(); s-- > 0;) {   // first shadow on top
        const Shadow& sh = shadows[s];
        if (sh.inset) continue;
        const double sigma = sh.blur * 0.5;
        const int layers = sh.blur > 0 ? kShadowLayers : 1;
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
            Mesh mesh;
            tessellate_rounded_rect(r, clamp_radii_to_rect(grow_radii(radii, grow), r.width, r.height),
                                    c, &mesh);
            draw_mesh(mesh, backend, {}, opacity, xf);
        }
    }
}

// An inset shadow darkens the padding box from its edges inward; the offset
// deepens the sides it points away from. Rendered as nested frames.
void paint_inset_shadows(const std::vector<Shadow>& shadows, const Rect& padding_box,
                         const BorderRadii& radii, RenderInterface* backend, double opacity,
                         const Transform2D* xf = nullptr) {
    for (size_t s = shadows.size(); s-- > 0;) {
        const Shadow& sh = shadows[s];
        if (!sh.inset) continue;
        const double sigma = sh.blur * 0.5;
        const int layers = sh.blur > 0 ? kShadowLayers : 1;
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
            draw_mesh(mesh, backend, {}, opacity, xf);
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
};

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
void prepare_glyphs(const BoxTree& tree, BoxId id, const PaintContext& paint) {
    const Box& b = tree[id];
    if (b.kind == BoxKind::Text && !b.text.empty() && paint.font && paint.atlas) {
        std::vector<ShapedGlyph> glyphs;
        paint.font->shape(paint.face, b.text, b.font_size, &glyphs);
        for (const ShapedGlyph& g : glyphs) {
            paint.atlas->get(paint.font, paint.face, g.glyph, b.font_size);
        }
    }
    for (BoxId c : tree.children(id)) prepare_glyphs(tree, c, paint);
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
                              const Transform2D* xf = nullptr) {
    if (!has_gradient_layer(layers) || !paint.backend || area.width <= 0 || area.height <= 0) {
        return false;
    }
    const int tex_w = static_cast<int>(std::min(1024.0, std::ceil(area.width)));
    const int tex_h = static_cast<int>(std::min(1024.0, std::ceil(area.height)));
    std::vector<uint8_t> rgba;
    rasterize_background(layers, color, area.width, area.height, tex_w, tex_h, ctx, font_size, &rgba);
    const TextureHandle tex = paint.backend->generate_texture(rgba, {tex_w, tex_h});
    if (paint.owned_textures) paint.owned_textures->push_back(tex);

    Mesh mesh;
    tessellate_rounded_rect(area, radii, LinearColor::white(), &mesh);
    for (Vertex& v : mesh.vertices) {
        v.tex_coord = {static_cast<float>((v.position.x - area.x) / area.width),
                       static_cast<float>((v.position.y - area.y) / area.height)};
    }
    draw_mesh(mesh, paint.backend, tex, opacity, xf);
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
            draw_mesh(mesh, paint.backend, tex, state.opacity, xf);
            blurred = true;
        }
    }

    std::vector<Shadow> shadows;
    if (decorated && b.style && !hidden && !blurred && b.width > 0 && b.height > 0) {
        shadows = parse_box_shadows(b.style, ctx, fs, resolve_color(b.style, "color"));
        paint_outer_shadows(shadows, border_box, radii, paint.backend, state.opacity, xf);
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
                state.opacity, xf);
        }
    }

    if (decorated && !hidden && !blurred) {
        Mesh mesh;
        paint_box_decorations(tree, id, ctx, x, y, &mesh, !background_done);
        draw_mesh(mesh, paint.backend, {}, state.opacity, xf);
        if (!shadows.empty()) {
            const Rect padding_box(x + b.border_left, y + b.border_top,
                                   b.width - b.border_left - b.border_right,
                                   b.height - b.border_top - b.border_bottom);
            if (padding_box.width > 0 && padding_box.height > 0) {
                paint_inset_shadows(shadows, padding_box,
                                    inset_radii(radii, b.border_top, b.border_right, b.border_bottom,
                                                b.border_left),
                                    paint.backend, state.opacity, xf);
            }
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
        // text-shadow first, under the glyphs: the offset run in the shadow
        // colour. A blur is a 5x5 Gaussian kernel of glyph copies, sigma =
        // blur / 2, weights normalised so the stack's coverage approaches
        // the shadow's alpha at the centre — no blur pass exists in the
        // canvas, and text is cheap to draw 25 times.
        for (const TextShadow& sh : parse_text_shadows(b.style, ctx, b.font_size, text_color)) {
            if (sh.blur <= 0) {
                Mesh shadow;
                build_text_geometry(b.text, x + sh.x, baseline + sh.y, b.font_size, sh.color, paint,
                                    &shadow, spacing);
                draw_mesh(shadow, paint.backend, atlas_texture, state.opacity, xf);
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
                                        b.font_size, c, paint, &shadow, spacing);
                    draw_mesh(shadow, paint.backend, atlas_texture, state.opacity, xf);
                }
            }
        }
        Mesh text;
        build_text_geometry(b.text, x, baseline, b.font_size, text_color, paint, &text, spacing);
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
        draw_mesh(text, paint.backend, atlas_texture, state.opacity, xf);
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
    }

    for (BoxId c : tree.children(id)) {
        paint_recursive(tree, c, ctx, x, y, paint, atlas_texture, canvas_owner, state);
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
                         double letter_spacing) {
    if (!paint.font || !paint.atlas || text.empty()) return;
    std::vector<ShapedGlyph> glyphs;
    paint.font->shape(paint.face, text, font_size, &glyphs);

    double pen = x;
    for (const ShapedGlyph& g : glyphs) {
        const GlyphSlot* slot = paint.atlas->get(paint.font, paint.face, g.glyph, font_size);
        // A glyph with no bitmap — a space, or one the face does not have —
        // still advances the pen. Skipping the advance would close the gaps
        // between words.
        if (slot) {
            const double gx = pen + g.x_offset + slot->bearing_x;
            // bearing_y measures UP from the baseline, so the quad's top edge
            // is above it.
            const double gy = baseline_y - g.y_offset - slot->bearing_y;
            const uint32_t base = static_cast<uint32_t>(out->vertices.size());
            const auto v = [&](double px, double py, float u, float w) {
                Vertex vt;
                vt.position = {static_cast<float>(px), static_cast<float>(py)};
                vt.color = color;
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
        prepare_glyphs(tree, root, paint);
        atlas_texture = paint.atlas->texture(paint.backend);
    }
    const BoxId canvas_owner = paint_canvas(tree, root, ctx, paint);
    paint_recursive(tree, root, ctx, 0, 0, paint, atlas_texture, canvas_owner, PaintState{});
}

} // namespace weva
