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

#include "unicode/uchar.h"

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

// Stock Godot 4.7 (modules/text_server_adv/script_iterator.cpp) grows two
// fixed stacks without copying their contents. More than 32 emoji sub-runs in
// one script run corrupt the glyph ranges, and the grown stack is freed before
// the next script run, which then writes through the dangling pointer and can
// crash the process. More than 128 unmatched open brackets do the same. The
// adapter therefore shapes such text in pieces that never fill either stack.
// Every split lands exactly where the engine would start a new emoji sub-run
// or push another bracket, so no ligature or kerning pair spans a boundary,
// and ordinary text keeps taking the single-buffer path unchanged.
constexpr int64_t kEmojiSubrunStackDepth = 32;
constexpr int64_t kBracketStackDepth = 128;
constexpr char32_t kZeroWidthJoiner = 0x200d;
constexpr char32_t kVariationSelector15 = 0xfe0e;
constexpr char32_t kVariationSelector16 = 0xfe0f;
constexpr char32_t kCombiningEnclosingKeycap = 0x20e3;

// Mirrors ScriptIterator::is_emoji so the count below matches the engine's.
bool engine_is_emoji(char32_t c, char32_t next) {
    const bool pictographic = u_hasBinaryProperty(c, UCHAR_EMOJI) ||
                              u_hasBinaryProperty(c, UCHAR_EXTENDED_PICTOGRAPHIC);
    if (next == kVariationSelector15 && pictographic) return false;
    if (next == kVariationSelector16 && pictographic) return true;
    return u_hasBinaryProperty(c, UCHAR_EMOJI_PRESENTATION) ||
           u_hasBinaryProperty(c, UCHAR_EMOJI_MODIFIER) ||
           u_hasBinaryProperty(c, UCHAR_REGIONAL_INDICATOR) ||
           (u_hasBinaryProperty(c, UCHAR_EMOJI) && u_hasBinaryProperty(next, UCHAR_EMOJI_MODIFIER));
}

// Character indices at which a fresh shaped-text buffer must begin. Empty for
// text the engine's starter stacks can hold, which is nearly all of it.
std::vector<int64_t> shaping_piece_starts(const char32_t* s, int64_t n) {
    std::vector<int64_t> starts;
    std::vector<char32_t> open_brackets;
    int64_t emoji_subruns = 0;
    bool emoji_run = false;
    const auto split_at = [&](int64_t i) {
        starts.push_back(i);
        emoji_subruns = 0;
        emoji_run = false;
        open_brackets.clear();
    };
    for (int64_t i = 0; i < n; ++i) {
        const char32_t c = s[i];
        const char32_t next = i + 1 < n ? s[i + 1] : 0;
        int32_t bracket = U_BPT_NONE;
        if (c < 0x80 && next < 0x80) {
            // ASCII never continues or starts an emoji sub-run on its own; a
            // keycap base needs the non-ASCII selector that follows it.
            emoji_run = false;
            if (c == '(' || c == '[' || c == '{') bracket = U_BPT_OPEN;
            else if (c == ')' || c == ']' || c == '}') bracket = U_BPT_CLOSE;
        } else {
            if (engine_is_emoji(c, next)) {
                if (!emoji_run) {
                    if (emoji_subruns == kEmojiSubrunStackDepth) split_at(i);
                    emoji_run = true;
                    ++emoji_subruns;
                }
            } else if (emoji_run && c != kZeroWidthJoiner && c != kVariationSelector16 &&
                       c != kCombiningEnclosingKeycap &&
                       !(u_hasBinaryProperty(c, UCHAR_EXTENDED_PICTOGRAPHIC) && next != kVariationSelector15)) {
                emoji_run = false;
            }
            bracket = u_getIntPropertyValue(c, UCHAR_BIDI_PAIRED_BRACKET_TYPE);
        }
        if (bracket == U_BPT_OPEN) {
            if (static_cast<int64_t>(open_brackets.size()) == kBracketStackDepth) split_at(i);
            open_brackets.push_back(c);
        } else if (bracket == U_BPT_CLOSE && !open_brackets.empty()) {
            // The engine pops unmatched opens down to the pair and then the
            // pair itself; a close with no pair on the stack empties it.
            const char32_t pair = static_cast<char32_t>(u_getBidiPairedBracket(static_cast<UChar32>(c)));
            while (!open_brackets.empty() && open_brackets.back() != pair) open_brackets.pop_back();
            if (!open_brackets.empty()) open_brackets.pop_back();
        }
    }
    return starts;
}

