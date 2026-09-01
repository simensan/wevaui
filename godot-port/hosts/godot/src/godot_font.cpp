#include "godot_font.h"

#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/text_server.hpp>
#include <godot_cpp/classes/text_server_manager.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/string.hpp>
#include <godot_cpp/variant/typed_array.hpp>
#include <godot_cpp/variant/transform2d.hpp>
#include <godot_cpp/variant/vector2.hpp>
#include <godot_cpp/variant/vector2i.hpp>

#include <cmath>

using namespace godot;

namespace weva_godot {

namespace {

TextServer* server() {
    TextServerManager* manager = TextServerManager::get_singleton();
    return manager ? manager->get_primary_interface().ptr() : nullptr;
}

// TextServer sizes fonts in integer pixels. Rounding once, here, means
// measurement and rasterisation ask for the same size — asking for 15.6 in one
// place and 16 in another is how text drifts off the line boxes laid out for it.
int64_t size_of(double px) {
    const int64_t s = static_cast<int64_t>(std::lround(px));
    return s > 0 ? s : 1;
}

// A glyph id is the TextServer glyph index in the low 24 bits and the
// fallback slot — which of the face's fonts it came from — in the top byte.
constexpr uint32_t kSlotShift = 24;
constexpr uint32_t kIndexMask = 0xFFFFFFu;
uint32_t encode_glyph(uint32_t slot, int64_t index) {
    return (slot << kSlotShift) | (static_cast<uint32_t>(index) & kIndexMask);
}
uint32_t slot_of(uint32_t glyph) { return glyph >> kSlotShift; }
int64_t index_of(uint32_t glyph) { return static_cast<int64_t>(glyph & kIndexMask); }

} // namespace

GodotFontBackend::~GodotFontBackend() {
    TextServer* ts = server();
    if (!ts) return;
    for (const RID& r : owned_) ts->free_rid(r);
}

uint64_t GodotFontBackend::adopt(const RID& font) {
    if (!font.is_valid()) return 0;
    const uint64_t handle = next_face_++;
    faces_[handle] = {font};
    return handle;
}

uint64_t GodotFontBackend::adopt(const TypedArray<RID>& fonts) {
    std::vector<RID> list;
    for (int64_t i = 0; i < fonts.size(); ++i) {
        const RID r = fonts[i];
        if (r.is_valid()) list.push_back(r);
    }
    if (list.empty()) return 0;
    const uint64_t handle = next_face_++;
    faces_[handle] = std::move(list);
    return handle;
}

uint64_t GodotFontBackend::adopt(const TypedArray<RID>& fonts, const PackedByteArray& primary_data) {
    const uint64_t handle = adopt(fonts);
    if (handle && !primary_data.is_empty()) face_data_[handle] = primary_data;
    return handle;
}

RID GodotFontBackend::resolve(uint64_t face, uint32_t slot) const {
    const auto it = faces_.find(face);
    if (it == faces_.end() || slot >= it->second.size()) return RID();
    return it->second[slot];
}

const std::vector<RID>* GodotFontBackend::fonts_of(uint64_t face) const {
    const auto it = faces_.find(face);
    return it == faces_.end() ? nullptr : &it->second;
}

uint64_t GodotFontBackend::load_face(void* self, const uint8_t* data, size_t length,
                                     int32_t index) {
    (void)index;   // face selection within a collection is not exposed by TextServer
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts || !data || length == 0) return 0;

    PackedByteArray bytes;
    bytes.resize(static_cast<int64_t>(length));
    std::memcpy(bytes.ptrw(), data, length);

    const RID font = ts->create_font();
    if (!font.is_valid()) return 0;
    ts->font_set_data(font, bytes);
    me->owned_.push_back(font);
    return me->adopt(font);
}

int32_t GodotFontBackend::face_metrics(void* self, uint64_t face, double px, double* ascent,
                                       double* descent, double* line_gap) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts) return 0;
    const RID font = me->resolve(face);
    if (!font.is_valid()) return 0;

    const int64_t size = size_of(px);
    if (ascent) *ascent = ts->font_get_ascent(font, size);
    if (descent) *descent = ts->font_get_descent(font, size);
    // TextServer exposes no line gap: its own line height is ascent + descent,
    // and reporting a gap the engine does not itself apply would make `line-
    // height: normal` taller here than in any Godot control using the same face.
    if (line_gap) *line_gap = 0;
    return 1;
}

