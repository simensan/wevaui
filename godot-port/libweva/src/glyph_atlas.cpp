#include "weva/glyph_atlas.h"

#include <cmath>

namespace weva {

const GlyphSlot* GlyphAtlas::get(FontInterface* font, FaceHandle face, uint32_t glyph,
                                 double px) {
    if (!font || !face) return nullptr;
    // Sizes are quantised to whole pixels for the cache key: a 15.99px and a
    // 16px glyph rasterize identically, and keying on the raw double would
    // re-pack the same glyph for every fractional size a percentage font-size
    // produces.
    const Key key{glyph, static_cast<int>(std::lround(px))};
    auto it = slots_.find(key);
    if (it != slots_.end()) return &it->second;

    Bitmap bmp;
    if (!font->rasterize(face, glyph, px, RenderMode::Alpha8, &bmp)) return nullptr;
    GlyphMetrics gm;
    font->glyph_metrics(face, glyph, px, &gm);
    if (bmp.width <= 0 || bmp.height <= 0) return nullptr;   // a space has no bitmap

    // One pixel of padding, so bilinear filtering in a GPU backend cannot bleed
    // a neighbouring glyph into this one.
    const int pad = 1;
    if (shelf_x_ + bmp.width + pad > width_) {
        shelf_x_ = 0;
        shelf_y_ += shelf_height_ + pad;
        shelf_height_ = 0;
    }
    if (shelf_y_ + bmp.height + pad > height_) return nullptr;   // full

    GlyphSlot slot;
    slot.x = shelf_x_;
    slot.y = shelf_y_;
    slot.width = bmp.width;
    slot.height = bmp.height;
    slot.bearing_x = gm.bearing_x;
    slot.bearing_y = gm.bearing_y;
    slot.advance = gm.advance;
    slot.u0 = static_cast<float>(slot.x) / width_;
    slot.v0 = static_cast<float>(slot.y) / height_;
    slot.u1 = static_cast<float>(slot.x + slot.width) / width_;
    slot.v1 = static_cast<float>(slot.y + slot.height) / height_;

    // The coverage byte goes into all four channels: white RGB means the
    // vertex colour passes through unchanged when the texture modulates it, so
    // one atlas serves text of any colour.
    for (int y = 0; y < bmp.height; ++y) {
        for (int x = 0; x < bmp.width; ++x) {
            const uint8_t a = bmp.data[static_cast<size_t>(y) * bmp.width + x];
            const size_t o = (static_cast<size_t>(slot.y + y) * width_ + slot.x + x) * 4;
            pixels_[o + 0] = 255;
            pixels_[o + 1] = 255;
            pixels_[o + 2] = 255;
            pixels_[o + 3] = a;
        }
    }

    shelf_x_ += bmp.width + pad;
    if (bmp.height > shelf_height_) shelf_height_ = bmp.height;
    dirty_ = true;
    return &slots_.emplace(key, slot).first->second;
}

TextureHandle GlyphAtlas::texture(RenderInterface* backend) {
    if (!backend) return {};
    if (dirty_) {
        // Released and regenerated rather than updated in place: the interface
        // has no partial-upload call, and adding one would push work back onto
        // every backend for a case that happens a handful of times per document.
        if (texture_) backend->release_texture(texture_);
        texture_ = backend->generate_texture(pixels_, {width_, height_});
        dirty_ = false;
    }
    return texture_;
}

} // namespace weva
