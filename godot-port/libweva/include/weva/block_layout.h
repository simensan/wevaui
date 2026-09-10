#pragma once
#include "weva/box.h"
#include "weva/computed_style.h"
#include "weva/css_properties.h"
#include "weva/font_metrics.h"
#include "weva/inline_layout.h"

#include <map>
#include <array>
#include "weva/style_resolver.h"

#include <string_view>
#include <vector>

// Ports the box-model half of Runtime/Layout/BlockLayout.cs — resolving an
// element's padding, border, margin and width against its containing block.
// Block flow itself (child stacking, margin collapsing, floats) is the next
// slice.

namespace weva {

// Place a normal-flow block within its containing inline width. Flex/grid,
// floats, inline boxes and positioned boxes have separate alignment rules.
double resolve_block_inline_offset(Box& box, double containing_width, bool rtl);

struct ResolvedSides {
    double top = 0, right = 0, bottom = 0, left = 0;
    // The authored text of the inline edges, kept because `auto` margins are a
    // keyword the resolved pixel value cannot represent.
    std::string_view right_raw = "0";
    std::string_view left_raw = "0";
};

// Percentage padding and margin resolve against the containing block's WIDTH on
// every edge, top and bottom included (CSS 2.1 §8.3, §8.4).
ResolvedSides resolve_box_sides_px(const ComputedStyle* style, int shorthand_id,
                                   const LayoutContext& ctx, double font_size,
                                   double containing_block_width, double line_height = 0);

ResolvedSides resolve_box_sides_px(const ComputedStyle* style, std::string_view shorthand,
                                   const LayoutContext& ctx, double font_size,
                                   double containing_block_width, double line_height);

// A border edge whose style is `none` or `hidden` has zero width regardless of
// the declared border-width (CSS Backgrounds §4.3).
ResolvedSides resolve_border_edges(const ComputedStyle* style, const LayoutContext& ctx,
                                   double font_size);

// CSS Basic UI §4.1. Initial is content-box; only `border-box` flips it.
//
// Inline, and in the header, because it is asked from block, flex, grid,
// inline, table and positioning -- every one of those a cross-translation-unit
// call, several times per box per pass, to read one byte's worth of answer.
// Caller attribution put it at 50 samples on layout-stress from apply_box_model
// alone.
//
// The id is a namespace-scope inline variable rather than a function-local
// static so there is no guard to check on each call. The registry is itself a
// function-local static, so it is constructed on first use and this cannot
// outrun it.
inline const int kBoxSizingId = CssPropertyRegistry::instance().id_of("box-sizing");

inline bool is_border_box(const ComputedStyle* style) {
    return style && style->get(kBoxSizingId) == "border-box";
}

PositionType parse_position_type(std::string_view raw);

// Shared by layout and intrinsic contribution measurement, so flex/grid parents
// cannot accidentally recover content sizes suppressed by containment.
bool has_size_containment(const ComputedStyle* style);
bool has_inline_size_containment(const ComputedStyle* style);
double contain_intrinsic_width(const ComputedStyle* style, const LayoutContext& ctx, double font_size);

// Resolves the box model onto `box`: padding, border and margin edges, the
// used width, and a definite height when one is available. Returns the
// element's resolved font size, which the caller reuses for its content.
//
// `box.width` and `box.height` are always BORDER-box values — paint and hit
// testing treat them as the outer rect — so under the default content-box
// sizing the frame is added before stamping.
double apply_box_model(BoxTree* tree, BoxId id, double containing_block_width,
                       const ComputedStyle* parent_style, const LayoutContext& ctx);

// ---- Margin collapsing (CSS Box Model L3 §8.3.1) -------------------------

// Two adjoining margins collapse to max() when both are positive, min() when
// both are negative, and their algebraic sum when the signs differ. A NaN input
// is treated as absent rather than propagated — one bad calc() would otherwise
// corrupt every block below it.
double collapse_margins(double a, double b);

// True for boxes whose vertical margins collapse with adjacent block siblings.
// Inline-blocks, floats and out-of-flow boxes are excluded and act as barriers.
bool participates_in_flow(const Box& b);
bool is_out_of_flow(const Box& b);

// CSS 2.1 §9.4.1: overflow other than visible, a display that is flow-root /
// flex / grid / inline-block / table-ish, an absolute or fixed position, or a
// float. A BFC root's own margins never collapse with its in-flow children.
bool establishes_new_bfc(const Box& b);

// The parent's top (bottom) edge is "open" when nothing — padding, border, or a
// BFC — sits between it and its first (last) in-flow child. An explicit height
// does NOT close the top; it only blocks bottom collapsing.
bool parent_top_open(const Box& b);
bool parent_bottom_open(const Box& b);
bool parent_height_auto(const Box& b, const LayoutContext& ctx, double font_size);

// A block with no padding, border, height, min-height or in-flow content: its
// own top and bottom margins collapse with each other and it contributes no
// height to the flow.
bool is_self_collapsing(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                        double font_size);

// ---- Floats (CSS 2.1 §9.5) -----------------------------------------------

// `inline-start` / `inline-end` alias to left / right, which is correct only in
// horizontal-tb LTR — the only writing mode the float code handles.
FloatType parse_float_type(std::string_view raw);
ClearType parse_clear_type(std::string_view raw);

// One instance per block formatting context: floats are scoped to the BFC that
// contains them and never escape it. Coordinates are BFC-local, and each entry
// is the float's MARGIN box, since that is what later floats and line boxes
// must not overlap (§9.5.1 rule 1).
class FloatContext {
public:
    struct Entry {
        BoxId box = kNoBox;
        FloatType side = FloatType::None;
        double top = 0, bottom = 0, left = 0, right = 0;
    };

