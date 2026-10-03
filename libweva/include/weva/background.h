#pragma once
#include "weva/render_interface.h"
#include "weva/color.h"
#include "weva/image_decode.h"
#include "weva/computed_style.h"
#include "weva/geometry.h"
#include "weva/style_resolver.h"

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

// CSS Backgrounds L3 §3 and CSS Images L3 §3: the background layers of a box
// and the gradients they carry, rasterized into one texture per box.
//
// Ported     `background-color` under `background-image` layers (first layer
//            on top); linear-gradient with an angle or `to <side/corner>`
//            (magic corners); radial-gradient with circle/ellipse, the four
//            size keywords, explicit radii and `at <position>`; conic-gradient
//            with `from <angle>` and `at <position>`; the repeating- forms;
//            colour stops in %, px or bare numbers, double-position stops and
//            colour hints, interpolated in premultiplied sRGB (Images L4 §3.4.1);
//            `background-position`, `-size` (lengths, %, cover/contain, auto)
//            and `-repeat` per layer.
//
// NOT ported url() images (the layer is kept and skipped), `background-origin`
//            / `-clip` / `-attachment`, interpolation in other colour spaces
//            (`in oklab` is parsed and ignored), image-set() and cross-fade().

namespace weva {

// See background_texture_detail(). Measured, not guessed: the corpus was
// rendered at 1024, 512, 256, 128 and 64 and compared channel by channel
// against the 1024 renders.
//
//     512   worst channel delta  2, 0.02% of pixels off by more than 1
//     256   worst channel delta  5, 0.05%
//     128   worst channel delta  8, 0.34%
//      64   worst channel delta 15, 1.27%
//
// 512 is where the difference disappears into the 8-bit quantisation already
// there, and it still cuts the texels by four. 256 is tempting -- it takes
// form-demo's background from 4.5 ms to 2.1 -- but a delta of 5 is a real if
// faint band, and the samples that show it (episode-stats, story-bubble) get it
// from the KNEE at an interior stop, which a coarser texture rounds off.
// Sharpening the predicate to notice knees would earn the lower number.
inline constexpr int kSmoothGradientTexels = 512;

struct GradientStop {
    LinearColor color;
    double position = 0;      // fraction of the gradient line / turn, or px
    bool has_position = false;
    bool is_px = false;
    bool is_hint = false;     // a bare position between two stops (§3.4.3)
};

struct Gradient {
    enum class Kind { Linear, Radial, Conic } kind = Kind::Linear;
    bool repeating = false;

    // Linear: the angle, or a corner (§3.1.1 "magic corners") resolved against
    // the box at raster time. corner bits: 1 left, 2 right, 4 top, 8 bottom.
    double angle_deg = 180;
    int corner = 0;

    // Radial: shape and size (§3.2.1); explicit radii kept raw so % resolves
    // against the box.
    enum class Sizing { ClosestSide, FarthestSide, ClosestCorner, FarthestCorner, Explicit };
    bool circle = false;
    Sizing sizing = Sizing::FarthestCorner;
    std::string radius_x_raw, radius_y_raw;

    // Radial and conic: centre, raw <position> components.
    std::string pos_x_raw = "50%", pos_y_raw = "50%";
    // Conic: the start angle.
    double from_deg = 0;