// The paragraph direction the engine's automatic detection would choose, so
// every piece of one run agrees on it.
TextServer::Direction strong_direction(const char32_t* s, int64_t n) {
    for (int64_t i = 0; i < n; ++i) {
        switch (u_charDirection(static_cast<UChar32>(s[i]))) {
            case U_LEFT_TO_RIGHT: return TextServer::DIRECTION_LTR;
            case U_RIGHT_TO_LEFT:
            case U_RIGHT_TO_LEFT_ARABIC: return TextServer::DIRECTION_RTL;
            default: break;
        }
    }
    return TextServer::DIRECTION_LTR;
}

// Shapes the text as one buffer, or as the pieces the stock engine can hold.
// Glyphs come back in visual order with whole-string character indices, the
// same contract as a single shaped_text_get_glyphs call.
bool shape_text_pieces(TextServer* ts, const String& text, const TypedArray<RID>& fonts, int64_t size,
                       const std::vector<int64_t>& piece_starts, TypedArray<Dictionary>& glyphs) {
    if (piece_starts.empty()) {
        const RID shaped = ts->create_shaped_text();
        if (!shaped.is_valid()) return false;
        ts->shaped_text_add_string(shaped, text, fonts, size);
        ts->shaped_text_shape(shaped);
        glyphs = ts->shaped_text_get_glyphs(shaped);
        ts->free_rid(shaped);
        return true;
    }
    const int64_t length = text.length();
    const TextServer::Direction direction = strong_direction(text.ptr(), length);
    std::vector<TypedArray<Dictionary>> pieces;
    pieces.reserve(piece_starts.size() + 1);
    int64_t begin = 0;
    for (size_t p = 0; p <= piece_starts.size(); ++p) {
        const int64_t end = p < piece_starts.size() ? piece_starts[p] : length;
        const RID shaped = ts->create_shaped_text(direction);
        if (!shaped.is_valid()) return false;
        ts->shaped_text_add_string(shaped, text.substr(begin, end - begin), fonts, size);
        ts->shaped_text_shape(shaped);
        TypedArray<Dictionary> piece = ts->shaped_text_get_glyphs(shaped);
        ts->free_rid(shaped);
        for (int64_t i = 0; i < piece.size(); ++i) {
            Dictionary g = piece[i];
            g["start"] = static_cast<int64_t>(g["start"]) + begin;
            g["end"] = static_cast<int64_t>(g["end"]) + begin;
        }
        pieces.push_back(piece);
        begin = end;
    }
    // A right-to-left run reads its later pieces first.
    if (direction == TextServer::DIRECTION_RTL) std::reverse(pieces.begin(), pieces.end());
    glyphs = TypedArray<Dictionary>();
    for (const auto& piece : pieces) glyphs.append_array(piece);
    return true;
}

} // namespace

