#include "godot_font.h"
#include <chrono>
#include <cstdio>
#include <cstdlib>

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
#include <algorithm>
#include <cstring>
#include <unordered_map>

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

} // namespace

struct SharedFontVariant {
    struct Glyph {
        int64_t index = 0;
        weva_shaped_glyph positioned{};
    };
    struct Run {
        int64_t size = 0;
        std::string text;
        std::vector<uint64_t> font_ids;
        std::vector<Glyph> glyphs;
        uint64_t used = 0;
    };
    Ref<TextServer> owner;
    RID font;
    PackedByteArray data;
    int strength = 0;
    bool oblique = false;
    // These fonts are constructed from immutable file bytes and synthesis
    // inputs. Only runs wholly supplied by this one font may live here; no
    // borrowed font or fallback resource is part of a shared result.
    std::unordered_map<uint64_t, Run> runs;
    size_t cached_glyphs = 0;
    uint64_t run_clock = 0;
    void remember(uint64_t key, Run run) {
        const auto collision = runs.find(key);
        if (collision != runs.end()) {
            cached_glyphs -= collision->second.glyphs.capacity();
            runs.erase(collision);
        }
        // Bound both the number of strings and the expanded glyph payload.
        // Evict individual least-recently-used entries so changing labels
        // cannot flush every stable label in a document at once.
        while (!runs.empty() && (runs.size() >= 128 || cached_glyphs + run.glyphs.capacity() > 4096)) {
            auto oldest = runs.begin();
            for (auto it = runs.begin(); it != runs.end(); ++it)
                if (it->second.used < oldest->second.used) oldest = it;
            cached_glyphs -= oldest->second.glyphs.capacity();
            runs.erase(oldest);
        }
        run.used = ++run_clock;
        cached_glyphs += run.glyphs.capacity();
        runs.emplace(key, std::move(run));
    }
    ~SharedFontVariant() {
        if (font.is_valid() && owner.is_valid()) owner->free_rid(font);
    }
};

namespace {
std::vector<std::shared_ptr<SharedFontVariant>>& variant_font_cache() {
    static std::vector<std::shared_ptr<SharedFontVariant>> cache;
    return cache;
}

std::shared_ptr<SharedFontVariant> synthetic_font(TextServer* ts, const PackedByteArray& data,
                                                int strength, bool oblique) {
    static const bool disabled = std::getenv("WEVA_GODOT_DISABLE_VARIANT_CACHE") != nullptr;
    auto& cache = variant_font_cache();
    if (!disabled) for (size_t i = 0; i < cache.size(); ++i) {
        const auto& entry = cache[i];
        // Independent fonts use precisely these inputs. Compare the immutable
        // file bytes, not a borrowed source RID or a resource's object identity:
        // resources can change their data without changing their identity.
        if (entry->owner.ptr() != ts || entry->strength != strength ||
            entry->oblique != oblique || entry->data != data) continue;
        auto result = entry;
        cache.erase(cache.begin() + i);
        cache.push_back(result);
        return result;
    }
    const RID font = ts->create_font();
    if (!font.is_valid()) return {};
    auto result = std::make_shared<SharedFontVariant>();
    result->owner = Ref<TextServer>(ts);
    result->font = font;
    result->data = data;
    result->strength = strength;
    result->oblique = oblique;
    ts->font_set_data(font, data);
    if (strength) ts->font_set_embolden(font, strength == 2 ? 0.9 : 0.6);
    if (oblique) ts->font_set_transform(font, Transform2D(1.0, 0.0, 0.2, 1.0, 0.0, 0.0));
    if (!disabled) {
        // Active backends also hold a reference: evicting the oldest idle
        // cache slot cannot free a font still used by a published document.
        if (cache.size() == 8) cache.erase(cache.begin());
        cache.push_back(result);
    }
    return result;
}
}

void release_shared_font_variants() { variant_font_cache().clear(); }

GodotFontBackend::~GodotFontBackend() {
    clear();
}

