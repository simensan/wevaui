#pragma once
#include "weva/font_interface.h"

#include <string_view>
#include <vector>

// Ports Runtime/Layout/Text/{IFontMetrics,MonoFontMetrics}.cs — the seam
// between layout and whatever actually measures glyphs.
//
// Keeping it this small is deliberate: it is the whole surface a Godot
// TextServer or a FreeType+HarfBuzz backend has to implement, and Phase 5 picks
// between them without touching layout.

namespace weva {

class FontMetrics {
public:
    virtual ~FontMetrics() = default;
    virtual double line_height(double font_size) const = 0;
    virtual double ascent(double font_size) const = 0;
    virtual double descent(double font_size) const = 0;
    // Measures a slice in place. Layout probes O(log n) prefixes of the same
    // word when wrapping, so this takes a view rather than forcing a copy per
    // probe.
    virtual double measure(std::string_view text, double font_size) const = 0;
};

// A deterministic per-em stand-in, so the layout pipeline can be exercised and
// tested without a font backend. Every value is a multiple of the font size, so
// results are identical on every machine.
//
// The parameterless shape (0.5 / 1.2 / 0.8 / 0.4) is what the reference's own
// tests pin their arithmetic against — "5 chars x 16px = 40px wide" — so it
// stays fixed. The Chrome factories are calibrated approximations for parity
// work, where a single scalar advance is a compromise: real per-glyph advances
// range from about 0.28em for `i` to 0.71em for `w`.
class MonoFontMetrics : public FontMetrics {
public:
    MonoFontMetrics() = default;
    MonoFontMetrics(double char_width_em, double line_height_em, double ascent_em,
                    double descent_em)
        : char_width_em_(char_width_em), line_height_em_(line_height_em),
          ascent_em_(ascent_em), descent_em_(descent_em) {}

    static MonoFontMetrics chrome_sans_serif() { return {0.45, 1.143, 0.85, 0.293}; }
    static MonoFontMetrics chrome_monospace() { return {0.6, 1.143, 0.85, 0.293}; }

    double line_height(double fs) const override { return fs * line_height_em_; }
    double ascent(double fs) const override { return fs * ascent_em_; }
    double descent(double fs) const override { return fs * descent_em_; }
    // Decodes UTF-8 so an emoji counts as one wide glyph rather than as its
    // bytes. Browsers render emoji from a separate face at roughly 1.3em, and
    // charging them the Latin advance underestimates a line by ~17px each.
    double measure(std::string_view text, double fs) const override;

    double char_width_em() const { return char_width_em_; }

private:
    double char_width_em_ = 0.5;
    double line_height_em_ = 1.2;
    double ascent_em_ = 0.8;
    double descent_em_ = 0.4;
};

// Drives FontMetrics from a FontInterface, so ONE backend serves both
// measurement and rendering.
//
// The two are separate seams because layout runs without a rasterizer and a
// rasterizer runs without layout — but a host that supplies a face must have
// them agree, or text is laid out to one face's advances and drawn with
// another's. Wiring them together is the caller's job, and this is the wire.
class FontInterfaceMetrics : public FontMetrics {
public:
    FontInterfaceMetrics(FontInterface* font, FaceHandle face) : font_(font), face_(face) {}

    double line_height(double fs) const override;
    double ascent(double fs) const override;
    double descent(double fs) const override;
    double measure(std::string_view text, double fs) const override;

private:
    FontInterface* font_;
    FaceHandle face_;
};

} // namespace weva
