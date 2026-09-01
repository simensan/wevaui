#pragma once
#include "weva/box.h"
#include "weva/render_interface.h"
#include "weva/style_resolver.h"
#include "weva/font_interface.h"
#include "weva/glyph_atlas.h"
#include "weva/tessellate.h"

namespace weva {

// Walks a laid-out box tree and issues draws through the render interface.
//
// Ports the role of Runtime/Paint/BoxToPaintConverter.cs, but at the lower
// altitude ARCHITECTURE.md §1 chose: the C# emits semantic commands and leaves
// each backend to work out rounded-rect coverage and border geometry; this
// emits triangles.

// Resolves the four corner radii from a style against the box's own size, since
// a percentage radius is relative to the border box.
BorderRadii resolve_border_radii(const ComputedStyle* style, double width, double height,
                                 const LayoutContext& ctx, double font_size);

// Resolves a colour-valued property. Returns transparent when absent or
// unparseable, so a bad declaration paints nothing rather than black.
LinearColor resolve_color(const ComputedStyle* style, std::string_view property);

// Paints `root` and its subtree. Boxes are walked in tree order, which is
// document order — stacking contexts and z-index ordering are a later slice, so
// a positive z-index does not yet lift a box above a later sibling.
// Everything paint needs beyond the box tree. A null font or atlas means text
// is skipped and the rest still paints, so a host without a font backend gets
// boxes rather than nothing.
struct PaintContext {
    RenderInterface* backend = nullptr;
    FontInterface* font = nullptr;
    GlyphAtlas* atlas = nullptr;
    FaceHandle face;
    // Textures paint generates for this pass (rasterized gradient layers).
    // The caller owns their release — after the host has consumed the draws
    // that reference them, typically at the start of the next pass. Null
    // means paint leaks nothing it can avoid and generates them anyway.
    std::vector<TextureHandle>* owned_textures = nullptr;
};

void paint_tree(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                RenderInterface* backend);
void paint_tree(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                const PaintContext& paint);

// Builds the quads for one text run: one textured quad per glyph, all sharing
// the atlas, so a run is a single draw. `x` and `y` are the run's origin, and
// the glyphs sit on the baseline at `baseline_y`.
// `letter_spacing` is added after every glyph, as layout added it when it
// measured the run; without it each word draws narrower than the box that
// was laid out for it and the gaps between words grow.
void build_text_geometry(std::string_view text, double x, double baseline_y, double font_size,
                         const LinearColor& color, const PaintContext& paint, Mesh* out,
                         double letter_spacing = 0, const FaceHandle* face = nullptr);

// Builds the mesh for one box's background and border, without issuing any
// draw. Exposed because it is far easier to assert geometry than backend calls.
void paint_box_decorations(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                           double origin_x, double origin_y, Mesh* out,
                           bool with_background = true);

} // namespace weva
