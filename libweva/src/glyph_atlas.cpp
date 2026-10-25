#include "weva/glyph_atlas.h"

#include <cmath>
#include <algorithm>
#include <limits>

namespace weva {

void GlyphAtlas::clear() {
    ++slot_version_;
    slots_.clear();
    std::fill(pixels_.begin(), pixels_.end(), 0);
    shelf_x_ = shelf_y_ = shelf_height_ = 0;
    dirty_ = true;
}

void GlyphAtlas::release_texture(RenderInterface* backend) {
    if (texture_ && backend) backend->release_texture(texture_);
    texture_ = {};
    dirty_ = true;
}

const GlyphSlot* GlyphAtlas::get(FontInterface* font, FaceHandle face, uint32_t glyph,
                                 double px) {
    if (!font || !face || !std::isfinite(px) || px <= 0 ||
        px > std::numeric_limits<int>::max() / 64.0) return nullptr;
    // Fractional pixel sizes have different coverage. Quantise the request
    // AND key to 1/64px so lookup never reuses another size's raster/metrics.
    const int quantised = static_cast<int>(std::lround(px * 64));
    if (quantised <= 0) return nullptr;
    px = quantised / 64.0;
    const Key key{face.id, glyph, quantised};
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
    if (bmp.width > width_ - 2 * pad || bmp.height > height_ - 2 * pad) return nullptr;
    if (shelf_x_ + bmp.width + 2 * pad > width_) {
        shelf_x_ = 0;
        shelf_y_ += shelf_height_;
        shelf_height_ = 0;
    }
    if (shelf_y_ + bmp.height + 2 * pad > height_) return nullptr;   // full

    GlyphSlot slot;
    slot.x = shelf_x_ + pad;
    slot.y = shelf_y_ + pad;
    slot.width = bmp.width;
    slot.height = bmp.height;
    slot.bearing_x = gm.bearing_x;
    slot.bearing_y = gm.bearing_y;
    slot.advance = gm.advance;
    slot.u0 = static_cast<float>(slot.x) / width_;
    slot.v0 = static_cast<float>(slot.y) / height_;
    slot.u1 = static_cast<float>(slot.x + slot.width) / width_;
    slot.v1 = static_cast<float>(slot.y + slot.height) / height_;

    // Coverage goes into alpha: white RGB means the
    // vertex colour passes through unchanged when the texture modulates it, so
    // one atlas serves text of any colour. A colour glyph keeps its texels and
    // paint draws its quad white instead (see build_text_geometry).
    const bool color = bmp.is_color &&
                       bmp.rgba.size() >= static_cast<size_t>(bmp.width) * bmp.height * 4;
    slot.is_color = color;
    // Straight-alpha filtering must not blend white coverage with black RGB
    // outside the bitmap (which attenuates its edges twice). Colour glyphs
    // extend their edge RGB into the transparent gutter for the same reason.
    for (int y = -pad; y < bmp.height + pad; ++y) {
        for (int x = -pad; x < bmp.width + pad; ++x) {
            const size_t o = (static_cast<size_t>(slot.y + y) * width_ + slot.x + x) * 4;
            const size_t edge = (static_cast<size_t>(std::clamp(y, 0, bmp.height - 1)) *
                                 bmp.width + std::clamp(x, 0, bmp.width - 1)) * 4;
            for (int channel = 0; channel < 3; ++channel)
                pixels_[o + channel] = color ? bmp.rgba[edge + channel] : 255;
        }
    }
    for (int y = 0; y < bmp.height; ++y) {
        for (int x = 0; x < bmp.width; ++x) {
            const size_t i = static_cast<size_t>(y) * bmp.width + x;
            const size_t o = (static_cast<size_t>(slot.y + y) * width_ + slot.x + x) * 4;
            if (color) {
                pixels_[o + 0] = bmp.rgba[4 * i + 0];
                pixels_[o + 1] = bmp.rgba[4 * i + 1];
                pixels_[o + 2] = bmp.rgba[4 * i + 2];
                pixels_[o + 3] = bmp.rgba[4 * i + 3];
            } else {
                pixels_[o + 0] = 255;
                pixels_[o + 1] = 255;
                pixels_[o + 2] = 255;
                pixels_[o + 3] = bmp.data[i];
            }
        }
    }

    shelf_x_ += bmp.width + 2 * pad;
    shelf_height_ = std::max(shelf_height_, bmp.height + 2 * pad);
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