    void add(const Entry& e) { floats_.push_back(e); }
    int count() const { return static_cast<int>(floats_.size()); }

    // How far a left float intrudes from the BFC's left content edge at `y`,
    // and how far a right float intrudes from the RIGHT edge. Both return the
    // inward distance, which is what line-box narrowing needs.
    double left_extent_at(double y) const;
    double right_extent_at(double y, double cb_width) const;

    // The lowest y at or below `y` where `width` of horizontal space is free.
    // A float that does not fit steps down to the first row where it does; if
    // no row ever fits it, it stays at the lowest examined y and overflows,
    // which is what CSS 2.1 specifies.
    double find_placement_y(double y, double width, FloatType side, double cb_width) const;

    // §9.5.2: the highest bottom edge among floats matching `clear`. A cleared
    // block's border edge (float's margin edge) is pushed below this. `found`
    // distinguishes no matching float from a float ending at or above zero.
    double clear_bottom(ClearType c, bool* found = nullptr) const;
    // §10.6.7: a BFC grows to enclose the floats it contains.
    double max_bottom() const;
    double max_top() const;
    // Intersect a containing-block band with preceding float margin boxes.
    // Returns the next bottom edge where a blocked band can become wider.
    double available_band(double top, double bottom, double* left, double* right) const;

private:
    std::vector<Entry> floats_;
};

// ---- Block flow ----------------------------------------------------------

// Lays out a block container and its in-flow block descendants.
//
// Ported: the box model, the margin-collapse chain, and auto-height
// finalisation. NOT ported yet, so the corresponding boxes are laid out as
// ordinary in-flow blocks: floats and `clear`, out-of-flow positioning, inline
// formatting, and the flex / grid / table / multicol modes.
class LayoutReuse {
public:
    virtual ~LayoutReuse() = default;
    // The box model and imposed width are already resolved. A miss must
    // materialize deferred children before ordinary layout resumes.
    virtual bool reuse_layout(BoxTree* tree, BoxId id) = 0;
};

class BlockLayout {
public:
    // `metrics` may be null, in which case a container of inline content
    // reports zero height — layout still runs, it just cannot measure text.
    BlockLayout(BoxTree* tree, const LayoutContext& ctx, const FontMetrics* metrics = nullptr,
                LayoutReuse* reuse = nullptr)
        : tree_(tree), ctx_(ctx), metrics_(metrics), reuse_(reuse) {}

