#pragma once

#include "weva_c.h"

#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/rid.hpp>
#include <godot_cpp/variant/typed_array.hpp>

#include <cstdint>
#include <map>
#include <memory>
#include <string>
#include <tuple>
#include <vector>

// libweva's font backend, implemented over Godot's TextServer.
//
// The core ships a 5x7 stub face so it can lay out and rasterise text with no
// host at all, which is what keeps it testable. That stub is not a font, and
// this is what replaces it: real faces, real metrics, and real shaping through
// the same HarfBuzz TextServer drives for every other control in the engine.
//
// It fills the C function-pointer table rather than subclassing anything: the
// core must not see a Godot type, and a `RID` reaching across the seam would
// end that.

namespace weva_godot {

struct SharedFontVariant;
// Called at scene-module shutdown while the TextServer still exists.
void release_shared_font_variants();

class GodotFontBackend {
public:
    // Fills a table whose `user_data` is this object. The table outlives the
    // call; the caller keeps it alive for as long as the document.
    void fill(weva_font_backend* out, weva_shape_glyphs_fn* positioned_shape = nullptr);
    // Releases loaded files and this backend's references to shared synthetic
    // variants; adopted engine fonts are left to their owners.
    ~GodotFontBackend();
    // Drop borrowed glyph IDs and owned variants before changing resources.
    // Existing face IDs are never reused during this backend's lifetime.
    void clear();

    // Adopts a face the engine already has — the theme's fallback font, say —
    // without going through a font file. Returns the handle to hand to
    // weva_document_set_font_backend.
    uint64_t adopt(const godot::RID& font);
    // A face with fallbacks: the first font is the face, the rest are tried
    // in order for glyphs it lacks (a symbol or emoji font behind the UI
    // face). Opaque glyph IDs retain the exact font TextServer chose, including
    // automatically discovered system fallbacks outside this initial list.
    uint64_t adopt(const godot::TypedArray<godot::RID>& fonts);
    // The same, with the primary font's file data, from which bold and italic
    // variants are built as independent fonts (see variant).
    // immutable_fallbacks is only for the host's private compatibility fonts:
    // every font after the primary must stay unchanged for the TextServer's
    // lifetime. Arbitrary resource fallback chains must leave this false.
    uint64_t adopt(const godot::TypedArray<godot::RID>& fonts,
                   const godot::PackedByteArray& primary_data, bool immutable_fallbacks = false);
    // A real bold or italic face for `face`, adopted separately: `variant`
    // answers with it, or the nearest one, before synthesizing. weight is the
    // CSS number (600-799 bold, 800+ black); a zero variant_face removes it.
    void set_real_variant(uint64_t face, int32_t weight, bool italic, uint64_t variant_face);

private:
    // Every entry point is static so its address fits a C function pointer;
    // each recovers the instance from user_data.
    static uint64_t load_face(void* self, const uint8_t* data, size_t length, int32_t index);
    static int32_t face_metrics(void* self, uint64_t face, double px, double* ascent,
                                double* descent, double* line_gap);
    static int32_t glyph_index(void* self, uint64_t face, uint32_t codepoint, uint32_t* out);
    static int32_t glyph_metrics(void* self, uint64_t face, uint32_t glyph, double px,
                                 double* advance, double* bearing_x, double* bearing_y,
                                 int32_t* width, int32_t* height);
    static int32_t rasterize(void* self, uint64_t face, uint32_t glyph, double px,
                             weva_glyph_bitmap* out);
    static size_t shape(void* self, uint64_t face, const char* utf8, size_t length, double px,
                        uint32_t* glyphs, double* advances, uint32_t* clusters, size_t capacity);
    static size_t shape_positioned(void* self, uint64_t face, const char* utf8, size_t length,
                                   double px, weva_shaped_glyph* out, size_t capacity);
    static uint64_t variant(void* self, uint64_t face, int32_t weight, int32_t italic);

    godot::RID resolve(uint64_t face, uint32_t slot = 0) const;
    const std::vector<godot::RID>* fonts_of(uint64_t face) const;

    struct GlyphSource { godot::RID font; int64_t index; };
    uint32_t retain_glyph(const godot::RID& font, int64_t index);
    const GlyphSource* glyph_source(uint64_t face, uint32_t glyph) const;
    std::vector<GlyphSource> glyph_sources_;
    std::map<std::pair<uint64_t, int64_t>, uint32_t> glyph_ids_;

    const std::vector<weva_shaped_glyph>& shape_run(uint64_t face, const char* utf8,
                                                  size_t length, double px);
    // One result bridges the sizing/fill calls. Face IDs identify immutable
    // fonts; the actual TextServer pixel size and source bytes complete the key.
    std::vector<weva_shaped_glyph> shaped_run_;
    std::string shaped_text_;
    uint64_t shaped_face_ = 0;
    int64_t shaped_size_ = 0;
    bool shaped_ready_ = false;
    struct ShapeProfile {
        size_t runs = 0, shared_hits = 0;
        double prepare_ms = 0, shape_ms = 0, extract_ms = 0, convert_ms = 0;
    } shape_profile_;

    std::map<uint64_t, std::vector<godot::RID>> faces_;
    // Synthetic bold changes outlines, not text advances. These immutable
    // regular peers are owned by the shared variants, never borrowed resources.
    std::map<uint64_t, godot::RID> metric_fonts_;
    // Synthesized faces by (base face, emboldening strength, italic): independent
    // primary fonts so synthesis cannot mutate the regular glyph cache.
    std::map<std::tuple<uint64_t, int, bool>, uint64_t> variants_;
    // Real files the host adopted for a weight/italic, by the same key.
    std::map<std::tuple<uint64_t, int, bool>, uint64_t> real_variants_;
    std::vector<std::shared_ptr<SharedFontVariant>> shared_variants_;
    // Only the host's private system-font fallback list can opt into sharing.
    // User-provided fallback resources remain mutable and never enter here.
    std::vector<uint64_t> immutable_fallback_faces_;
    struct SharedShapeFace {
        SharedFontVariant* primary = nullptr; // owned by shared_variants_
        std::vector<uint64_t> font_ids;
        uint64_t key = 1469598103934665603ULL;
    };
    std::map<uint64_t, SharedShapeFace> shared_shape_faces_;
    std::vector<godot::RID> owned_;
    std::map<uint64_t, godot::PackedByteArray> face_data_;
    uint64_t next_face_ = 1;
    // The core copies the bitmap before `rasterize` returns, so one reusable
    // buffer is enough and costs no per-glyph allocation.
    std::vector<uint8_t> scratch_;
    std::vector<uint8_t> scratch_rgba_;
};

} // namespace weva_godot
