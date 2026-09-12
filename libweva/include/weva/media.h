#pragma once
#include <memory>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Media/* and Runtime/Css/Cascade/SupportsEvaluator.cs —
// enough to decide whether a conditional at-rule's body applies.

namespace weva {

enum class ColorScheme { Light, Dark };
enum class HoverCapability { None, Hover };
enum class PointerCapability { None, Coarse, Fine };
enum class MediaType { All, Screen, Print };
enum class Orientation { Portrait, Landscape };

struct MediaContext {
    double viewport_width_px = 1920;
    double viewport_height_px = 1080;
    double dpi_pixels_per_inch = 96;
    ColorScheme color_scheme = ColorScheme::Light;
    HoverCapability hover = HoverCapability::Hover;
    PointerCapability pointer = PointerCapability::Fine;
    bool prefers_reduced_motion = false;
    MediaType type = MediaType::Screen;

    Orientation orientation() const {
        // Note >=: a square viewport is landscape, matching the C#.
        return viewport_width_px >= viewport_height_px ? Orientation::Landscape
                                                       : Orientation::Portrait;
    }
};

// Evaluates a full media query list ("screen and (min-width: 600px), print").
// An unparseable or unknown query evaluates to false, so an unrecognised
// condition hides its block rather than applying it unconditionally.
bool evaluate_media_query(std::string_view query, const MediaContext& ctx);

// Evaluates an @supports condition ("(display: grid) and (gap: 1px)").
bool evaluate_supports(std::string_view condition);

} // namespace weva
