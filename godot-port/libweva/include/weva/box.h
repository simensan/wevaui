#pragma once
#include "weva/computed_style.h"
#include "weva/dom.h"
#include "weva/table_border.h"

#include <deque>
#include <string>
#include <cstdint>
#include <optional>
#include <string_view>
#include <vector>
#include <map>
#include <algorithm>

// Ports Runtime/Layout/Boxes — the box tree that layout writes into and paint
// reads out of.
//
// Two structural departures from the C#, both required by docs/CONVENTIONS.md:
//
//  1. **One struct with a kind tag, not a class hierarchy.** The C# has
//     Box -> BlockBox -> AnonymousBlockBox and three sibling subclasses. RTTI is
//     off here and the tree is walked constantly, so a tag is both cheaper and
//     the only option that survives arena storage.
//  2. **Stable indices, not pointers.** Boxes live in one contiguous vector that
//     is reset (not freed) between passes, so any pointer into it is invalidated
//     by the next allocation. A BoxId stays valid.
//
// The children list is an intrusive sibling chain rather than a per-box
// std::vector. The C# `List<Box>` per box is a per-frame allocation each, which
// the zero-allocation-per-frame gate does not allow.

namespace weva {

using BoxId = int32_t;
constexpr BoxId kNoBox = -1;

enum class BoxKind : uint8_t {
    Block,
    AnonymousBlock,   // a block wrapper generated around inline content
    Inline,
    AnonymousInline,
    Line,             // one line box inside an inline formatting context
    Text,             // a run of text with a single style
};

// The resolved `display` value. The C# encodes this as a subclass tower
// (FlexBox, GridBox, TableBox, MulticolBox, TableRowGroupBox, ...); with one
// box struct it has to be a field, which is also what lets a box change
// formatting context without being reallocated.
enum class DisplayKind : uint8_t {
    None,
    Contents,
    Inline,
    Block,
    FlowRoot,
    ListItem,
    Flex,
    Grid,
    Table,
    TableRowGroup,
    TableHeaderGroup,
    TableFooterGroup,
    TableRow,
    TableCell,
    TableCaption,
    TableColumn,
    TableColumnGroup,
    InlineBlock,
    InlineFlex,
    InlineGrid,
    InlineTable,
};

// Unrecognised values compute to `inline`, which is also the initial value —
// so an author typo degrades the way an omitted declaration would.
DisplayKind parse_display(std::string_view value);
bool try_parse_display(std::string_view value, DisplayKind* out);
const char* display_name(DisplayKind d);

// CSS 2.1 §17.4: table-internal displays are block-level for the purpose of
// child classification, so the anonymous-block pass does not sweep them in
// beside inline siblings.
bool is_table_display(DisplayKind d);

enum class PositionType : uint8_t { Static, Relative, Absolute, Fixed, Sticky };
enum class FloatType : uint8_t { None, Left, Right };
enum class ClearType : uint8_t { None, Left, Right, Both };

// The measurements a flex/grid parent consumed BEFORE allocating the item's
// final size. Final used widths can hide a change in natural size through
// flex shrinking, so incremental layout compares these inputs as well.
struct ParentLayoutInput {
    double available_width = -1;
    double width = 0, height = 0;
    double min_content = 0, max_content = 0;
    bool operator==(const ParentLayoutInput& o) const {
        return available_width == o.available_width && width == o.width && height == o.height &&
               min_content == o.min_content && max_content == o.max_content;
    }
    bool operator!=(const ParentLayoutInput& o) const { return !(*this == o); }
};

struct Box {
    BoxKind kind = BoxKind::Block;

    // Intrusive tree links. `element` is null on anonymous boxes and line
    // boxes; `style` is shared with the originating element and is never owned.
    BoxId parent = kNoBox;
    BoxId first_child = kNoBox;
    BoxId last_child = kNoBox;
    BoxId next_sibling = kNoBox;
    BoxId prev_sibling = kNoBox;

    const Element* element = nullptr;
    const ComputedStyle* style = nullptr;
    // Set on a ::before/::after box: the originating element. `element` stays
    // null — a pseudo is not an element, so the dump and hit-testing skip it —
    // but unlike a true anonymous box it carries its own style and IS
    // decorated by paint.
    const Element* pseudo_host = nullptr;