void GodotFontBackend::clear() {
    if (shape_profile_.runs || shape_profile_.shared_hits) {
        std::fprintf(stderr, "font shaping: %zu runs; prepare %.3f shape %.3f extract %.3f convert %.3f ms; %zu shared hits\n",
            shape_profile_.runs, shape_profile_.prepare_ms, shape_profile_.shape_ms,
            shape_profile_.extract_ms, shape_profile_.convert_ms, shape_profile_.shared_hits);
        shape_profile_ = {};
    }
    TextServer* ts = server();
    if (ts) for (const RID& r : owned_) ts->free_rid(r);
    owned_.clear();
    faces_.clear();
    variants_.clear();
    face_data_.clear();
    glyph_sources_.clear();
    glyph_ids_.clear();
    shared_shape_faces_.clear();
    immutable_fallback_faces_.clear();
    shared_variants_.clear();
    shaped_ready_ = false;
    shaped_run_.clear();
    shaped_text_.clear();
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

uint64_t GodotFontBackend::adopt(const TypedArray<RID>& fonts, const PackedByteArray& primary_data,
                               bool immutable_fallbacks) {
    const uint64_t handle = adopt(fonts);
    if (handle && !primary_data.is_empty()) face_data_[handle] = primary_data;
    if (handle && immutable_fallbacks) immutable_fallback_faces_.push_back(handle);
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

uint32_t GodotFontBackend::retain_glyph(const RID& font, int64_t index) {
    if (!font.is_valid()) return 0;
    const auto key = std::make_pair(font.get_id(), index);
    const auto hit = glyph_ids_.find(key);
    if (hit != glyph_ids_.end()) return hit->second;
    const uint32_t id = static_cast<uint32_t>(glyph_sources_.size() + 1);
    glyph_sources_.push_back({font, index});
    glyph_ids_.emplace(key, id);
    return id;
}

const GodotFontBackend::GlyphSource* GodotFontBackend::glyph_source(uint64_t face,
                                                                 uint32_t glyph) const {
    if (!fonts_of(face) || !glyph || glyph > glyph_sources_.size()) return nullptr;
    return &glyph_sources_[glyph - 1];
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
    static const bool profile = std::getenv("WEVA_FONT_LOG") != nullptr;
    using Clock = std::chrono::steady_clock;
    const auto start = profile ? Clock::now() : Clock::time_point{};
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
    if (profile) std::fprintf(stderr, "font metrics: face %llu size %lld %.3f ms\n",
        static_cast<unsigned long long>(face), static_cast<long long>(size),
        std::chrono::duration<double, std::milli>(Clock::now() - start).count());
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
            *out = me->retain_glyph((*fonts)[slot], glyph);
            return 1;
        }
    }
    *out = me->retain_glyph(fonts->front(), 0);
    return 1;
}

int32_t GodotFontBackend::glyph_metrics(void* self, uint64_t face, uint32_t glyph, double px,
                                        double* advance, double* bearing_x, double* bearing_y,
                                        int32_t* width, int32_t* height) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts) return 0;
    const GlyphSource* source = me->glyph_source(face, glyph);
    if (!source) return 0;
    const RID font = source->font;
    const int64_t index = source->index;

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
    const GlyphSource* source = me->glyph_source(face, glyph);
    if (!source) return 0;
    const RID font = source->font;
    const int64_t index = source->index;

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

    // The core wants one coverage byte per pixel, plus the texels themselves
    // for a colour glyph. TextServer's atlas is LA8 for coverage glyphs and
    // RGBA8 for colour ones (a CBDT/COLR emoji); the alpha channel is the
    // coverage either way, and Image::get_pixel normalises both. A colour
    // glyph is one whose texels actually carry chroma — an RGBA8 page can
    // also hold plain coverage. Done once per glyph per size, not per frame.
    const size_t n = static_cast<size_t>(gw) * gh;
    me->scratch_.resize(n);
    const bool rgba_page = image->get_format() == Image::FORMAT_RGBA8 ||
                           image->get_format() == Image::FORMAT_RGB8;
    if (rgba_page) me->scratch_rgba_.resize(n * 4);
    bool chroma = false;
    for (int32_t y = 0; y < gh; ++y) {
        for (int32_t x = 0; x < gw; ++x) {
            const Color c = image->get_pixel(gx + x, gy + y);
            const size_t i = static_cast<size_t>(y) * gw + x;
            me->scratch_[i] = static_cast<uint8_t>(std::lround(c.a * 255.0));
            if (rgba_page) {
                me->scratch_rgba_[4 * i + 0] = static_cast<uint8_t>(std::lround(c.r * 255.0));
                me->scratch_rgba_[4 * i + 1] = static_cast<uint8_t>(std::lround(c.g * 255.0));
                me->scratch_rgba_[4 * i + 2] = static_cast<uint8_t>(std::lround(c.b * 255.0));
                me->scratch_rgba_[4 * i + 3] = me->scratch_[i];
                if (c.a > 0.02 && (std::fabs(c.r - c.g) > 0.02 || std::fabs(c.g - c.b) > 0.02 ||
                                   c.r < 0.98)) {
                    chroma = true;
                }
            }
        }
    }

    out->alpha = me->scratch_.data();
    out->width = gw;
    out->height = gh;
    out->rgba = (rgba_page && chroma) ? me->scratch_rgba_.data() : nullptr;
    return 1;
}

