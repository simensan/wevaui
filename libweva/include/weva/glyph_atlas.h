#pragma once
#include "weva/font_interface.h"
#include "weva/render_interface.h"

#include <map>
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
    // needs them and the atlas size does not change between uploads.
    float u0 = 0, v0 = 0, u1 = 0, v1 = 0;
    // The atlas texels hold the glyph's own colours (an emoji); the text
    // colour must not modulate them.
    bool is_color = false;
};

class GlyphAtlas {
public:
    GlyphAtlas(int width = 512, int height = 512) : width_(width), height_(height),
                                                    pixels_(static_cast<size_t>(width) * height * 4, 0) {}

    // Returns the slot for a glyph at a size, rasterizing and packing it on
    // first request. Null when the glyph has no bitmap (a space, or a missing
    // glyph) or when the atlas is full.
    const GlyphSlot* get(FontInterface* font, FaceHandle face, uint32_t glyph, double px);

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
    // The packed pixels, RGBA. Read access so a caller can composite a run
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

    int width_, height_;
    std::vector<uint8_t> pixels_;
    std::map<Key, GlyphSlot> slots_;
    // Shelf packing: glyphs go left to right on a row, and a new row starts
    // below the tallest glyph of the previous one. Simple, and good enough when
    // the entries are all roughly the same height, which glyphs at one size are.
    int shelf_x_ = 0, shelf_y_ = 0, shelf_height_ = 0;
    bool dirty_ = false;
    uint64_t slot_version_ = 1;
    TextureHandle texture_;
};

} // namespace weva