    // CSS 2.1 §9.2.1.1. Set on the inline fragments a block-in-inline split
    // manufactures around the block. It matters because an EMPTY fragment of
    // this kind must not form a line box — the spec does not generate the
    // empty anonymous blocks either side of the block — while an empty inline
    // the AUTHOR wrote (`<div><span></span></div>`) still forms a line of
    // strut height. Only the provenance tells the two apart.
    bool is_split_fragment = false;

    // The LAST piece of a block-in-inline split. The reference gives that one
    // a zero-size box even when it is empty, and gives the leading pieces
    // none — so this is the only empty fragment that gets emitted, and the
    // element's box then lands AFTER the block, where the reference puts it.
    bool is_last_split_fragment = false;

    // Outermost inline ancestor whose box was split around this in-flow block.
    // Its DOM ancestors through this element own the promoted block fragment
    // for client bounds. Ordinary overflowing descendants do not contribute.
    // Non-owning, cleared by Box{} when the pool releases the box.
    const Element* split_inline_owner = nullptr;

    // Border-box geometry, relative to the parent box's content origin.
    double x = 0, y = 0, width = 0, height = 0;

    double margin_top = 0, margin_right = 0, margin_bottom = 0, margin_left = 0;
    double padding_top = 0, padding_right = 0, padding_bottom = 0, padding_left = 0;
    double border_top = 0, border_right = 0, border_bottom = 0, border_left = 0;

    // Empty optional means `auto`, which is distinct from 0.
    PositionType position = PositionType::Static;
    std::optional<double> offset_top, offset_right, offset_bottom, offset_left;
    std::optional<int> z_index;

    // Replaced image contribution before flex/grid imposes a used width;
    // the width accounts for a definite authored height and its aspect ratio.
    double intrinsic_width = 0, intrinsic_height = 0;

    // Scroll offset on a scroll container. Paint shifts descendants by
    // (-scroll_x, -scroll_y); hit testing does the inverse.
    double scroll_x = 0, scroll_y = 0;
    // The rectangle this box and everything under it can paint into, relative
    // to the box's own origin. A box that clips its overflow stops the union
    // at its own padding box, which is what makes this useful: paint tests it
    // against the current clip and skips the whole subtree when it misses.
    //
    // Without it, painting a scrolled list walked every box in it -- 46,000
    // for a 2,000-row list -- to produce the twenty draws you can see, and a
    // scroll cost 100 ms a frame. The draw-level clip already threw the meshes
    // away; the walk that built them is what had to stop.
    //
    // Filled by a single bottom-up pass after layout (compute_visual_overflow).
    double vis_x0 = 0, vis_y0 = 0, vis_x1 = 0, vis_y1 = 0;
    // Written by the sticky resolver on top of the natural origin. x/y stay at
    // the in-flow position so other passes still reason about static placement.
    double sticky_offset_x = 0, sticky_offset_y = 0;

    // ---- Block ----------------------------------------------------------
    // The resolved display, which decides the formatting context this box
    // establishes. Distinct from `is_inline_block`, which is about how the box
    // participates in its PARENT's formatting context.
    DisplayKind display = DisplayKind::Block;
    // CSS Multi-column L1 §2: a block container with a non-auto column-count or
    // column-width. Flex, grid and table containers ignore the column
    // properties, so this is only ever set on a block container.
    bool is_multicol = false;
    bool contains_inlines = false;
    // display: inline-block / inline-flex / inline-grid. The inner formatting
    // context is unchanged; only participation in the parent IFC differs, and
    // margins do not collapse with siblings (CSS Box Model §8.3.1).
    bool is_inline_block = false;
    FloatType float_type = FloatType::None;
    ClearType clear = ClearType::None;
    // list-style-image on a marker box, whose text run is suppressed.
    std::string_view list_marker_image;
    // A list item's marker, under `list-style-position: outside` -- the
    // initial value. The marker still rides the item's first line, but it is
    // placed BEFORE the content edge and takes no inline space, so the item's
    // own content starts where it would with no marker at all. Set only for
    // `outside`; an `inside` marker is an ordinary inline run.
    bool is_list_marker_outside = false;