const std::vector<weva_shaped_glyph>& GodotFontBackend::shape_run(
    uint64_t face, const char* utf8, size_t length, double px) {
    const int64_t size = size_of(px);
    if (utf8 && shaped_ready_ && shaped_face_ == face && shaped_size_ == size &&
        shaped_text_.size() == length && std::memcmp(shaped_text_.data(), utf8, length) == 0)
        return shaped_run_;
    shaped_ready_ = false;
    shaped_run_.clear();
    TextServer* ts = server();
    if (!ts || !utf8) return shaped_run_;
    const std::vector<RID>* face_fonts = fonts_of(face);
    if (!face_fonts || face_fonts->empty()) return shaped_run_;

    static const bool profile = std::getenv("WEVA_FONT_LOG") != nullptr;
    using Clock = std::chrono::steady_clock;
    auto phase = profile ? Clock::now() : Clock::time_point{};
    const auto lap = [&](double& ms) {
        if (!profile) return;
        const auto next = Clock::now();
        ms += std::chrono::duration<double, std::milli>(next - phase).count();
        phase = next;
    };

    static const bool disable_shared_shapes = std::getenv("WEVA_GODOT_DISABLE_SHAPE_CACHE") != nullptr;
    SharedFontVariant* shared = nullptr;
    uint64_t shared_key = 1469598103934665603ULL;
    const SharedShapeFace* shared_face = nullptr;
    // A borrowed font can change without changing its RID. These entries are
    // only created for immutable synthesis plus immutable fallback inputs.
    if (!disable_shared_shapes && length <= 512) {
        const auto candidate = shared_shape_faces_.find(face);
        if (candidate != shared_shape_faces_.end() && candidate->second.primary->owner.ptr() == ts) {
            shared_face = &candidate->second;
            shared = shared_face->primary;
            shared_key = shared_face->key;
            for (size_t i = 0; i < length; ++i)
                shared_key = (shared_key ^ static_cast<unsigned char>(utf8[i])) * 1099511628211ULL;
            shared_key = (shared_key ^ static_cast<uint64_t>(size)) * 1099511628211ULL;
            const auto found = shared->runs.find(shared_key);
            if (found != shared->runs.end() && found->second.size == size &&
                found->second.font_ids == shared_face->font_ids &&
                found->second.text.size() == length &&
                std::memcmp(found->second.text.data(), utf8, length) == 0) {
                auto& run = found->second;
                run.used = ++shared->run_clock;
                shaped_run_.reserve(run.glyphs.size());
                for (const auto& saved : run.glyphs) {
                    auto glyph = saved.positioned;
                    // Published handles belong to this backend, even though
                    // the immutable native glyph index is shared.
                    glyph.glyph = saved.index ? retain_glyph(shared->font, saved.index) : 0;
                    shaped_run_.push_back(glyph);
                }
                shaped_text_.assign(utf8, length);
                shaped_face_ = face;
                shaped_size_ = size;
                shaped_ready_ = true;
                lap(shape_profile_.convert_ms);
                if (profile) ++shape_profile_.shared_hits;
                return shaped_run_;
            }
        }
    }

    const String text = String::utf8(utf8, static_cast<int64_t>(length));
    const int64_t text_length = text.length();
    // TextServer shapes UTF-32. Its cluster/start values index characters;
    // the C ABI and core use byte offsets into the original UTF-8 string.
    std::vector<uint32_t> byte_offsets(static_cast<size_t>(text_length) + 1);
    uint32_t byte = 0;
    for (int64_t i = 0; i < text_length; ++i) {
        byte_offsets[static_cast<size_t>(i)] = byte;
        const char32_t cp = text[i];
        byte += cp <= 0x7f ? 1 : cp <= 0x7ff ? 2 : cp <= 0xffff ? 3 : 4;
    }
    byte_offsets.back() = byte;
    if (byte != length) return shaped_run_; // invalid UTF-8 was not preserved by String
    const RID shaped = ts->create_shaped_text();
    if (!shaped.is_valid()) return shaped_run_;

    // Every font of the face, in order: TextServer falls back through the
    // list per character, and reports which font each glyph came from.
    TypedArray<RID> fonts;
    for (const RID& r : *face_fonts) fonts.push_back(r);
    lap(shape_profile_.prepare_ms);
    ts->shaped_text_add_string(shaped, text, fonts, size);
    ts->shaped_text_shape(shaped);
    lap(shape_profile_.shape_ms);

    const TypedArray<Dictionary> shaped_glyphs = ts->shaped_text_get_glyphs(shaped);
    lap(shape_profile_.extract_ms);
    // Build the same String keys once per run, instead of constructing six
    // native Strings/Variants for every glyph returned by TextServer.
    const Variant font_key("font_rid"), index_key("index"), start_key("start"),
                  advance_key("advance"), offset_key("offset"), repeat_key("repeat");
    const int64_t glyph_count = shaped_glyphs.size();
    SharedFontVariant::Run reusable;
    bool primary_only = shared != nullptr;
    if (shared) reusable.glyphs.reserve(static_cast<size_t>(std::min<int64_t>(512, glyph_count)));
    for (int64_t i = 0; i < glyph_count; ++i) {
        const Dictionary g = shaped_glyphs[i];
        const RID from = g[font_key];
        if (shared && from != shared->font) primary_only = false;
        const int64_t index = g[index_key];
        weva_shaped_glyph glyph{};
        // A valid TextServer font may be an automatic system fallback. Keep
        // it with the glyph rather than silently treating it as the primary.
        // Index zero on a valid shaped font is an invisible control glyph.
        glyph.glyph = from.is_valid() ? (index ? retain_glyph(from, index) : 0)
                                      : retain_glyph(face_fonts->front(), 0);
        const int64_t start = std::clamp<int64_t>(g[start_key], 0, text_length);
        glyph.cluster = byte_offsets[static_cast<size_t>(start)];
        glyph.x_advance = static_cast<double>(g[advance_key]);
        const Vector2 offset = g[offset_key];
        glyph.x_offset = offset.x;
        glyph.y_offset = -offset.y; // core offsets are upward from the baseline
        const int64_t repeat = g[repeat_key];
        for (int64_t r = 0; r < repeat; ++r) {
            shaped_run_.push_back(glyph);
            if (primary_only && reusable.glyphs.size() < 512) reusable.glyphs.push_back({index, glyph});
            else primary_only = false;
        }
    }
    ts->free_rid(shaped);
    if (primary_only) {
        reusable.size = size;
        reusable.text.assign(utf8, length);
        reusable.font_ids = shared_face->font_ids;
        shared->remember(shared_key, std::move(reusable));
    }
    shaped_text_.assign(utf8, length);
    shaped_face_ = face;
    shaped_size_ = size;
    shaped_ready_ = true;
    lap(shape_profile_.convert_ms);
    if (profile) ++shape_profile_.runs;
    return shaped_run_;
}

