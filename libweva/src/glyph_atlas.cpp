#include "weva/glyph_atlas.h"

#include <cmath>
#include <algorithm>
#include <limits>

namespace weva {

void GlyphAtlas::clear() {
    ++slot_version_;
    slots_.clear();
    empty_glyphs_ = {};
    std::fill(pixels_.begin(), pixels_.end(), 0);
    shelf_x_ = shelf_y_ = shelf_height_ = 0;
    overflowed_ = false;
    dirty_ = true;
}

void GlyphAtlas::start_over(int width, int height) {
    clear();
    if (width == width_ && height == height_) return;
    width_ = width;
    height_ = height;
    // Sized again by the next glyph with ink.
    std::vector<uint8_t>().swap(pixels_);
}

bool GlyphAtlas::make_room() {
    if (!overflowed_) {
        only_this_frame_ = false;
        hold_off_ = 0;
        return false;
    }
    if (!only_this_frame_) {
        // A frame that fits at no size overflows on every call. Rasterising
        // all of it three times over each time would cost far more than the
        // glyphs it is missing.
        if (hold_off_ > 0) {
            --hold_off_;
            overflowed_ = false;
            return false;
        }
        start_over(base_width_, base_height_);
        only_this_frame_ = true;
        return true;
    }
    if (width_ < max_size_ || height_ < max_size_) {
        start_over(std::min(width_ * 2, max_size_), std::min(height_ * 2, max_size_));
        return true;
    }
    only_this_frame_ = false;
    overflowed_ = false;
    hold_off_ = 120;
    return false;
}

void GlyphAtlas::release_texture(RenderInterface* backend) {
    if (texture_ && backend) backend->release_texture(texture_);
    texture_ = {};
    dirty_ = true;
}

const GlyphSlot* GlyphAtlas::get(FontInterface* font, FaceHandle face, uint32_t glyph,
                                 double px) {
    Key key{};
    if (!font || !request_key(face, glyph, &px, &key)) return nullptr;
    auto it = slots_.find(key);
    if (it != slots_.end()) return &it->second;

    auto& empty = empty_glyphs_[empty_index(key)];
    if (empty.face == key.face && empty.glyph == key.glyph &&
        empty.px_quantised == key.px_quantised) return nullptr;

    Bitmap bmp;
    if (!font->rasterize(face, glyph, px, RenderMode::Alpha8, &bmp)) return nullptr;
    if (bmp.width <= 0 || bmp.height <= 0) {
        empty = key;
        return nullptr;
    }
    GlyphMetrics gm;
    font->glyph_metrics(face, glyph, px, &gm);

    // One pixel of padding, so bilinear filtering in a GPU backend cannot bleed
    // a neighbouring glyph into this one.
    const int pad = 1;
    // Larger than the atlas can ever be: not something making room can fix.
    if (bmp.width > max_size_ - 2 * pad || bmp.height > max_size_ - 2 * pad) return nullptr;
    if (bmp.width > width_ - 2 * pad || bmp.height > height_ - 2 * pad) {
        overflowed_ = true;
        return nullptr;
    }
    // Decide before moving to a new shelf: a glyph that is refused must not
    // cost the smaller ones after it the rest of this row.
    const bool wraps = shelf_x_ + bmp.width + 2 * pad > width_;
    if ((wraps ? shelf_y_ + shelf_height_ : shelf_y_) + bmp.height + 2 * pad > height_) {
        overflowed_ = true;
        return nullptr;
    }
    if (wraps) {
        shelf_x_ = 0;
        shelf_y_ += shelf_height_;
        shelf_height_ = 0;
    }

    // Documents without visible glyphs need neither CPU atlas storage nor an
    // uploaded blank texture. The dimensions change only in start_over, which
    // drops every slot, so the UVs of the slots that exist stay valid.
    if (pixels_.empty()) pixels_.resize(static_cast<size_t>(width_) * height_ * 4, 0);

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

bool GlyphAtlas::request_key(FaceHandle face, uint32_t glyph, double* px, Key* key) {
    if (!face || !px || !std::isfinite(*px) || *px <= 0 ||
        *px > std::numeric_limits<int>::max() / 64.0) return false;
    const int quantised = static_cast<int>(std::lround(*px * 64));
    if (quantised <= 0) return false;
    *px = quantised / 64.0;
    // Lookup, preparation and rasterization share the same 1/64px identity.
    *key = {face.id, glyph, quantised};
    return true;
}

size_t GlyphAtlas::empty_index(const Key& key) const {
    const uint64_t hash = key.face * 0x9e3779b97f4a7c15ULL ^
                          key.glyph * 0xbf58476d1ce4e5b9ULL ^
                          static_cast<uint64_t>(key.px_quantised) * 0x94d049bb133111ebULL;
    return (hash ^ (hash >> 32)) % empty_glyphs_.size();
}

bool GlyphAtlas::needs_preparation(FaceHandle face, uint32_t glyph, double* px) const {
    Key key{};
    if (!request_key(face, glyph, px, &key)) return false;
    if (slots_.find(key) != slots_.end()) return false;
    const auto& empty = empty_glyphs_[empty_index(key)];
    return empty.face != key.face || empty.glyph != key.glyph || empty.px_quantised != key.px_quantised;
}

TextureHandle GlyphAtlas::texture(RenderInterface* backend) {
    if (!backend) return {};
    if (dirty_) {
        // Released and regenerated rather than updated in place: the interface
        // has no partial-upload call, and adding one would push work back onto
        // every backend for a case that happens a handful of times per document.
        if (texture_) backend->release_texture(texture_);
        texture_ = pixels_.empty() ? TextureHandle{} :
                   backend->generate_texture(pixels_, {width_, height_});
        dirty_ = false;
    }
    return texture_;
}

} // namespace weva
