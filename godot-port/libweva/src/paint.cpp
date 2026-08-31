#include "weva/paint.h"

#include "weva/block_layout.h"
#include "weva/css_value.h"

#include <cmath>

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

void paint_recursive(const BoxTree& tree, BoxId id, const LayoutContext& ctx, double origin_x,
                     double origin_y, RenderInterface* backend) {
    const Box& b = tree[id];
    const double x = origin_x + b.x;
    const double y = origin_y + b.y;

    Mesh mesh;
    paint_box_decorations(tree, id, ctx, x, y, &mesh);
    if (!mesh.empty()) {
        // Compiled and released per box for now. A backend that batches will
        // want geometry to outlive a frame; that needs the paint cache, which
        // is keyed on style and layout versions and is a later slice.
        const GeometryHandle g = backend->compile_geometry(mesh.vertices, mesh.indices);
        backend->render_geometry(g, {0, 0}, {});
        backend->release_geometry(g);
    }

    for (BoxId c : tree.children(id)) paint_recursive(tree, c, ctx, x, y, backend);
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
                           double origin_x, double origin_y, Mesh* out) {
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
    if (bg.a > 0) tessellate_rounded_rect(border_box, radii, bg, out);

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

void paint_tree(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                RenderInterface* backend) {
    if (!backend || root == kNoBox) return;
    paint_recursive(tree, root, ctx, 0, 0, backend);
}

} // namespace weva