    // ---- Inline ---------------------------------------------------------
    // Set on the SECOND and later fragments of a span that wraps across lines
    // (CSS 2.1 §9.4.2). The first fragment is the element-keyed box, so a
    // single-line span is both first (is_line_fragment false) and last.
    bool is_line_fragment = false;
    bool is_last_fragment = false;
    // Inline-axis padding+border+margin reserved on the start and end edges,
    // ONE SIDE EACH, not a sum. Under `box-decoration-break: slice` the start
    // applies to the first fragment only and the end to the last only; under
    // `clone` both apply to every fragment.
    double inline_pbm_start = 0;
    double inline_pbm_end = 0;

    // ---- Line -----------------------------------------------------------
    double baseline = 0;
    bool is_final_line = false;
    // Preserved newlines and <br> end an intrinsic-sizing paragraph; a soft
    // wrap does not. This is independent of the final line in the container.
    bool ends_with_forced_break = false;
    // The text-align shift already applied to this line's children. A later
    // alignment pass — a flex or grid item re-running its inline content once
    // its width settles — subtracts this before applying the new offset.
    // Without it each pass stamps its shift on top of the previous one.
    double applied_text_align_delta = 0;
    // On a line box: the width of the collapsible spaces the wrap removed from
    // its end. Summing a paragraph's lines plus these gives its unwrapped
    // (max-content) width without measuring the text again.
    double trimmed_trailing_space = 0;
    // On a line box: the inline boxes' own horizontal margin, border and
    // padding placed on this line — on the line, in no run.
    double inline_decoration_width = 0;
    // On a grid container: the tracks' min- and max-content extents (with
    // gaps), recorded by layout_grid so the intrinsic-width walk can answer
    // for it; negative until the grid has been sized once.
    double grid_min_content = -1;
    double grid_max_content = -1;

    // ---- Text -----------------------------------------------------------
    // Points into either the source text node or the pass arena (for collapsed
    // text). Never owned by the box.
    std::string_view text;
    std::string_view font_family;
    double font_size = 0;
    const TextNode* source_node = nullptr;
    const Element* source_control = nullptr; // textarea's live value buffer
    // Byte-preserving source position, independent of the display buffer's
    // address (e.g. text-transform). SIZE_MAX means no identity mapping.
    size_t control_source_offset = size_t(-1);
    // A preserved source tab is a one-byte placeholder with an exact advance.
    bool preserved_tab = false;
    // Inter-character justification, added on top of the CSS letter-spacing.
    double justify_letter_spacing = 0;
    // Width `text-align: justify` added to this run; not part of its natural
    // advance, so intrinsic sizing takes it back off.
    double justify_extra_width = 0;

    // A flex line stretched this box's cross size, so the auto-height rule must
    // not collapse it back to its content when its layout is re-run. Without
    // this a stretched item re-lays at the right size and then immediately
    // discards it, which is invisible until something inside depends on the
    // height — a nested column flex container, for instance.
    bool cross_size_imposed = false;
    ParentLayoutInput parent_layout_input;
    // Scratch-only reference to a retained layout subtree in the destination
    // tree. Its children stay there until the transaction is committed.
    BoxId retained_from = kNoBox;

    bool is_float() const { return float_type != FloatType::None; }
    double content_width() const {
        return width - padding_left - padding_right - border_left - border_right;
    }
    double content_height() const {
        return height - padding_top - padding_bottom - border_top - border_bottom;
    }
};

// Identity-mapped control text may live in a transformed arena buffer. Other
// text still uses its DOM-backed view. Reject expanded/unmapped display text.
inline size_t text_source_length(const Box& box) {
    return box.text.size();
}
inline size_t source_to_display(const Box& box, size_t offset) {
    return std::min(offset, box.text.size());
}
inline std::optional<size_t> text_source_offset(const Box& box, std::string_view source) {
    if (box.source_control && box.control_source_offset != size_t(-1)) {
        const size_t at = box.control_source_offset;
        if (at <= source.size() && text_source_length(box) <= source.size() - at) return at;
        return std::nullopt;
    }
    const uintptr_t base = reinterpret_cast<uintptr_t>(source.data());
    const uintptr_t run = reinterpret_cast<uintptr_t>(box.text.data());
    if (run >= base && run - base <= source.size() && box.text.size() <= source.size() - (run - base))
        return size_t(run - base);
    return std::nullopt;
}

// Owns every box for one layout pass. `reset()` returns all storage to the free
// pool without releasing it, so a steady-state frame allocates nothing.
class BoxTree {
public:
    BoxTree() = default;
    // Boxes hold views into this tree's owned text. A shallow copy duplicates
    // the strings but leaves those views pointing at the old owner. In
    // particular, vector growth must MOVE scratch trees, never copy them.
    BoxTree(const BoxTree&) = delete;
    BoxTree& operator=(const BoxTree&) = delete;
    BoxTree(BoxTree&&) = default;
    BoxTree& operator=(BoxTree&&) = default;