size_t GodotFontBackend::shape_positioned(void* self, uint64_t face, const char* utf8,
                                         size_t length, double px, weva_shaped_glyph* out,
                                         size_t capacity) {
    auto* me = static_cast<GodotFontBackend*>(self);
    if (!me) return 0;
    const auto& run = me->shape_run(face, utf8, length, px);
    if (out) std::copy_n(run.begin(), std::min(capacity, run.size()), out);
    return run.size();
}

size_t GodotFontBackend::shape(void* self, uint64_t face, const char* utf8, size_t length,
                               double px, uint32_t* glyphs, double* advances, uint32_t* clusters,
                               size_t capacity) {
    auto* me = static_cast<GodotFontBackend*>(self);
    if (!me) return 0;
    const auto& run = me->shape_run(face, utf8, length, px);
    for (size_t i = 0; i < std::min(capacity, run.size()); ++i) {
        if (glyphs) glyphs[i] = run[i].glyph;
        if (advances) advances[i] = run[i].x_advance;
        if (clusters) clusters[i] = run[i].cluster;
    }
    return run.size();
}

uint64_t GodotFontBackend::variant(void* self, uint64_t face, int32_t weight, int32_t italic) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts) return face;
    const int strength = weight >= 800 ? 2 : weight >= 600 ? 1 : 0;
    const bool bold = strength != 0;
    const bool oblique = italic != 0;
    if (!bold && !oblique) return face;
    const auto key = std::make_tuple(face, strength, oblique);
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
    SharedFontVariant* primary_variant = nullptr;
    for (size_t slot = 0; slot < fonts->size(); ++slot) {
        const RID& base = (*fonts)[slot];
        // Only the primary font's data is known; the fallbacks (system symbol
        // faces) keep their regular selves.
        if (slot != 0) {
            derived.push_back(base);
            continue;
        }
        auto variant = synthetic_font(ts, data_it->second, strength, oblique);
        if (!variant) {
            derived.push_back(base);
            continue;
        }
        derived.push_back(variant->font);
        primary_variant = variant.get();
        me->shared_variants_.push_back(std::move(variant));
    }
    if (derived.empty()) return face;
    const uint64_t handle = me->next_face_++;
    if (primary_variant && derived.size() <= 64 && (fonts->size() == 1 ||
        std::find(me->immutable_fallback_faces_.begin(), me->immutable_fallback_faces_.end(), face) !=
        me->immutable_fallback_faces_.end())) {
        SharedShapeFace input;
        input.primary = primary_variant;
        for (const RID& font : derived) {
            const uint64_t id = font.get_id();
            input.font_ids.push_back(id);
            input.key = (input.key ^ id) * 1099511628211ULL;
        }
        me->shared_shape_faces_.emplace(handle, std::move(input));
    }
    if (std::getenv("WEVA_FONT_LOG")) std::fprintf(stderr,
        "font variant: base %llu derived %llu weight %d italic %d\n",
        static_cast<unsigned long long>(face), static_cast<unsigned long long>(handle), weight, italic);
    me->faces_[handle] = std::move(derived);
    me->variants_[key] = handle;
    return handle;
}

void GodotFontBackend::fill(weva_font_backend* out, weva_shape_glyphs_fn* positioned_shape) {
    if (!out) return;
    out->user_data = this;
    out->load_face = &GodotFontBackend::load_face;
    out->face_metrics = &GodotFontBackend::face_metrics;
    out->glyph_index = &GodotFontBackend::glyph_index;
    out->glyph_metrics = &GodotFontBackend::glyph_metrics;
    out->rasterize = &GodotFontBackend::rasterize;
    out->shape = &GodotFontBackend::shape;
    out->variant = &GodotFontBackend::variant;
    if (positioned_shape) *positioned_shape = &GodotFontBackend::shape_positioned;
}

} // namespace weva_godot