int32_t GodotFontBackend::glyph_index(void* self, uint64_t face, uint32_t codepoint,
                                      uint32_t* out) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts || !out) return 0;
    const std::vector<RID>* fonts = me->fonts_of(face);
    if (!fonts || fonts->empty()) return 0;

    // The ABI's glyph_index carries no size because a glyph id does not depend
    // on one; TextServer asks for a size anyway, so a reference size is used.
    // This is only a lookup key — every metric call passes the real size. The
    // first font that has the character wins; none having it yields the
    // primary face's .notdef, as a single font would.
    for (size_t slot = 0; slot < fonts->size(); ++slot) {
        const int64_t glyph = ts->font_get_glyph_index((*fonts)[slot], 16, codepoint, 0);
        if (glyph != 0) {
            *out = encode_glyph(static_cast<uint32_t>(slot), glyph);
            return 1;
        }
    }
    *out = encode_glyph(0, 0);
    return 1;
}

int32_t GodotFontBackend::glyph_metrics(void* self, uint64_t face, uint32_t glyph, double px,
                                        double* advance, double* bearing_x, double* bearing_y,
                                        int32_t* width, int32_t* height) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts) return 0;
    const RID font = me->resolve(face, slot_of(glyph));
    if (!font.is_valid()) return 0;
    const int64_t index = index_of(glyph);

    const int64_t size = size_of(px);
    const Vector2i sz(static_cast<int32_t>(size), 0);
    const Vector2 adv = ts->font_get_glyph_advance(font, size, index);
    const Vector2 offset = ts->font_get_glyph_offset(font, sz, index);
    const Vector2 extent = ts->font_get_glyph_size(font, sz, index);

    if (advance) *advance = adv.x;
    if (bearing_x) *bearing_x = offset.x;
    // Godot's offset is the quad's top-left relative to the baseline, with y
    // growing downward; the core's bearing_y measures UP from the baseline to
    // that same edge, so the sign flips.
    if (bearing_y) *bearing_y = -offset.y;
    if (width) *width = static_cast<int32_t>(extent.x);
    if (height) *height = static_cast<int32_t>(extent.y);
    return 1;
}

int32_t GodotFontBackend::rasterize(void* self, uint64_t face, uint32_t glyph, double px,
                                    weva_glyph_bitmap* out) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts || !out) return 0;
    const RID font = me->resolve(face, slot_of(glyph));
    if (!font.is_valid()) return 0;
    const int64_t index = index_of(glyph);

    const Vector2i sz(static_cast<int32_t>(size_of(px)), 0);
    // TextServer rasterises lazily into its own atlas, so the glyph has to be
    // asked for before its texture exists.
    ts->font_render_glyph(font, sz, index);

    const int64_t texture_index = ts->font_get_glyph_texture_idx(font, sz, index);
    if (texture_index < 0) return 0;
    const Ref<Image> image = ts->font_get_texture_image(font, sz, texture_index);
    if (image.is_null()) return 0;

    const Rect2 uv = ts->font_get_glyph_uv_rect(font, sz, index);
    const int32_t gx = static_cast<int32_t>(uv.position.x);
    const int32_t gy = static_cast<int32_t>(uv.position.y);
    const int32_t gw = static_cast<int32_t>(uv.size.x);
    const int32_t gh = static_cast<int32_t>(uv.size.y);
    // A blank glyph — a space — has no rect. Reporting no bitmap rather than an
    // empty one keeps it out of the atlas, which is what the core expects.
    if (gw <= 0 || gh <= 0) return 0;
    if (gx < 0 || gy < 0 || gx + gw > image->get_width() || gy + gh > image->get_height()) {
        return 0;
    }

    // The core wants one coverage byte per pixel. TextServer's atlas may be
    // RGBA (colour or subpixel) or 8-bit; the alpha channel is the coverage in
    // the first case, and Image::get_pixel normalises the second to it too, so
    // reading alpha is correct either way at the cost of a per-pixel call. Done
    // once per glyph per size, not per frame.
    me->scratch_.resize(static_cast<size_t>(gw) * gh);
    for (int32_t y = 0; y < gh; ++y) {
        for (int32_t x = 0; x < gw; ++x) {
            const Color c = image->get_pixel(gx + x, gy + y);
            me->scratch_[static_cast<size_t>(y) * gw + x] =
                static_cast<uint8_t>(std::lround(c.a * 255.0));
        }
    }

    out->alpha = me->scratch_.data();
    out->width = gw;
    out->height = gh;
    return 1;
}

