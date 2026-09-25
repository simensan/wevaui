#pragma once
#include "weva/image_store.h"
#include "weva/box.h"
#include <vector>
#include "weva/render_interface.h"
#include "weva/style_resolver.h"
#include "weva/font_interface.h"
#include "weva/glyph_atlas.h"
#include "weva/tessellate.h"

#include <map>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>

namespace weva {
class StyleProvider;
struct RasterQueue;

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
// Registered properties share ComputedStyle's invalidated-on-write parse cache.
LinearColor resolve_color(const ComputedStyle* style, std::string_view property);

// Shared numeric opacity for painting and stacking-context classification.
double resolve_opacity(const ComputedStyle* style);

// The same local transform used by painting, for geometry inspection tools.
bool resolve_transform(const ComputedStyle* style, const LayoutContext& ctx,
                       double font_size, double width, double height, Transform2D* out);

// Whether `transform` or one of the individual `translate` / `rotate` /
// `scale` properties is set to something other than none.
bool has_transform_property(const ComputedStyle* style);

// Paints `root` and its subtree. Boxes are walked in tree order, which is
// document order — stacking contexts and z-index ordering are a later slice, so
// a positive z-index does not yet lift a box above a later sibling.
// Everything paint needs beyond the box tree. A null font or atlas means text
// is skipped and the rest still paints, so a host without a font backend gets
// boxes rather than nothing.
// Rasterized background and blur textures, kept ACROSS updates.
//
// A gradient layer is rasterized on the CPU into a texture and uploaded. Doing
// that every update is what made a document expensive to change at all: the
// canvas background alone is viewport-sized, so a page with nothing but a
// `linear-gradient` body re-sampled 921,600 texels to move a number in a
// corner. Measured on the sample corpus, layout was 0.1-3 ms and the whole
// update 46-1361 ms, essentially all of it here.
//
// So a texture is keyed by everything that decides its pixels, and an entry
// asked for again is reused -- which also spares the host a re-upload, since
// the id it caches by does not change. Entries nothing asked for are released
// at the end of the pass.
class TextureCache {
public:
    // Null when absent. A hit marks the entry used for this pass.
    TextureHandle get(const std::string& key);
    void put(const std::string& key, TextureHandle texture);
    // A reused command references the texture without asking for its key.
    void retain(TextureHandle texture);
    // Called around a paint pass; end_pass releases whatever went unused.
    void begin_pass();
    void end_pass(RenderInterface* backend);
    void release_all(RenderInterface* backend);
    size_t size() const { return entries_.size(); }
    // Diagnostics: how much of a pass the cache actually saved.
    int hits() const { return hits_; }
    int misses() const { return misses_; }

private:
    struct Entry {
        TextureHandle texture;
        bool used = false;
    };
    std::map<std::string, Entry> entries_;
    std::unordered_map<uint64_t, Entry*> by_texture_;
    int hits_ = 0;
    int misses_ = 0;
};

// Rasterized pixels kept ACROSS documents, so a screen opened again -- a
// Unity WevaDocument re-enabled, a Godot scene instanced anew -- is drawn from
// what the last one rasterized instead of from nothing. TextureCache lives
// and dies with its document; this is process-wide, holds pixels rather than
// host handles, and is bounded by bytes, least recently used first.
//
// Only pictures that depend on nothing but their key are shared: shadows,
// and backgrounds and blurs without images or font-relative units. The key
// adds the viewport, root font size and resolution a `vw` or `rem` resolves
// against. Thread-safe; weva_set_raster_cache_limit sets the bound.
struct SharedPixels {
    std::vector<uint8_t> rgba;
    int width = 0, height = 0;
};
std::shared_ptr<const SharedPixels> shared_raster_find(const std::string& key);
void shared_raster_insert(const std::string& key, std::shared_ptr<const SharedPixels> pixels);
void set_shared_raster_limit(size_t bytes);
size_t shared_raster_bytes();
inline constexpr size_t kDefaultSharedRasterBytes = size_t{32} << 20;

// Where the text cursor is, for the one field that has focus.
//
// The caret is not a property of the document -- nothing in the DOM says where
// a cursor sits -- so it arrives with the paint pass rather than being found in
// the tree. A field a user can type into with no visible cursor reads as
// broken, whatever else is right about it.
struct CaretState {
    const Element* element = nullptr;   // the focused field, or null for none
    int index = 0;                      // characters before the caret
    bool visible = true;                // the blink, off half the time
    double text_scroll_x = 0;           // focused input's internal text viewport
    // A <textarea>'s value is laid out as ordinary inline content, so its
    // cursor lives inside one of the text runs rather than in text paint draws
    // itself. Which run, and how far into it, is settled once after layout --
    // paint cannot work it out per run without either missing the cursor at a
    // line end (the newline belongs to no run) or drawing it twice where two
    // runs meet.
    BoxId run = kNoBox;
    size_t run_offset = 0;   // characters of `run` before the cursor
    bool downstream = false; // shared soft-wrap boundary belongs to next line