    std::vector<GradientStop> stops;
};

// Parses one `<gradient>()` image. `current_color` resolves `currentcolor`
// stops. False when the text is not a gradient this port reads.
bool parse_gradient(std::string_view raw, const LinearColor& current_color, Gradient* out);

struct Box;

struct BackgroundLayer {
    bool is_gradient = false;
    Gradient gradient;
    std::string url;                            // an image layer's source
    // Resolved from `url` before rasterizing, by whoever owns the ImageStore.
    // Null means the file was missing or is not a format the decoder knows,
    // and the layer paints nothing -- which is what the whole engine did with
    // an image before this existed.
    const DecodedImage* image = nullptr;
    std::string pos_x = "0%", pos_y = "0%";     // background-position, raw
    std::string size_x = "auto", size_y = "auto";   // background-size, raw
    bool repeat_x = true, repeat_y = true;
    // CSS Compositing 1 §9 background-blend-mode: how this layer blends with
    // the layers and colour below it. Normal is plain source-over.
    BlendMode blend = BlendMode::Normal;
    // CSS Masking 1 §6: a `mask-image` layer. It paints nothing; its
    // coverage (alpha, or luminance times alpha) multiplies what the paint
    // layers produced. Several mask layers add (mask-composite: add).
    bool is_mask = false;
    bool mask_luminance = false;
    // CSS Backgrounds L3 §3.5 background-attachment. `fixed` positions and
    // sizes the layer against the viewport and keeps it still under
    // scrolling; `local` scrolls it with a scroll container's content. Paint
    // fills in the positioning area and the shift from the box's own origin
    // (apply_background_attachment); the rasterizer just uses them.
    bool attachment_fixed = false;
    bool attachment_local = false;
    double area_w = 0, area_h = 0;      // 0: the box's own painting area
    double shift_x = 0, shift_y = 0;    // added to the tile origin
};

// Resolves the fixed / local layers' positioning areas for a box painted
// with its border box at (`x`, `y`) in visual document coordinates.
void apply_background_attachment(std::vector<BackgroundLayer>* layers, const Box& box,
                                 const LayoutContext& ctx, double x, double y);

// CSS Compositing 1 §11 <blend-mode> keywords, in the specification's order;
// anything else is normal.
BlendMode blend_mode_from_keyword(std::string_view raw);

// The box's `background-image` layers with their position/size/repeat and
// blend mode, top layer first as in the property, followed by its
// `mask-image` layers (flagged `is_mask`). Empty when there is neither.
std::vector<BackgroundLayer> resolve_background_layers(const ComputedStyle* style,
                                                       const LinearColor& current_color);

// The texture side a SMOOTH background can be rasterized at without losing
// anything, or 0 for "as many texels as the box has pixels".
//
// A gradient with no discontinuity in it is a slowly varying function, and the
// bilinear filter that samples the texture reconstructs it from far fewer texels
// than the box has pixels -- form-demo's page background is one
// `radial-gradient(1200px 600px at 50% -10%, ...)`, and rasterizing it at
// 1024x1024 cost 14 ms of a 15 ms frame for detail nothing can see. At the cap
// it is 4.5 ms, and the worst pixel anywhere in the corpus moves by 2 of 255.
//
// A discontinuity is the thing this must not touch: a conic gradient closes on
// itself, two stops at one position are a hard band, and a repeating layer's
// tiles have edges where they meet. Those all return 0 and keep every texel.
int background_texture_detail(const std::vector<BackgroundLayer>& layers);

// Whether these layers rasterize to the SAME texels at any box size, given the
// same texture size -- because every coordinate in them is a fraction of the
// box, and divides straight back out.
//
// This is what lets a background texture survive a resize. The cache key
// otherwise carries the box's width and height, so a box one pixel wider is a
// miss and a full re-rasterization: on grid-playground, whose <body> carries a
// 1024x1024 two-layer gradient, that was 28 ms for a one-pixel padding change.
//
// Being wrong here puts the wrong picture on screen, and no page-comparison
// gate would catch it -- the captures are of documents that never resize. The
// test that does is test_background_size_independence, which rasterizes each
// answer at several sizes and holds the promise to the pixel.
bool background_size_independent(const std::vector<BackgroundLayer>& layers);

// Rasterizes `color` under `layers` over a `width` x `height` painting area
// into `tex_w` x `tex_h` straight-alpha sRGB RGBA8 texels. Gradient boxes are
// the layer tiles; positions and sizes resolve with `ctx` and `font_size`.
void rasterize_background(const std::vector<BackgroundLayer>& layers, const LinearColor& color,
                          double width, double height, int tex_w, int tex_h,
                          const LayoutContext& ctx, double font_size,
                          std::vector<uint8_t>* out_rgba);

// The same, drawn `pad` texels inside a larger texture with the box's rounded
// corners applied as a mask, for painting that needs room around the box (a
// blur). `radii` may be null for square corners.
void rasterize_background_padded(const std::vector<BackgroundLayer>& layers,
                                 const LinearColor& color, double width, double height,
                                 int tex_w, int tex_h, int pad, const struct BorderRadii* radii,
                                 const LayoutContext& ctx, double font_size,
                                 std::vector<uint8_t>* out_rgba);

// The two halves of the above. prepare_background resolves every CSS value a
// rasterization needs -- positions, sizes, gradient geometry -- and so parses,
// which only the thread that owns the document may do; the CSS parser keeps
// single-threaded scratch state. Rasterizing a prepared plan reads nothing
// but the plan and the decoded images it names, so any thread may run it,
// and several at once. Paint queues its rasters this way (RasterQueue).
struct BackgroundPlan {
    virtual ~BackgroundPlan() = default;
};
std::shared_ptr<const BackgroundPlan> prepare_background(
    const std::vector<BackgroundLayer>& layers, const LinearColor& color, double width,
    double height, int tex_w, int tex_h, const LayoutContext& ctx, double font_size);
void rasterize_background(const BackgroundPlan& plan, std::vector<uint8_t>* out_rgba);
// Roughly how long rasterizing `plan` takes, in nanoseconds on a desktop
// core: enough to order a pass's jobs, not to predict a frame.
long long raster_cost(const BackgroundPlan& plan);
// `tex_w` x `tex_h` is the padded texture; the plan is its inside.
std::shared_ptr<const BackgroundPlan> prepare_background_padded(
    const std::vector<BackgroundLayer>& layers, const LinearColor& color, double width,
    double height, int tex_w, int tex_h, int pad, const LayoutContext& ctx, double font_size);
void rasterize_background_padded(const BackgroundPlan& plan, int tex_w, int tex_h, int pad,
                                 const struct BorderRadii* radii, std::vector<uint8_t>* out_rgba);

// Coverage of a rounded rectangle at a point, with the box's origin at (0,0)
// and `radii` its corners. Antialiased: half a texel either side of the edge,
// which is exact for a circular corner and within a percent for the
// eccentricities a border-radius produces. Null radii means square corners.
double rounded_rect_coverage(double px, double py, double w, double h,
                             const struct BorderRadii* radii);

// A Gaussian blur of straight-alpha RGBA8 texels (blurred premultiplied, so
// colour does not bleed from transparent texels), sigma in texels. Three box
// passes per axis.
void blur_rgba(std::vector<uint8_t>* rgba, int width, int height, double sigma);

// The same blur, for a buffer whose colour is the SAME everywhere and whose
// alpha is the only thing that varies -- every box-shadow and every text-shadow,
// which are a single colour drawn at a coverage.
//
// Blurring is linear and the working buffer is premultiplied, so the three
// colour planes are just the alpha plane times a constant: running them through
// the filter reproduces that constant at four times the cost in arithmetic and,
// more to the point, in memory traffic. The result is identical wherever there
// is any coverage, which test_blur_flat_matches_full holds it to.
//
// In a texel with NO coverage the two differ, and deliberately: the four-channel
// path divides the blurred colour by a blurred alpha that has fallen to a
// rounding residue, and gets an arbitrary colour out -- 204 of 255 away from the
// real one, in the case the test pins. This keeps the real colour, which is what
// a straight-alpha texture wants at its transparent edge, since the bilinear
// filter blends those texels into their neighbours.
void blur_flat_rgba(std::vector<uint8_t>* rgba, int width, int height, double sigma);

// A large blur, done at the resolution it needs rather than the one it is
// drawn at.
//
// A Gaussian of sigma s passes nothing finer than about s, so a blur of 30
// texels rasterized and blurred texel for texel spends ~12x the work on detail
// the blur then removes -- and blurs are most of a cold open: hud's
// `filter: blur(60px)` backdrop was 22 of its 50 ms, neon's glows more than
// that. Skia shrinks large blurs the same way. The picture is rasterized on a
// coarser grid, blurred there, and interpolated back up to the texture's own
// size, so the texture a host samples -- with nearest filtering, in Godot --
// keeps every texel it had.
//
// The coarse grid is chosen so the blur there is exactly kReducedBlurRadius
// box passes: three boxes of radius r are a Gaussian of sigma sqrt(r(r+1)),
// and the grid's scale makes that the requested sigma, per axis, to within
// the rounding of the grid's size. At sigma 8.5 texels the interpolation
// error is under a tenth of a level.
inline constexpr int kReducedBlurRadius = 8;
struct ReducedBlur {
    bool reduced = false;        // false: blur at full resolution, as before
    int inner_w = 0, inner_h = 0; // the box, in coarse texels
    int pad = 0;                  // coarse texels around it
    int width() const { return inner_w + 2 * pad; }
    int height() const { return inner_h + 2 * pad; }
    double sigma() const;         // in coarse texels
};
// For a box of `inner_w` x `inner_h` texels blurred by `sigma` texels.
// Reduces only where it saves at least four times the texels.
ReducedBlur reduced_blur_grid(int inner_w, int inner_h, double sigma);
// Resamples a blurred coarse texture (`grid`, straight-alpha RGBA8) to the
// `tex_w` x `tex_h` texture whose box sits `pad` texels in, bilinearly and in
// premultiplied alpha, as a GPU's linear filter would sample it.
void upsample_blurred(const std::vector<uint8_t>& coarse, const ReducedBlur& grid, int tex_w,
                      int tex_h, int pad, std::vector<uint8_t>* out_rgba);

// The colour of `g` at (x, y) inside a `width` x `height` gradient box, as
// straight-alpha sRGB in [0, 1]. Exposed for tests.
void sample_gradient(const Gradient& g, double x, double y, double width, double height,
                     const LayoutContext& ctx, double font_size, float out_srgb[4]);

} // namespace weva
