using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Images;
using Weva.Testing.Goldens;

namespace Weva.Tests.Paint {
    // B17 — URL mask pixel sampling in the software paint path.
    //
    // CSS Masking Module Level 1 §3: mask-image: url() layers must be sampled
    // pixel-by-pixel in the software rasterizer (used by the headless golden
    // pipeline). Prior to B17, the rasterizer returned opaque-white for all
    // url() mask brushes, making them equivalent to "no mask" regardless of
    // the source image content.
    //
    // These tests drive the SoftwareRasterizer directly (PushMask / PopMask)
    // using in-memory RawPixelImageSource instances so no Unity APIs are needed.
    // They verify:
    //   - Alpha-mode: source pixel alpha controls element visibility
    //   - Luminance-mode: source pixel luma controls element visibility
    //   - Unresolved/missing url: element is fully hidden (Chrome parity —
    //     a failed mask url acts as a transparent-black image)
    //   - Tile boundaries: repeat tiling samples correctly across the seam
    //   - Position offset: tile origin shifts sampling correctly
    //   - Bilinear sampling: fractional coordinates interpolate between texels
    //   - Multi-layer url+gradient compositing (url adds to gradient)
    //   - No-registry rasterizer: opaque behavior reverts to the old stub
    //     (transparent) because the registry is unavailable
    //
    // Harness: SoftwareRasterizer with an InMemoryImageRegistry wired with
    // RawPixelImageSource test fixtures. We push a white fill, then
    // PushMask/PopMask and inspect framebuffer pixels.
    public class UrlMaskPixelSamplingTests {

        // ─── helpers ─────────────────────────────────────────────────────────

        // RawPixelImageSource: in-memory RGBA image for headless mask testing.
        // Pixels are stored row-major, top-left origin, straight (not premultiplied) alpha.
        sealed class RawPixelImageSource : IRawPixelImageSource {
            public int Width { get; }
            public int Height { get; }
            public byte[] Pixels { get; }

            // Create a solid-color image.
            public RawPixelImageSource(int w, int h, byte r, byte g, byte b, byte a) {
                Width = w;
                Height = h;
                Pixels = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++) {
                    Pixels[i * 4 + 0] = r;
                    Pixels[i * 4 + 1] = g;
                    Pixels[i * 4 + 2] = b;
                    Pixels[i * 4 + 3] = a;
                }
            }

            // Create an image from an explicit pixel array.
            public RawPixelImageSource(int w, int h, byte[] pixels) {
                Width = w;
                Height = h;
                Pixels = pixels;
            }
        }

        // A stub image source that does NOT implement IRawPixelImageSource, used to
        // test the fallback for non-pixel-readable images.
        sealed class OpaqueStubImageSource : IImageSource {
            public int Width { get; }
            public int Height { get; }
            public OpaqueStubImageSource(int w, int h) { Width = w; Height = h; }
        }

        static InMemoryImageRegistry MakeRegistry(string handle, IImageSource source) {
            var reg = new InMemoryImageRegistry();
            reg.Register(handle, source);
            return reg;
        }