    // The selected range, in bytes into the field's value, low end first --
    // empty when there is only a cursor. Drawn as a band behind the glyphs,
    // which is why paint needs the value the runs view into: each run covers a
    // slice of it, and the part of that slice inside the range is the part to
    // highlight.
    size_t selection_from = 0, selection_to = 0;
    size_t composition_from = 0, composition_to = 0;
    std::string_view source;
};

// The open <select>'s list. A dropdown is the one piece of a document that is
// NOT in the box tree: it covers whatever it happens to open over, so it is
// painted after the tree rather than in it, and hit tested before it.
struct SelectPopup {
    const Element* element = nullptr;   // the open select, or null for none
    int highlighted = -1;               // the option under the pointer or the keys
    int first_row = 0;                  // the list is scrolled to here
};

// Optional retained draw-list seam. A replay must reproduce the complete
// subtree, including backend state changes, or return false without drawing.
struct ClipNode;
struct OverflowClip;
struct ColorFilter;

// Incoming paint inputs at a retained layout boundary. Clips and filters are
// immutable snapshots; equality compares their values, not pointer identities.
struct PaintReplayInputs {
    double x = 0, y = 0, opacity = 1;
    std::optional<Recti> scissor;
    bool transformed = false;
    Transform2D xform;
    std::shared_ptr<const ClipNode> clip;
    std::shared_ptr<const ColorFilter> filter;
    BoxId canvas_owner = kNoBox;
    std::shared_ptr<const OverflowClip> overflow;
    BoxId absolute_cb = kNoBox, fixed_cb = kNoBox;
    BlendMode blend = BlendMode::Normal;
    bool operator==(const PaintReplayInputs& other) const;
};

class PaintReuse {
public:
    virtual ~PaintReuse() = default;
    // The glyph prepass may skip a subtree only when all its text/font inputs
    // and the atlas's slot version still match the preceding prepared pass.
    // Paint position, clipping and texture uploads do not remove glyph slots.
    virtual void begin_glyphs(const GlyphAtlas&) {}
    virtual bool reuse_glyphs(BoxId) const { return false; }
    virtual void begin_paint(TextureHandle atlas) = 0;
    virtual bool replay(BoxId box) = 0;
    virtual bool tracks_inputs(BoxId) const { return false; }
    virtual bool replay(BoxId box, const PaintReplayInputs&) { return replay(box); }
    virtual void begin_box(BoxId box) = 0;
    virtual void end_box(BoxId box) = 0;
};

struct PaintContext {
    StyleProvider* styles = nullptr; // Popup rows have styles but no layout boxes.
    PaintReuse* reuse = nullptr;
    RenderInterface* backend = nullptr;
    FontInterface* font = nullptr;
    GlyphAtlas* atlas = nullptr;
    FaceHandle face;
    // Textures paint generates for this pass (rasterized gradient layers).
    // The caller owns their release — after the host has consumed the draws
    // that reference them, typically at the start of the next pass. Null
    // means paint leaks nothing it can avoid and generates them anyway.
    std::vector<TextureHandle>* owned_textures = nullptr;
    // When set, rasterized backgrounds and blurs are cached here instead of
    // being regenerated and released every pass.
    TextureCache* texture_cache = nullptr;
    // Set by paint_tree for the pass: where rasters wait to run together.
    RasterQueue* raster_queue = nullptr;
    // Where a `url(...)` in a background gets its pixels. Null means images
    // do not paint, which is what the engine did before it existed.
    ImageStore* images = nullptr;
    CaretState caret;
    SelectPopup popup;
    const Element* active_option = nullptr;
};

// Where the open list goes, and how tall each row is: shared by paint and by
// the hit testing, so a row you can see is a row you can click.
struct SelectListGeometry {
    bool visible = false;
    Rect box;             // the whole list, in document coordinates
    double row_height = 0;
    int count = 0;        // display rows, including optgroup headings
    int rows = 0;         // how many of them fit in the box
};

SelectListGeometry select_list_geometry(const BoxTree& tree, BoxId select_box,
                                        const LayoutContext& ctx, const Element& select);

// Every option of a select, in order, including those inside an <optgroup>.
std::vector<const Element*> select_options(const Element& select);

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

// Which character of a field's value sits under a point -- the inverse of
// putting the cursor at an index, and what a click in a text box needs.
//
// Both live here rather than beside the input handling because the answer
// depends on the same shaping, face and letter-spacing the text was DRAWN
// with; working it out anywhere else means guessing at those and putting the
// cursor a character off. `paint` needs only its font, atlas and face.
//
// Returns the byte offset into the control's value, rounded to the nearest
// character boundary -- clicking the right half of a glyph puts the cursor
// after it, as it does everywhere.

// For a form control whose text paint draws itself (an <input>). `x` is in
// document coordinates.
size_t control_text_offset_at(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                              const PaintContext& paint, double x);

// Insertion caret in document coordinates, with scroll and CSS transforms.
// The geometry uses the same face, prefix measurement and line metrics as paint.
bool text_caret_bounds(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                       const PaintContext& paint, Rect* out);

// Navigate actual textarea line fragments. direction: -1/+1 up/down,
// -2/+2 line start/end, -3/+3 previous/next paragraph retaining the column.
// NaN preferred_x starts a new vertical-motion sequence.
bool navigate_text_line(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                        const PaintContext& paint, int direction, double* preferred_x,
                        size_t* offset, bool* downstream);

// Clamp the focused input's text viewport, optionally revealing its caret.
// Timed selection scrolling controls the viewport independently of the caret.
double input_text_scroll(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                         const PaintContext& paint, bool reveal_caret = true,
                         double* maximum = nullptr);

// For a <textarea>, whose value is laid out as runs: the offset into `source`
// nearest the point, taking the line under `y` and then the character under
// `x`. `origin_x`/`origin_y` is the textarea box's border-box origin in
// document coordinates, already shifted by any scroll.
size_t run_text_offset_at(const BoxTree& tree, BoxId box, const LayoutContext& ctx,
                          const PaintContext& paint, std::string_view source, double origin_x,
                          double origin_y, double x, double y);

// Builds the mesh for one box's background and border, without issuing any
// draw. Exposed because it is far easier to assert geometry than backend calls.
void paint_box_decorations(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                           double origin_x, double origin_y, Mesh* out,
                           bool with_background = true,
                           // Set when a border image has already been drawn over
                           // this border and replaces it.
                           bool skip_border = false);

} // namespace weva