size_t GodotFontBackend::shape(void* self, uint64_t face, const char* utf8, size_t length,
                               double px, uint32_t* glyphs, double* advances, uint32_t* clusters,
                               size_t capacity) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts || !utf8) return 0;
    const std::vector<RID>* face_fonts = me->fonts_of(face);
    if (!face_fonts || face_fonts->empty()) return 0;

    const String text = String::utf8(utf8, static_cast<int64_t>(length));
    const RID shaped = ts->create_shaped_text();
    if (!shaped.is_valid()) return 0;

    // Every font of the face, in order: TextServer falls back through the
    // list per character, and reports which font each glyph came from.
    TypedArray<RID> fonts;
    for (const RID& r : *face_fonts) fonts.push_back(r);
    ts->shaped_text_add_string(shaped, text, fonts, size_of(px));
    ts->shaped_text_shape(shaped);

    const TypedArray<Dictionary> shaped_glyphs = ts->shaped_text_get_glyphs(shaped);
    const size_t count = static_cast<size_t>(shaped_glyphs.size());
    for (size_t i = 0; i < count && i < capacity; ++i) {
        const Dictionary g = shaped_glyphs[static_cast<int64_t>(i)];
        uint32_t slot = 0;
        const RID from = g["font_rid"];
        for (size_t s = 0; s < face_fonts->size(); ++s) {
            if ((*face_fonts)[s] == from) { slot = static_cast<uint32_t>(s); break; }
        }
        if (glyphs) glyphs[i] = encode_glyph(slot, static_cast<int64_t>(g["index"]));
        if (advances) advances[i] = static_cast<double>(g["advance"]);
        // "start" is the byte offset into the string this glyph came from,
        // which is exactly the cluster the core uses to map back to text.
        if (clusters) clusters[i] = static_cast<uint32_t>(static_cast<int64_t>(g["start"]));
    }
    ts->free_rid(shaped);
    // The count is returned whether or not it fit, so a caller sizes with one
    // call and fills with a second.
    return count;
}

uint64_t GodotFontBackend::variant(void* self, uint64_t face, int32_t weight, int32_t italic) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts) return face;
    const bool bold = weight >= 600;
    const bool oblique = italic != 0;
    if (!bold && !oblique) return face;
    const auto key = std::make_tuple(face, bold, oblique);
    const auto hit = me->variants_.find(key);
    if (hit != me->variants_.end()) return hit->second;
    const std::vector<RID>* fonts = me->fonts_of(face);
    if (!fonts || fonts->empty()) return face;
    // The theme font ships one weight, so bold is emboldened outlines (the
    // same synthesis Godot's own SystemFont applies without a bold file) and
    // italic a shear; a face with real bold or italic files would be adopted
    // as its own face instead. Each variant is an INDEPENDENT font over the
    // same data: a linked variation shares the base font's glyph cache, and
    // its emboldened, sheared glyphs replaced the regular ones at every size
    // both were drawn at — the whole page came out bold italic. A font whose
    // data is not readable (a system symbol face) keeps its regular self.
    const auto data_it = me->face_data_.find(face);
    if (data_it == me->face_data_.end() || data_it->second.is_empty()) return face;
    std::vector<RID> derived;
    for (size_t slot = 0; slot < fonts->size(); ++slot) {
        const RID& base = (*fonts)[slot];
        // Only the primary font's data is known; the fallbacks (system symbol
        // faces) keep their regular selves.
        if (slot != 0) {
            derived.push_back(base);
            continue;
        }
        const RID v = ts->create_font();
        if (!v.is_valid()) {
            derived.push_back(base);
            continue;
        }
        ts->font_set_data(v, data_it->second);
        if (bold) ts->font_set_embolden(v, weight >= 800 ? 0.9 : 0.6);
        if (oblique) ts->font_set_transform(v, Transform2D(1.0, 0.0, 0.2, 1.0, 0.0, 0.0));
        me->owned_.push_back(v);
        derived.push_back(v);
    }
    if (derived.empty()) return face;
    const uint64_t handle = me->next_face_++;
    me->faces_[handle] = std::move(derived);
    me->variants_[key] = handle;
    return handle;
}

void GodotFontBackend::fill(weva_font_backend* out) {
    if (!out) return;
    out->user_data = this;
    out->load_face = &GodotFontBackend::load_face;
    out->face_metrics = &GodotFontBackend::face_metrics;
    out->glyph_index = &GodotFontBackend::glyph_index;
    out->glyph_metrics = &GodotFontBackend::glyph_metrics;
    out->rasterize = &GodotFontBackend::rasterize;
    out->shape = &GodotFontBackend::shape;
    out->variant = &GodotFontBackend::variant;
}

} // namespace weva_godot