    // Seeds the synthetic root with the viewport box. The height seed matters:
    // it is what lets `html, body { height: 100% }` chain down, since a
    // percentage height needs a definite basis to resolve against.
    void layout_root(BoxId root, double viewport_width, double viewport_height);
    void layout_block(BoxId id, double available_width, const ComputedStyle* parent_style);

    // Re-lays a box's content at a new width, keeping its own outer geometry.
    // The positioning pass needs it: an out-of-flow box's width is only known
    // after block layout has run, and its children were sized against the
    // provisional one.
    void relayout_at(BoxId id, double width);

    // Re-lays a box's content with BOTH axes imposed from outside — what a flex
    // line does to a stretched item. The height survives the re-layout, which
    // relayout_at alone does not guarantee: the auto-height rule would collapse
    // it back to the content.
    void relayout_at_size(BoxId id, double width, double height);

    // CSS 2.1 §10.3.5. Used by floats, inline-block atoms and grid items that
    // are not stretched, all of which hug their content rather than filling
    // their containing block. Returns the box's font size.
    double shrink_to_fit(BoxId id, double available_width, const ComputedStyle* parent_style);

private:
    void layout_content(BoxId id, double font_size, double containing_block_width,
                        const ComputedStyle* parent_style);
    void finalize_block_size(BoxId id, double font_size, double content_bottom_y);
    void layout_float_box(BoxId id, double containing_block_width);
    void relayout_content_at(BoxId id, double width, double font_size,
                             const ComputedStyle* parent_style);
    void size_atoms(std::vector<InlineItem>* items, double available_width,
                    const ComputedStyle* parent_style);
    // The one entry point for laying out a container's inline content, so the
    // item cache is written and read by the same path.
    double layout_inline_content(BoxId id, double content_width,
                                 const ComputedStyle* parent_style);
    void place_float(BoxId container, BoxId float_box, double top_y, double content_w);

    BoxTree* tree_;
    LayoutContext ctx_;
    const FontMetrics* metrics_ = nullptr;
    LayoutReuse* reuse_ = nullptr;
    // Inline items per container, collected once. A shrink-to-fit probe lays
    // the same container out three times, and the first pass replaces its
    // children with line boxes — so the source runs must not be re-walked.
    struct InlineLayoutEntry {
        std::vector<InlineItem> items;
        bool plain_text = false;
        struct Input {
            double width = 0, top = 0, left = 0;
            const ComputedStyle* style = nullptr;
            const ComputedStyle* parent_style = nullptr;
            int64_t style_version = 0, parent_version = 0;
            bool operator==(const Input& o) const {
                return width == o.width && top == o.top && left == o.left &&
                    style == o.style && parent_style == o.parent_style &&
                    style_version == o.style_version && parent_version == o.parent_version;
            }
        };
        struct Result {
            Input input;
            double height = 0;
            struct Line {
                BoxId id = kNoBox;
                double x = 0, y = 0;
            };
            std::array<Line, 8> lines{};
            size_t count = 0;
            bool ready = false;
        };
        // Intrinsic probes and the final width commonly alternate. Retain a
        // few small results without allocating another vector per probe.
        std::array<Result, 3> results;
        size_t next_result = 0;
    };
    std::map<BoxId, InlineLayoutEntry> inline_items_;
    // The float context of the BFC currently being laid out, and where that
    // BFC's origin sits in the coordinates of the box being laid out. Both are
    // saved and restored around each BFC boundary.
    FloatContext* current_floats_ = nullptr;
    double bfc_origin_x_ = 0;
    double bfc_origin_y_ = 0;
};

} // namespace weva