    BoxId create(BoxKind kind, const Element* element = nullptr,
                 const ComputedStyle* style = nullptr);

    Box& operator[](BoxId id) { return boxes_[static_cast<size_t>(id)]; }
    const Box& operator[](BoxId id) const { return boxes_[static_cast<size_t>(id)]; }
    bool valid(BoxId id) const {
        return id >= 0 && static_cast<size_t>(id) < boxes_.size();
    }
    int size() const { return static_cast<int>(boxes_.size()); }

    void append_child(BoxId parent, BoxId child);
    // Used when attaching inline fragments to lines: the first box to claim an
    // element is its principal box, so the fragment must precede the text runs
    // it covers in tree order.
    void insert_child_first(BoxId parent, BoxId child);
    // Unlinks `child` from its parent. The box itself stays allocated — the
    // arena is reset wholesale, never per box.
    void remove_child(BoxId child);
    void replace_child(BoxId existing, BoxId replacement);
    void clear_children(BoxId parent);
    // Splice a separately laid-out subtree while retaining its root identity.
    // Imported runs own their text; no view may point into the scratch tree.
    void replace_subtree(BoxId root, const BoxTree& source, BoxId source_root);

    // Frees every box. Capacity is kept: that is the point of the arena.
    void reset() {
        boxes_.clear();
        owned_text_.clear();
        imported_text_.clear();
        free_boxes_.clear();
        table_borders_.clear();
    }
    // Storage for text the tree produced itself — a `text-transform`ed run
    // — that no DOM node holds. Lives as long as the tree.
    std::string_view own_text(std::string text) {
        owned_text_.push_back(std::move(text));
        return owned_text_.back();
    }
    // Pre-sizes the storage so the first pass of a document does not grow it
    // repeatedly.
    void reserve(int n) { boxes_.reserve(static_cast<size_t>(n)); }

    // Range-for over a box's children:  for (BoxId c : tree.children(id))
    class ChildRange {
    public:
        class Iterator {
        public:
            Iterator(const BoxTree* t, BoxId id) : tree_(t), id_(id) {}
            BoxId operator*() const { return id_; }
            Iterator& operator++() {
                id_ = tree_->boxes_[static_cast<size_t>(id_)].next_sibling;
                return *this;
            }
            bool operator!=(const Iterator& o) const { return id_ != o.id_; }

        private:
            const BoxTree* tree_;
            BoxId id_;
        };
        ChildRange(const BoxTree* t, BoxId first) : tree_(t), first_(first) {}
        Iterator begin() const { return Iterator(tree_, first_); }
        Iterator end() const { return Iterator(tree_, kNoBox); }

    private:
        const BoxTree* tree_;
        BoxId first_;
    };
    ChildRange children(BoxId parent) const {
        return ChildRange(this, valid(parent) ? boxes_[static_cast<size_t>(parent)].first_child
                                              : kNoBox);
    }
    int child_count(BoxId parent) const;
    BoxId child_at(BoxId parent, int index) const;
    void set_table_borders(BoxId table, std::vector<TableBorderSegment> segments) {
        if (segments.empty()) table_borders_.erase(table);
        else table_borders_[table] = std::move(segments);
    }
    const std::vector<TableBorderSegment>* table_borders(BoxId table) const {
        if (table_borders_.empty()) return nullptr;
        const auto found = table_borders_.find(table);
        return found == table_borders_.end() ? nullptr : &found->second;
    }

private:
    std::map<BoxId, std::vector<TableBorderSegment>> table_borders_;
    std::vector<Box> boxes_;
    std::deque<std::string> owned_text_;
    std::map<BoxId, std::string> imported_text_;
    std::vector<BoxId> free_boxes_;
    void release_subtree(BoxId root);
    void import_subtree(BoxId into, const BoxTree& source, BoxId from);
};

} // namespace weva
