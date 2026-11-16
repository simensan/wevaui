#pragma once
#include "weva/font_interface.h"
#include "weva/render_interface.h"

#include <algorithm>
#include <map>
#include <array>
#include <vector>

// Packs rasterized glyphs into one texture so a run of text is a single draw
// rather than one per character.

namespace weva {

struct GlyphSlot {
    // Position and size within the atlas, in pixels.
    int x = 0, y = 0, width = 0, height = 0;
    double bearing_x = 0, bearing_y = 0;
    double advance = 0;
    // Normalised texture coordinates, precomputed because every emitted quad
    // needs them. The atlas changes size only when it starts over, which
    // drops every slot.
    float u0 = 0, v0 = 0, u1 = 0, v1 = 0;
    // The atlas texels hold the glyph's own colours (an emoji); the text
    // colour must not modulate them.
    bool is_color = false;
};

class GlyphAtlas {
public:
    // The atlas is built at width x height and goes back to that whenever it
    // starts over. make_room may double it, up to max_size a side, for a
    // frame that does not fit.
    GlyphAtlas(int width = 512, int height = 512, int max_size = 2048)
        : width_(width), height_(height), base_width_(width), base_height_(height),
          max_size_(std::max({width, height, max_size})) {}

    // Returns the slot for a glyph at a size, rasterizing and packing it on
    // first request. Null when the glyph has no bitmap (a space, or a missing
    // glyph) or when the atlas has no room for it, which overflowed() reports.
    const GlyphSlot* get(FontInterface* font, FaceHandle face, uint32_t glyph, double px);
    // Normalize exactly as get(), and report only a valid uncached request.
    bool needs_preparation(FaceHandle face, uint32_t glyph, double* px) const;

    // A glyph was refused for lack of room since the atlas last started over.
    bool overflowed() const { return overflowed_; }

    // The atlas only adds, so sizes that nothing draws any more stay in it:
    // text under an animated scale leaves a set of glyphs behind at every
    // step. Once they fill it, a glyph not yet packed has nowhere to go and
    // draws nothing from then on.
    //
    // A caller that requests every glyph of a frame before drawing any of them
    // calls this after those requests. True means the atlas has started over
    // empty and the caller must request them all again: the first time at the
    // constructed size, which sheds what the frame does not draw, and then
    // doubled for as long as the frame alone does not fit. False means no
    // glyph was refused, or that starting over again would not help.
    bool make_room();

    // Uploads the atlas if anything changed since the last call, and returns
    // the texture. Uploading once per frame rather than per glyph is the point
    // of packing at all.
    TextureHandle texture(RenderInterface* backend);

    // Font identities belong to a backend. Replacing it invalidates every
    // slot even if the new backend reuses face/glyph IDs. The old uploaded
    // texture remains alive until texture() publishes the replacement.
    void clear();
    // Release through the backend that owns the handle. Retain CPU glyphs
    // for upload to a replacement renderer on its next paint.
    void release_texture(RenderInterface* backend);

    int width() const { return width_; }
    int height() const { return height_; }
    // The packed pixels, RGBA; empty until the first glyph with ink is packed.
    // Read access so a caller can composite a run
    // into its own buffer -- which is what a blurred text-shadow needs, since
    // blurring means having the run's coverage somewhere it can be filtered.
    const std::vector<uint8_t>& pixels() const { return pixels_; }
    int slot_count() const { return static_cast<int>(slots_.size()); }
    // Adding glyphs or replacing the uploaded texture preserves existing slots.
    // Clearing the atlas changes their input version and requires preparation.
    uint64_t slot_version() const { return slot_version_; }

private:
    // Keyed by face as well as glyph: a bold or italic variant is another
    // face over the same glyph indices, and without the face in the key a
    // bold run reused whichever regular glyphs were already packed.
    struct Key {
        uint64_t face;
        uint32_t glyph;
        int px_quantised;
        bool operator<(const Key& o) const {
            if (face != o.face) return face < o.face;
            return glyph != o.glyph ? glyph < o.glyph : px_quantised < o.px_quantised;
        }
    };

    static bool request_key(FaceHandle face, uint32_t glyph, double* px, Key* key);
    size_t empty_index(const Key& key) const;

    void start_over(int width, int height);

    int width_, height_;
    int base_width_, base_height_, max_size_;
    bool overflowed_ = false;
    // Every slot was requested since the atlas last started over, so an
    // overflow now means the frame itself is too big for this size.
    bool only_this_frame_ = false;
    // Calls left to sit out after a frame did not fit at the largest size.
    int hold_off_ = 0;
    std::vector<uint8_t> pixels_;
    std::map<Key, GlyphSlot> slots_;
    // Successful rasters without ink need no slot or texture space. Keep a
    // bounded direct-mapped cache so repeated spaces do not call the host on
    // every preparation/draw. Collisions only cause another raster request.
    std::array<Key, 64> empty_glyphs_{};
    // Shelf packing: glyphs go left to right on a row, and a new row starts
    // below the tallest glyph of the previous one. Simple, and good enough when
    // the entries are all roughly the same height, which glyphs at one size are.
    int shelf_x_ = 0, shelf_y_ = 0, shelf_height_ = 0;
    bool dirty_ = false;
    uint64_t slot_version_ = 1;
    TextureHandle texture_;
};

} // namespace weva