struct SharedFontVariant {
    std::shared_ptr<SharedFontVariant> metrics;
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
// TextServer's automatic subpixel mode snaps advances to whole pixels above
// 20 px; Chrome positions fractionally at every size. Measured with the
// sample's font against Chrome (docs/verification/textserver-subpixel.json):
// automatic is up to 0.95 px off per string above 20 px, quarter-pixel
// positioning within 0.09 px at every size, and hinting does not move
// advances. Every font the adapter owns is positioned this way; a game's own
// Font resource is never modified, a private copy of its bytes is made instead.
void position_fractionally(TextServer* ts, const RID& font) {
    ts->font_set_subpixel_positioning(font, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
}

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
    position_fractionally(ts, font);
    if (strength) result->metrics = synthetic_font(ts, data, 0, false);
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
    metric_fonts_.clear();
    variants_.clear();
    real_variants_.clear();
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
    // With the primary's bytes in hand, the face renders and measures through
    // a private, fractionally positioned copy rather than the borrowed
    // resource, which keeps its own settings for the game's native controls.
    // The fallbacks stay borrowed.
    std::shared_ptr<SharedFontVariant> regular;
    TextServer* ts = server();
    TypedArray<RID> owned = fonts;
    if (ts && !primary_data.is_empty() && fonts.size() > 0) {
        regular = synthetic_font(ts, primary_data, 0, false);
        if (regular) {
            owned = fonts.duplicate();
            owned[0] = regular->font;
        }
    }
    const uint64_t handle = adopt(owned);
    if (!handle) return 0;
    if (!primary_data.is_empty()) face_data_[handle] = primary_data;
    if (immutable_fallbacks) immutable_fallback_faces_.push_back(handle);
    if (regular) {
        shared_variants_.push_back(regular);
        if (fonts.size() == 1 || immutable_fallbacks) share_shapes(handle, regular.get());
    }
    return handle;
}

void GodotFontBackend::share_shapes(uint64_t face, SharedFontVariant* primary) {
    const std::vector<RID>* fonts = fonts_of(face);
    if (!primary || !fonts || fonts->size() > 64) return;
    SharedShapeFace input;
    input.primary = primary;
    for (const RID& font : *fonts) {
        const uint64_t id = font.get_id();
        input.font_ids.push_back(id);
        input.key = (input.key ^ id) * 1099511628211ULL;
    }
    shared_shape_faces_.emplace(face, std::move(input));
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
    position_fractionally(ts, font);
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
    double a = ts->font_get_ascent(font, size);
    double d = ts->font_get_descent(font, size);
    if (me->face_data_.find(face) != me->face_data_.end()) {
        // FreeType's pixel-sized metrics round each extent outward. Blink
        // rounds the scaled design extents to the nearest pixel instead.
        // Query an unhinted large scale to recover the design proportions;
        // this only reads face metrics, never rasterizes oversized glyphs.
        constexpr int64_t design_size = 16384;
        a = std::round(ts->font_get_ascent(font, design_size) * px / design_size);
        d = std::round(ts->font_get_descent(font, design_size) * px / design_size);
    }
    if (ascent) *ascent = a;
    if (descent) *descent = d;
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
    const auto metric = me->metric_fonts_.find(font.get_id());
    const Vector2 adv = ts->font_get_glyph_advance(
        metric == me->metric_fonts_.end() ? font : metric->second, size, index);
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
    const std::vector<int64_t> piece_starts = shaping_piece_starts(text.ptr(), text_length);

    // Every font of the face, in order: TextServer falls back through the
    // list per character, and reports which font each glyph came from.
    TypedArray<RID> fonts;
    const RID primary_render = face_fonts->front();
    const auto metric = metric_fonts_.find(primary_render.get_id());
    const RID primary_metrics = metric == metric_fonts_.end() ? primary_render : metric->second;
    for (const RID& r : *face_fonts) fonts.push_back(r == primary_render ? primary_metrics : r);
    lap(shape_profile_.prepare_ms);
    TypedArray<Dictionary> shaped_glyphs;
    if (!shape_text_pieces(ts, text, fonts, size, piece_starts, shaped_glyphs)) return shaped_run_;
    lap(shape_profile_.shape_ms);

    // Reuse native keys in both the synthesis check and glyph conversion.
    const Variant font_key("font_rid"), index_key("index"), start_key("start"),
                  advance_key("advance"), offset_key("offset"), repeat_key("repeat");
    bool styled_positions = false;
    struct PrimaryAdvances {
        std::vector<double> values;
        size_t consumed = 0;
    };
    std::map<std::pair<int64_t, int64_t>, PrimaryAdvances> primary_advances;
    if (primary_metrics != primary_render) {
        for (const Dictionary glyph : shaped_glyphs) {
            const RID from = glyph[font_key];
            const Vector2 offset = glyph[offset_key];
            if ((from == primary_metrics && offset != Vector2()) ||
                (from.is_valid() && from != primary_metrics &&
                 std::find(face_fonts->begin() + 1, face_fonts->end(), from) == face_fonts->end())) {
                styled_positions = true;
                break;
            }
        }
        if (styled_positions) {
            // Native automatic fallbacks inherit synthesis from the primary.
            // Preserve that selection and synthesized mark attachment offsets
            // while taking primary advances from the regular shaper
            // (subtracting glyph metrics loses hinted kerning).
            for (const Dictionary glyph : shaped_glyphs) {
                const RID from = glyph[font_key];
                if (from == primary_metrics)
                    primary_advances[{static_cast<int64_t>(glyph[start_key]),
                                      static_cast<int64_t>(glyph[index_key])}].values.push_back(glyph[advance_key]);
            }
            fonts[0] = primary_render;
            lap(shape_profile_.extract_ms);
            if (!shape_text_pieces(ts, text, fonts, size, piece_starts, shaped_glyphs)) return shaped_run_;
            lap(shape_profile_.shape_ms);
        }
    }
    lap(shape_profile_.extract_ms);
    const int64_t glyph_count = shaped_glyphs.size();
    SharedFontVariant::Run reusable;
    bool primary_only = shared != nullptr;
    if (shared) reusable.glyphs.reserve(static_cast<size_t>(std::min<int64_t>(512, glyph_count)));
    for (int64_t i = 0; i < glyph_count; ++i) {
        const Dictionary g = shaped_glyphs[i];
        const RID from = g[font_key];
        if (shared && from != (styled_positions ? primary_render : primary_metrics)) primary_only = false;
        const int64_t index = g[index_key];
        weva_shaped_glyph glyph{};
        // A valid TextServer font may be an automatic system fallback. Keep
        // it with the glyph rather than silently treating it as the primary.
        // Index zero on a valid shaped font is an invisible control glyph.
        const RID rendered = from == primary_metrics ? primary_render : from;
        glyph.glyph = from.is_valid() ? (index ? retain_glyph(rendered, index) : 0)
                                      : retain_glyph(face_fonts->front(), 0);
        const int64_t start = std::clamp<int64_t>(g[start_key], 0, text_length);
        glyph.cluster = byte_offsets[static_cast<size_t>(start)];
        glyph.x_advance = static_cast<double>(g[advance_key]);
        if (styled_positions && from == primary_render) {
            const auto advance = primary_advances.find({static_cast<int64_t>(g[start_key]), index});
            if (advance != primary_advances.end() && advance->second.consumed < advance->second.values.size()) {
                glyph.x_advance = advance->second.values[advance->second.consumed++];
            }
        }
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

void GodotFontBackend::set_real_variant(uint64_t face, int32_t weight, bool italic, uint64_t variant_face) {
    const int strength = weight >= 800 ? 2 : weight >= 600 ? 1 : 0;
    const auto key = std::make_tuple(face, strength, italic);
    if (variant_face) real_variants_[key] = variant_face;
    else real_variants_.erase(key);
}

uint64_t GodotFontBackend::variant(void* self, uint64_t face, int32_t weight, int32_t italic) {
    GodotFontBackend* me = static_cast<GodotFontBackend*>(self);
    TextServer* ts = server();
    if (!me || !ts) return face;
    const int strength = weight >= 800 ? 2 : weight >= 600 ? 1 : 0;
    const bool bold = strength != 0;
    const bool oblique = italic != 0;
    if (!bold && !oblique) return face;
    // A real file first: the exact weight and slant, else the other bold
    // strength with the same slant, else a real face for one axis with the
    // other synthesized on top of it (an italic shear over the bold file, or
    // emboldening over the italic file), the way a browser matches faces.
    const auto real = [&](int s, bool o) -> uint64_t {
        const auto it = me->real_variants_.find(std::make_tuple(face, s, o));
        return it == me->real_variants_.end() ? 0 : it->second;
    };
    if (bold) {
        if (const uint64_t exact = real(strength, oblique)) return exact;
        if (const uint64_t other = real(strength == 2 ? 1 : 2, oblique)) return other;
        if (oblique) {
            if (const uint64_t real_bold = real(strength, false)) return variant(self, real_bold, 400, 1);
            if (const uint64_t other_bold = real(strength == 2 ? 1 : 2, false)) return variant(self, other_bold, 400, 1);
            if (const uint64_t real_italic = real(0, true)) return variant(self, real_italic, weight, 0);
        }
    } else if (const uint64_t real_italic = real(0, true)) {
        return real_italic;
    }
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
        if (variant->metrics) me->metric_fonts_[variant->font.get_id()] = variant->metrics->font;
        primary_variant = variant.get();
        me->shared_variants_.push_back(std::move(variant));
    }
    if (derived.empty()) return face;
    const uint64_t handle = me->next_face_++;
    const bool shareable = primary_variant && (fonts->size() == 1 ||
        std::find(me->immutable_fallback_faces_.begin(), me->immutable_fallback_faces_.end(), face) !=
        me->immutable_fallback_faces_.end());
    if (std::getenv("WEVA_FONT_LOG")) std::fprintf(stderr,
        "font variant: base %llu derived %llu weight %d italic %d\n",
        static_cast<unsigned long long>(face), static_cast<unsigned long long>(handle), weight, italic);
    me->faces_[handle] = std::move(derived);
    // The same bytes as the base: face_metrics rounds a synthesized
    // variant's design extents the way it rounds the regular face's, so a
    // bold heading's line is 43px at 32px like Chrome's, not FreeType's
    // ceiled 45.
    me->face_data_[handle] = data_it->second;
    me->variants_[key] = handle;
    if (shareable) me->share_shapes(handle, primary_variant);
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
