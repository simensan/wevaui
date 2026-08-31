#pragma once
#include <cstdint>
#include <string_view>
#include <vector>

// Ports the shape ARCHITECTURE.md §2 specifies for the font backend.
//
// The C# rasterizer binds `FontEngine.TryRenderGlyphsToTexture` BY REFLECTION
// into undocumented internals of a Unity module, with a TextMeshPro fallback
// ladder. The interfaces above it were the right shape; the implementation
// underneath was never portable. This is that shape, with nothing behind it
// that a host cannot supply.
//
// Deliberately compatible with either Phase 5 candidate — FreeType+HarfBuzz, or
// Godot's TextServer — so the choice stays open.

namespace weva {

struct FaceHandle { uint64_t id = 0; explicit operator bool() const { return id != 0; } };

struct FaceMetrics {
    double ascent = 0;
    double descent = 0;     // positive, measured downward
    double line_gap = 0;
    double units_per_em = 0;
};

struct GlyphMetrics {
    double advance = 0;
    // Offsets from the pen position to the bitmap's top-left corner. `bearing_y`
    // is measured UP from the baseline, so a glyph with no descender has a
    // positive value and `p` has a negative one.
    double bearing_x = 0;
    double bearing_y = 0;
    int width = 0, height = 0;
};

// One glyph positioned by shaping. Kept separate from GlyphMetrics because
// shaping may substitute or reposition a glyph — a ligature has one entry for
// two code points, and the cluster says which.
struct ShapedGlyph {
    uint32_t glyph = 0;
    double x_advance = 0, y_advance = 0;
    double x_offset = 0, y_offset = 0;
    uint32_t cluster = 0;   // byte offset into the source text
};

enum class RenderMode : uint8_t { Alpha8, Sdf };

struct Bitmap {
    std::vector<uint8_t> data;   // one byte per pixel in Alpha8
    int width = 0, height = 0;
};

class FontInterface {
public:
    virtual ~FontInterface() = default;
    virtual FaceHandle load_face(const std::vector<uint8_t>& ttf, int index) = 0;
    virtual bool face_metrics(FaceHandle face, double px, FaceMetrics* out) = 0;
    virtual bool glyph_index(FaceHandle face, uint32_t codepoint, uint32_t* out) = 0;
    virtual bool glyph_metrics(FaceHandle face, uint32_t glyph, double px,
                               GlyphMetrics* out) = 0;
    virtual bool rasterize(FaceHandle face, uint32_t glyph, double px, RenderMode mode,
                           Bitmap* out) = 0;
    virtual void shape(FaceHandle face, std::string_view utf8, double px,
                       std::vector<ShapedGlyph>* out) = 0;
};

// A built-in 5x7 bitmap face covering printable ASCII, so the pipeline can
// render real glyph shapes headlessly and deterministically — the same role
// MonoFontMetrics plays for measurement.
//
// It is NOT a stand-in for a real face: no hinting, no kerning, and shaping is
// one glyph per code point in order. What it does give is a rendering path that
// can be asserted pixel by pixel on any machine, which is what the golden tests
// need before a real backend exists.
class StubFont : public FontInterface {
public:
    FaceHandle load_face(const std::vector<uint8_t>& ttf, int index) override;
    bool face_metrics(FaceHandle face, double px, FaceMetrics* out) override;
    bool glyph_index(FaceHandle face, uint32_t codepoint, uint32_t* out) override;
    bool glyph_metrics(FaceHandle face, uint32_t glyph, double px, GlyphMetrics* out) override;
    bool rasterize(FaceHandle face, uint32_t glyph, double px, RenderMode mode,
                   Bitmap* out) override;
    void shape(FaceHandle face, std::string_view utf8, double px,
               std::vector<ShapedGlyph>* out) override;

    // The one face this backend has; no file needed.
    static FaceHandle builtin() { return FaceHandle{1}; }
};

} // namespace weva