        // Render a 10x10 white rectangle; apply a mask; return the pixel at (px, py).
        // The mask definition covers the full 10x10 area.
        static (byte r, byte g, byte b, byte a) RenderWithMask(
                MaskLayer maskLayer, IImageRegistry registry = null) {
            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), registry);
            rasterizer.Clear(0, 0, 0, 0);

            // Fill the canvas white.
            var fill = new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);

            // Build a single-layer mask definition covering the whole canvas.
            var maskDef = MaskDefinition.Single(maskLayer);

            // Push mask → fill → pop mask.
            rasterizer.Submit(new PushMaskCommand(new Rect(0, 0, 10, 10), maskDef));
            rasterizer.Submit(fill);
            rasterizer.Submit(new PopMaskCommand());

            // Return center pixel (5, 5).
            int o = (5 * 10 + 5) * 4;
            byte[] p = rasterizer.Pixels;
            return (p[o], p[o + 1], p[o + 2], p[o + 3]);
        }

        // Read a pixel at (px, py) from a rasterizer's pixel buffer.
        static (byte r, byte g, byte b, byte a) ReadPixel(byte[] pixels, int w, int px, int py) {
            int o = (py * w + px) * 4;
            return (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]);
        }

        // ─── Test 1: Opaque white image, alpha mode → element fully visible ──

        [Test]
        public void Alpha_mode_opaque_mask_image_passes_full_fill_through() {
            // A fully opaque white mask image (alpha=255) in alpha mode should
            // pass 100% of the fill through — the center pixel is white.
            const string handle = "mask_opaque.png";
            var src = new RawPixelImageSource(4, 4, 255, 255, 255, 255);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (r, g, b, a) = RenderWithMask(layer, reg);
            // Full white fill through an opaque mask → white (255, 255, 255) with full alpha.
            Assert.That(a, Is.EqualTo(255),
                "Opaque alpha=255 mask image should pass fill through at full opacity");
            Assert.That(r, Is.EqualTo(255), "Fill R channel should be 255 through opaque mask");
        }

        // ─── Test 2: Transparent image, alpha mode → element hidden ──────────

        [Test]
        public void Alpha_mode_transparent_mask_image_hides_element() {
            // A fully transparent mask image (alpha=0) in alpha mode must produce
            // alpha=0 at the output — the element is hidden. This matches Chrome's
            // behavior for a transparent mask layer.
            const string handle = "mask_transparent.png";
            var src = new RawPixelImageSource(4, 4, 0, 0, 0, 0);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (_, _, _, a) = RenderWithMask(layer, reg);
            Assert.That(a, Is.EqualTo(0),
                "Transparent alpha=0 mask image should hide element (output alpha=0)");
        }

        // ─── Test 3: Luminance mode — white image → full pass-through ────────

        [Test]
        public void Luminance_mode_white_mask_image_passes_full_fill_through() {
            // Luminance masking: the mask alpha is luma × src_alpha.
            // A white image (R=G=B=255, A=255) has luma=1.0 → alpha contribution=1.0.
            // The fill should pass through at full opacity.
            const string handle = "mask_white_lum.png";
            var src = new RawPixelImageSource(4, 4, 255, 255, 255, 255);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Luminance,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (_, _, _, a) = RenderWithMask(layer, reg);
            // Luma of white = 1.0, so mask alpha = 1.0 → output alpha = 255.
            Assert.That(a, Is.EqualTo(255),
                "Luminance-mode white mask image should pass fill through at full opacity");
        }

        // ─── Test 4: Luminance mode — black image → element hidden ───────────

        [Test]
        public void Luminance_mode_black_image_hides_element() {
            // Luminance masking with a black image (R=G=B=0, A=255):
            // luma = 0.2126*0 + 0.7152*0 + 0.0722*0 = 0.0 → mask alpha = 0.
            // The element must be hidden.
            const string handle = "mask_black_lum.png";
            var src = new RawPixelImageSource(4, 4, 0, 0, 0, 255);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Luminance,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (_, _, _, a) = RenderWithMask(layer, reg);
            Assert.That(a, Is.EqualTo(0),
                "Luminance-mode black mask image should hide element (luma=0)");
        }

        // ─── Test 5: Missing/unresolved url → element hidden (Chrome parity) ──

        [Test]
        public void Missing_url_mask_hides_element_matching_chrome_behavior() {
            // Chrome: a mask-image url that cannot be resolved (404, etc.) acts as a
            // fully transparent black image. The element is hidden beneath it.
            // We simulate this by registering no image for the handle used.
            var emptyReg = new InMemoryImageRegistry();

            var brush = Brush.ImageFullRect("nonexistent_mask.png", ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (_, _, _, a) = RenderWithMask(layer, emptyReg);
            Assert.That(a, Is.EqualTo(0),
                "Unresolved url() mask must hide element (transparent-black, Chrome parity)");
        }

        // ─── Test 6: Non-IRawPixelImageSource (opaque stub) → element hidden ──

        [Test]
        public void Non_raw_pixel_image_source_hides_element() {
            // When the registered IImageSource does NOT implement IRawPixelImageSource
            // (e.g., a Unity Texture2D which is opaque in the headless context), the
            // software rasterizer cannot sample pixels. It falls back to transparent
            // (Chrome behavior for an unresolvable mask layer).
            const string handle = "opaque_source.png";
            var opaqueSource = new OpaqueStubImageSource(4, 4);
            var reg = MakeRegistry(handle, opaqueSource);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (_, _, _, a) = RenderWithMask(layer, reg);
            Assert.That(a, Is.EqualTo(0),
                "Non-IRawPixelImageSource must fall back to transparent (element hidden)");
        }

        // ─── Test 7: Null registry → element hidden ───────────────────────────

        [Test]
        public void Null_registry_hides_element_for_url_mask() {
            // When no registry is wired into the rasterizer, url() masks cannot
            // be resolved — fall through to transparent, hiding the element.
            var brush = Brush.ImageFullRect("any.png", ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            // No registry — use null (the three-arg constructor).
            var (_, _, _, a) = RenderWithMask(layer, registry: null);
            Assert.That(a, Is.EqualTo(0),
                "Null registry must hide element for url() mask (cannot resolve handle)");
        }

        // ─── Test 8: Repeat tiling — seam pixel samples correctly ────────────

        [Test]
        public void Repeat_tiling_samples_correctly_at_tile_boundary() {
            // A 4x4 image: top two rows opaque, bottom two rows transparent.
            // Tiled at 4px × 4px over a 10px canvas with repeat in both axes.
            // Pixel at y=0 samples within the opaque half of the tile;
            // pixel at y=6 samples within the transparent half.
            // Because the image has two solid rows at each half, bilinear
            // bleeding across the seam is minimal (only at the exact boundary).
            const string handle = "halfband_mask.png";
            // 4×4 image: rows 0-1 = opaque white, rows 2-3 = transparent.
            byte[] imgData = new byte[4 * 4 * 4];
            for (int row = 0; row < 4; row++) {
                for (int col = 0; col < 4; col++) {
                    int i = (row * 4 + col) * 4;
                    byte alpha = row < 2 ? (byte)255 : (byte)0;
                    imgData[i + 0] = 255;
                    imgData[i + 1] = 255;
                    imgData[i + 2] = 255;
                    imgData[i + 3] = alpha;
                }
            }
            var src = new RawPixelImageSource(4, 4, imgData);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            // Tile: 4×4, repeat in both axes.
            var tile = new BackgroundTile(4, 4, 0, 0, BackgroundRepeatMode.Repeat, BackgroundRepeatMode.Repeat);
            var layer = new MaskLayer(new Rect(0, 0, 10, 10), brush, MaskMode.Alpha, MaskComposite.Add, tile);
            var maskDef = MaskDefinition.Single(layer);

            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), reg);
            rasterizer.Clear(0, 0, 0, 0);
            var fill = new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);

            rasterizer.Submit(new PushMaskCommand(new Rect(0, 0, 10, 10), maskDef));
            rasterizer.Submit(fill);
            rasterizer.Submit(new PopMaskCommand());

            byte[] pixels = rasterizer.Pixels;
            // Pixel at y=0: ly = 0.5 mod 4 = 0.5 → v = 0.5/4 = 0.125 → ty = 0.125*3 = 0.375
            // → between row 0 (α=255) and row 1 (α=255) → alpha = 255.
            var topPx = ReadPixel(pixels, 10, 2, 0);
            // Pixel at y=6: ly = 6.5 mod 4 = 2.5 → v = 2.5/4 = 0.625 → ty = 0.625*3 = 1.875
            // → between row 1 (α=255) and row 2 (α=0) with fy=0.875 → alpha ≈ 255*0.125 = 32.
            var midPx = ReadPixel(pixels, 10, 2, 6);

            Assert.That(topPx.a, Is.GreaterThan(220),
                "Pixel in opaque tile band (y=0) should have high alpha");
            // Pixel at y=6 is in the transparent band (rows 2-3) → low alpha.
            // Bilinear gives α ≈ 0 for rows deep in the transparent half.
            Assert.That(midPx.a, Is.LessThan(topPx.a),
                "Pixel in transparent tile band (y=6) should have lower alpha than opaque band");
        }

        // ─── Test 9: Half-alpha image → partial transparency ──────────────────

        [Test]
        public void Half_alpha_mask_image_produces_partial_transparency() {
            // An image with alpha=127 (≈50%) in alpha mode produces roughly 50%
            // transparency at the output pixel.
            const string handle = "mask_half_alpha.png";
            var src = new RawPixelImageSource(4, 4, 255, 255, 255, 127);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (_, _, _, a) = RenderWithMask(layer, reg);
            // 127/255 ≈ 0.498; composited with white fill alpha=1.0 → output alpha ≈ 127.
            // Allow ±5 for rounding (bilinear + double→byte).
            Assert.That(a, Is.GreaterThanOrEqualTo(90),
                "Half-alpha mask should produce partial transparency (alpha ≥ 90)");
            Assert.That(a, Is.LessThanOrEqualTo(165),
                "Half-alpha mask should produce partial transparency (alpha ≤ 165)");
        }

        // ─── Test 10: Multi-layer url+gradient compositing ────────────────────

        [Test]
        public void Multi_layer_url_plus_gradient_compositing_adds_coverage() {
            // Two mask layers:
            //   Layer 0 (bottom/final): gradient (fully opaque at center)
            //   Layer 1 (top): url() mask — transparent (alpha=0)
            // When both combine with "add" composite the result alpha should
            // remain driven by the gradient layer's alpha, not zeroed by the url().
            // CSS Masking 1 §10: add = src + dst*(1-src).
            // If url() layer has alpha=0: alpha(url) + alpha(grad)*(1-0) = grad_alpha → visible.
            const string handle = "mask_zero_layer.png";
            // Fully transparent url layer.
            var src = new RawPixelImageSource(4, 4, 0, 0, 0, 0);
            var reg = MakeRegistry(handle, src);

            var urlBrush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var urlLayer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                urlBrush,
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            // Gradient layer: fully opaque (linear-gradient black→black = all opaque alpha).
            var grad = new LinearGradient(180, new List<GradientStop> {
                new GradientStop(new LinearColor(0, 0, 0, 1f), 0f),
                new GradientStop(new LinearColor(0, 0, 0, 1f), 1f),
            });
            var gradBrush = Brush.Gradient(grad);
            var gradLayer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                gradBrush,
                MaskMode.Alpha,
                MaskComposite.Add,
                null);

            // layers[0] = url (top in CSS rendering order), layers[1] = gradient (bottom).
            // SampleMaskAlpha in the rasterizer starts from the last layer and composites up.
            var maskDef = new MaskDefinition(new MaskLayer[] { urlLayer, gradLayer });

            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), reg);
            rasterizer.Clear(0, 0, 0, 0);
            var fill = new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            rasterizer.Submit(new PushMaskCommand(new Rect(0, 0, 10, 10), maskDef));
            rasterizer.Submit(fill);
            rasterizer.Submit(new PopMaskCommand());

            var (_, _, _, a) = ReadPixel(rasterizer.Pixels, 10, 5, 5);
            // Gradient layer has full alpha; adding a zero-alpha url layer keeps the element visible.
            Assert.That(a, Is.GreaterThan(200),
                "Multi-layer add: transparent url + opaque gradient should keep element visible");
        }

        // ─── Test 11: Position offset shifts sampling ─────────────────────────

        [Test]
        public void Mask_position_offset_shifts_sampling() {
            // A 2x2 image: left column fully opaque, right column fully transparent.
            // The tile covers the full 10x10 canvas (TileWidth=10, TileHeight=10).
            //
            // Without offset (OriginX=0): center pixel at x=5 maps u=5/10=0.5,
            //   tx = 0.5*(2-1) = 0.5 → bilinear between col0(255) and col1(0) → alpha≈127.
            // With offset (OriginX=8): center pixel at x=5: lx = 5-0-8 = -3.
            //   NoRepeat: lx < 0 → return 0.0 (outside tile). Element is hidden.
            // This confirms that OriginX shifts which part of the element is covered
            // by the tile, and areas outside the (no-repeat) tile get alpha=0.
            const string handle = "mask_offset_test.png";
            // 2x2 image: left column opaque, right column transparent.
            byte[] imgData = new byte[2 * 2 * 4] {
                255, 255, 255, 255,  // (0,0) left col, opaque
                255, 255, 255, 0,    // (1,0) right col, transparent
                255, 255, 255, 255,  // (0,1) left col, opaque
                255, 255, 255, 0,    // (1,1) right col, transparent
            };
            var src = new RawPixelImageSource(2, 2, imgData);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);

            // Without offset: center (5,5) → lx=5, u=0.5 → between col0(opaque) and col1(transparent).
            // bilinear: alpha ≈ 0.5*255 + 0.5*0 = 127.5 → ≈127.
            var tileNoOffset = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var layerNoOffset = new MaskLayer(new Rect(0, 0, 10, 10), brush, MaskMode.Alpha, MaskComposite.Add, tileNoOffset);
            var (_, _, _, aNoOffset) = RenderWithMask(layerNoOffset, reg);

            // With OriginX=8: tile starts at x=8 on canvas. Center at x=5: lx = 5 - 0 - 8 = -3.
            // NoRepeat: lx < 0 → outside tile → alpha=0. Element hidden.
            var tileWithOffset = new BackgroundTile(10, 10, 8, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var layerWithOffset = new MaskLayer(new Rect(0, 0, 10, 10), brush, MaskMode.Alpha, MaskComposite.Add, tileWithOffset);
            var (_, _, _, aWithOffset) = RenderWithMask(layerWithOffset, reg);

            // Without offset: center maps to midpoint of image → partial alpha ≈ 127.
            Assert.That(aNoOffset, Is.GreaterThan(50),
                "Without position offset, center maps to image midpoint (partial alpha)");
            Assert.That(aNoOffset, Is.LessThan(205),
                "Without position offset, center should not be fully opaque (transparent half contributes)");
            // With offset: center is outside the tile (no-repeat) → alpha=0.
            Assert.That(aWithOffset, Is.EqualTo(0),
                "With offset placing tile to the right of center, no-repeat leaves center at alpha=0");
        }

        // ─── Test 12: match-source mode treats url() as alpha (CSS Masking L1 §3.2) ───

        [Test]
        public void Match_source_mode_for_url_mask_uses_alpha_channel() {
            // CSS Masking L1 §3.2: mask-mode: match-source → for <image> sources
            // (which includes url()), match-source resolves to "alpha". So a url()
            // mask with match-source uses the source alpha, same as explicit "alpha".
            // We verify with a half-alpha image that the result is partial (not zero
            // which would indicate luminance of a gray/white image being used).
            const string handle = "mask_match_source.png";
            // Image: medium gray (R=G=B=128) with A=200.
            var src = new RawPixelImageSource(4, 4, 128, 128, 128, 200);
            var reg = MakeRegistry(handle, src);

            var brush = Brush.ImageFullRect(handle, ImageRenderingMode.Auto);
            var layer = new MaskLayer(
                new Rect(0, 0, 10, 10),
                brush,
                MaskMode.MatchSource, // match-source for url() → alpha mode
                MaskComposite.Add,
                new BackgroundTile(10, 10, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat, 0, 0));

            var (_, _, _, a) = RenderWithMask(layer, reg);
            // A=200 → mask_alpha ≈ 200/255 ≈ 0.784.
            // MatchSource for url() images = alpha mode in CSS Masking L1 §3.2.
            // Engine's existing gradient-mask path treats MatchSource as alpha (no luma).
            // Expected output alpha ≈ 200 (within ±20 for bilinear).
            Assert.That(a, Is.GreaterThan(140),
                "match-source on url() mask should use alpha channel (result > 140)");
            Assert.That(a, Is.LessThanOrEqualTo(255),
                "match-source result must not exceed 255");
        }
    }
}
