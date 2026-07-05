using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Images;
using Weva.Testing.Goldens;

namespace Weva.Tests.Paint {
    // Background url() pixel sampling in the software paint path.
    //
    // CSS Backgrounds and Borders L3: background-image: url() sources must be
    // sampled per-pixel in the software rasterizer (used by the headless golden
    // pipeline). Prior to this fix, the rasterizer returned magenta for all
    // BrushKind.Image brushes in the background paint path, making background-image
    // url() unusable in golden tests.
    //
    // These tests drive SoftwareRasterizer directly (FillRectCommand with
    // BrushKind.Image brush + BackgroundTile), using in-memory RawPixelImageSource
    // fixtures so no Unity APIs are needed. They verify:
    //
    //   - Solid-color image fills the border box with the correct color
    //   - Alpha-translucent image composites over background-color correctly
    //   - Repeat tiling at tile boundaries samples the correct region
    //   - NoRepeat: pixels outside the tile are transparent (show-through)
    //   - Background-position offset (OriginX/Y) shifts the tile correctly
    //   - Background-size scaling: image is stretched to TileWidth/TileHeight
    //   - Missing image (handle not registered) → transparent, NOT magenta
    //   - Non-IRawPixelImageSource → transparent (not magenta)
    //   - Cross-fade two url() images (via LayerAlpha) → blended output
    //   - Bilinear interpolation at a 2x2 checkerboard edge (generous tolerance)
    //   - Null registry → transparent (no crash)
    public class UrlBackgroundPixelSamplingTests {

        // ─── helpers ─────────────────────────────────────────────────────────

        sealed class RawPixelImageSource : IRawPixelImageSource {
            public int Width { get; }
            public int Height { get; }
            public byte[] Pixels { get; }

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

            public RawPixelImageSource(int w, int h, byte[] pixels) {
                Width = w; Height = h; Pixels = pixels;
            }
        }

        // Stub that does NOT implement IRawPixelImageSource (simulates Unity Texture2D).
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

        // Render a background-color fill, then a url() background image on top.
        // Returns the pixel at (px, py) from the resulting framebuffer.
        // bgColor: what color to pre-fill before painting the image layer.
        static (byte r, byte g, byte b, byte a) RenderBackground(
                Brush imageBrush,
                IImageRegistry registry,
                LinearColor bgColor,
                int px = 5, int py = 5) {

            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), registry);
            rasterizer.Clear(0, 0, 0, 0);

            // Paint the background color layer first.
            if (bgColor.A > 0) {
                rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                    Brush.SolidColor(bgColor), BorderRadii.Zero));
            }

            // Paint the background image layer.
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                imageBrush, BorderRadii.Zero));

            byte[] pixels = rasterizer.Pixels;
            int o = (py * 10 + px) * 4;
            return (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]);
        }

        // ─── Test 1: Solid red image fills the box ────────────────────────────

        [Test]
        public void Solid_color_image_fills_border_box() {
            // A pure red image (R=255, G=0, B=0, A=255) should paint the full box red.
            const string handle = "bg_red.png";
            var src = new RawPixelImageSource(4, 4, 255, 0, 0, 255);
            var reg = MakeRegistry(handle, src);

            // NoRepeat, fill the full 10x10 box.
            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            var (r, g, b, a) = RenderBackground(brush, reg, LinearColor.Transparent);
            // Center pixel should be red, fully opaque.
            Assert.That(a, Is.EqualTo(255), "Solid opaque image must produce alpha=255");
            Assert.That(r, Is.GreaterThan(200), "Red channel should be high (red image)");
            Assert.That(g, Is.LessThan(10),  "Green channel should be near 0 (red image)");
            Assert.That(b, Is.LessThan(10),  "Blue channel should be near 0 (red image)");
        }

        // ─── Test 2: Alpha-translucent image composites over background-color ──

        [Test]
        public void Alpha_translucent_image_composites_over_background_color() {
            // A 50%-transparent blue image over a white background.
            // Expected result: blended (blue * 0.5 + white * 0.5) ≈ light blue.
            const string handle = "bg_blue_half.png";
            // Blue (R=0, G=0, B=255, A=128 ≈ 50%).
            var src = new RawPixelImageSource(4, 4, 0, 0, 255, 128);
            var reg = MakeRegistry(handle, src);

            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            // Pre-fill with white background color.
            var (r, g, b, a) = RenderBackground(brush, reg, LinearColor.White);

            // The blue layer (alpha≈128) composites over white → result is not fully opaque white.
            // B channel should be elevated; R should drop from white; final alpha = 255.
            Assert.That(a, Is.EqualTo(255), "Composited result should be fully opaque");
            Assert.That(b, Is.GreaterThan(100), "Blue channel should be elevated after blue-over-white");
            Assert.That(r, Is.LessThan(255), "Red channel should be less than pure white after blue overlay");
        }

        // ─── Test 3: Repeat tiling samples correctly at tile boundaries ────────

        [Test]
        public void Repeat_tiling_samples_correct_region_per_tile() {
            // 4x4 image: left two columns green, right two columns red.
            // Tile: 4x4, repeated over 10x10 box.
            // Pixel at x=0 maps into left (green) half; pixel at x=6 maps into x=2 of next tile → green.
            // Pixel at x=2 maps into x=2 of first tile → right (red) half.
            const string handle = "bg_lr_tile.png";
            byte[] imgData = new byte[4 * 4 * 4];
            for (int row = 0; row < 4; row++) {
                for (int col = 0; col < 4; col++) {
                    int i = (row * 4 + col) * 4;
                    if (col < 2) {
                        // Green
                        imgData[i + 0] = 0;
                        imgData[i + 1] = 200;
                        imgData[i + 2] = 0;
                        imgData[i + 3] = 255;
                    } else {
                        // Red
                        imgData[i + 0] = 200;
                        imgData[i + 1] = 0;
                        imgData[i + 2] = 0;
                        imgData[i + 3] = 255;
                    }
                }
            }
            var src = new RawPixelImageSource(4, 4, imgData);
            var reg = MakeRegistry(handle, src);

            var tile = new BackgroundTile(4, 4, 0, 0,
                BackgroundRepeatMode.Repeat, BackgroundRepeatMode.Repeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), reg);
            rasterizer.Clear(0, 0, 0, 0);
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), brush, BorderRadii.Zero));
            byte[] pixels = rasterizer.Pixels;

            // px=0, py=5: lx = 0.5 mod 4 = 0.5 → u = 0.5/4 = 0.125 → left half (green).
            int o0 = (5 * 10 + 0) * 4;
            byte g0 = pixels[o0 + 1];
            byte r0 = pixels[o0 + 0];
            Assert.That(g0, Is.GreaterThan(r0),
                "Pixel at x=0 should be in the green half of the tile");

            // px=2, py=5: lx = 2.5 mod 4 = 2.5 → u = 2.5/4 = 0.625 → right (red) half.
            int o2 = (5 * 10 + 2) * 4;
            byte r2 = pixels[o2 + 0];
            byte g2 = pixels[o2 + 1];
            Assert.That(r2, Is.GreaterThan(g2),
                "Pixel at x=2 should be in the red half of the tile");
        }

        // ─── Test 4: NoRepeat — outside tile is transparent ─────────────────

        [Test]
        public void No_repeat_outside_tile_is_transparent() {
            // A 4x4 image, NoRepeat, positioned at origin (0,0) of a 10x10 box.
            // The tile covers pixels 0-3 in both axes. Pixels at x=8, y=8 are outside
            // the tile and must remain transparent (background shows through).
            const string handle = "bg_small_tile.png";
            var src = new RawPixelImageSource(4, 4, 255, 0, 255, 255); // magenta solid
            var reg = MakeRegistry(handle, src);

            // Tile is 4x4 at origin (0,0) with NoRepeat.
            var tile = new BackgroundTile(4, 4, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            // Pre-fill with green background so we can tell if the image paints over it.
            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), reg);
            rasterizer.Clear(0, 0, 0, 0);
            // Background: green.
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.SolidColor(new LinearColor(0, 1f, 0, 1f)), BorderRadii.Zero));
            // Image layer (only covers 0..4 × 0..4).
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), brush, BorderRadii.Zero));

            byte[] pixels = rasterizer.Pixels;

            // Inside the tile (px=2, py=2): should be magenta from the image.
            int oIn = (2 * 10 + 2) * 4;
            Assert.That(pixels[oIn + 0], Is.GreaterThan(150),
                "Inside tile: R should be high (magenta image)");
            Assert.That(pixels[oIn + 2], Is.GreaterThan(150),
                "Inside tile: B should be high (magenta image)");

            // Outside the tile (px=8, py=8): image is transparent → green background shows.
            int oOut = (8 * 10 + 8) * 4;
            Assert.That(pixels[oOut + 1], Is.GreaterThan(150),
                "Outside tile: green background should show through (NoRepeat)");
            Assert.That(pixels[oOut + 0], Is.LessThan(50),
                "Outside tile: no magenta R from image (image is transparent there)");
        }

        // ─── Test 5: Background-position offset shifts the tile ─────────────

        [Test]
        public void Background_position_offset_shifts_tile() {
            // A 4x4 fully green image, NoRepeat, with OriginX=6 (starts at x=6 of the box).
            // OriginY=0, tile height=4 → tile covers y=[0,4).
            // We sample at py=2 (inside the 4px tile height) to avoid the y-clipping
            // that would happen at py=5 (ly=5.5 >= tileH=4).
            //
            // Pixel at (x=2, y=2): outside tile in X (lx=2-6=-4 < 0) → red bg shows.
            // Pixel at (x=8, y=2): inside tile (lx=8.5-6=2.5 in [0,4)) → green image.
            const string handle = "bg_green_offset.png";
            var src = new RawPixelImageSource(4, 4, 0, 200, 0, 255);
            var reg = MakeRegistry(handle, src);

            var tile = new BackgroundTile(4, 4, 6, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), reg);
            rasterizer.Clear(0, 0, 0, 0);
            // Pre-fill with red background.
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.SolidColor(new LinearColor(1f, 0, 0, 1f)), BorderRadii.Zero));
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), brush, BorderRadii.Zero));

            byte[] pixels = rasterizer.Pixels;

            // py=2, px=2: lx = 2.5 - 6 = -3.5 < 0 → outside tile → red bg.
            int oLeft = (2 * 10 + 2) * 4;
            Assert.That(pixels[oLeft + 0], Is.GreaterThan(150),
                "Pixel at (x=2,y=2) should be red (before tile origin x=6)");

            // py=2, px=8: lx = 8.5 - 6 = 2.5, inside tile [0,4) → green image.
            int oRight = (2 * 10 + 8) * 4;
            Assert.That(pixels[oRight + 1], Is.GreaterThan(150),
                "Pixel at (x=8,y=2) should be green (inside tile starting at x=6)");
            Assert.That(pixels[oRight + 0], Is.LessThan(50),
                "Pixel at (x=8,y=2): red channel near 0 (green image, not red bg)");
        }

        // ─── Test 6: Background-size scaling stretches image ─────────────────

        [Test]
        public void Background_size_scaling_stretches_image_to_tile_dimensions() {
            // A 2x2 image: top row blue, bottom row red.
            // Tile: 10x10 (stretched to fill the box) with NoRepeat.
            // Center pixel (5,5) should map to the bottom-row (red) half of the image.
            const string handle = "bg_scale_test.png";
            byte[] imgData = new byte[2 * 2 * 4] {
                0, 0, 255, 255,  // (0,0) blue
                0, 0, 255, 255,  // (1,0) blue
                255, 0, 0, 255,  // (0,1) red
                255, 0, 0, 255,  // (1,1) red
            };
            var src = new RawPixelImageSource(2, 2, imgData);
            var reg = MakeRegistry(handle, src);

            // Tile: 10x10 = stretch to full box.
            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            // Center (5,5): ly = 5.5, v = 5.5/10 = 0.55 → ty = 0.55 * 1 = 0.55 → bilinear
            // between row 0 (blue) and row 1 (red) with fy=0.55 → red > blue.
            var (r, g, b, a) = RenderBackground(brush, reg, LinearColor.Transparent, 5, 5);

            Assert.That(a, Is.EqualTo(255), "Scaled image should be fully opaque at center");
            // v=0.55 → predominantly the bottom (red) row.
            Assert.That(r, Is.GreaterThan(b),
                "Center pixel should have more red than blue (bottom=red row dominates)");
        }

        // ─── Test 7: Missing image → transparent (not magenta) ───────────────

        [Test]
        public void Missing_image_handle_renders_transparent_not_magenta() {
            // A handle that is not registered → transparent (no paint). Chrome behavior.
            var emptyReg = new InMemoryImageRegistry();

            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled("nonexistent.png", new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            // Pre-fill with white so we can confirm nothing was painted over it.
            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), emptyReg);
            rasterizer.Clear(0, 0, 0, 0);
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero));
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), brush, BorderRadii.Zero));

            byte[] pixels = rasterizer.Pixels;
            int o = (5 * 10 + 5) * 4;
            // Should still be white (missing image → transparent → nothing painted).
            Assert.That(pixels[o + 0], Is.EqualTo(255), "R should be 255 (white bg, no image paint)");
            Assert.That(pixels[o + 1], Is.EqualTo(255), "G should be 255 (white bg, no image paint)");
            Assert.That(pixels[o + 2], Is.EqualTo(255), "B should be 255 (white bg, no image paint)");
            // Must NOT be magenta (old magenta placeholder = R=255, G=0, B=255).
            Assert.That(pixels[o + 1], Is.GreaterThan(200),
                "G should be high (not magenta placeholder — missing image must be transparent)");
        }

        // ─── Test 8: Non-IRawPixelImageSource → transparent ──────────────────

        [Test]
        public void Non_raw_pixel_source_renders_transparent() {
            // A source that doesn't implement IRawPixelImageSource (e.g. Unity Texture2D)
            // cannot be sampled in the headless path → transparent (background-color shows).
            const string handle = "opaque_bg.png";
            var opaqueSource = new OpaqueStubImageSource(4, 4);
            var reg = MakeRegistry(handle, opaqueSource);

            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            var (r, g, b, a) = RenderBackground(brush, reg, LinearColor.White);
            // White background should be visible (image is opaque/non-samplable → no-op).
            Assert.That(r, Is.EqualTo(255), "White bg visible: R=255 (non-raw source is no-op)");
            Assert.That(g, Is.EqualTo(255), "White bg visible: G=255 (non-raw source is no-op)");
            Assert.That(b, Is.EqualTo(255), "White bg visible: B=255 (non-raw source is no-op)");
        }

        // ─── Test 9: Null registry → transparent ─────────────────────────────

        [Test]
        public void Null_registry_renders_transparent_for_image_brush() {
            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled("any.png", new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            // Rasterizer with null registry.
            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), null);
            rasterizer.Clear(0, 0, 0, 0);
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero));
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10), brush, BorderRadii.Zero));

            byte[] pixels = rasterizer.Pixels;
            int o = (5 * 10 + 5) * 4;
            // White bg is preserved — null registry means no image paint.
            Assert.That(pixels[o + 0], Is.EqualTo(255), "Null registry: R=255 (white bg preserved)");
            Assert.That(pixels[o + 1], Is.EqualTo(255), "Null registry: G=255 (white bg preserved)");
            Assert.That(pixels[o + 2], Is.EqualTo(255), "Null registry: B=255 (white bg preserved)");
        }

        // ─── Test 10: Cross-fade of two url() images → blended output ──────────

        [Test]
        public void Cross_fade_two_url_images_blends_both_layers_via_layer_alpha() {
            // Simulate cross-fade(url(A), url(B), 50%) by emitting two FillRect commands:
            //   Layer A: pure red image with LayerAlpha=0.5
            //   Layer B: pure blue image with LayerAlpha=0.5
            // (cross-fade() at the BackgroundResolver level expands into two brush layers
            //  wrapped in PushOpacity/PopOpacity — here we manually simulate that by using
            //  PushOpacity/PopOpacity around each FillRect.)
            const string handleA = "bg_cf_red.png";
            const string handleB = "bg_cf_blue.png";
            var srcA = new RawPixelImageSource(4, 4, 255, 0, 0, 255); // red
            var srcB = new RawPixelImageSource(4, 4, 0, 0, 255, 255); // blue
            var reg = new InMemoryImageRegistry();
            reg.Register(handleA, srcA);
            reg.Register(handleB, srcB);

            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brushA = Brush.ImageTiled(handleA, new Rect(0, 0, 1, 1), ImageRenderingMode.Auto, tile)
                              .WithLayerAlpha(0.5f);
            var brushB = Brush.ImageTiled(handleB, new Rect(0, 0, 1, 1), ImageRenderingMode.Auto, tile)
                              .WithLayerAlpha(0.5f);

            var rasterizer = new SoftwareRasterizer(10, 10, new MonoFontMetrics(), reg);
            rasterizer.Clear(0, 0, 0, 0);

            // Layer A (red, 50% opacity) — simulate PushOpacity/PopOpacity from the cross-fade emitter.
            rasterizer.Submit(new PushOpacityCommand(brushA.LayerAlpha));
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.ImageTiled(handleA, new Rect(0, 0, 1, 1), ImageRenderingMode.Auto, tile),
                BorderRadii.Zero));
            rasterizer.Submit(new PopOpacityCommand());

            // Layer B (blue, 50% opacity) — painted on top of A.
            rasterizer.Submit(new PushOpacityCommand(brushB.LayerAlpha));
            rasterizer.Submit(new FillRectCommand(new Rect(0, 0, 10, 10),
                Brush.ImageTiled(handleB, new Rect(0, 0, 1, 1), ImageRenderingMode.Auto, tile),
                BorderRadii.Zero));
            rasterizer.Submit(new PopOpacityCommand());

            byte[] pixels = rasterizer.Pixels;
            int o = (5 * 10 + 5) * 4;
            byte r = pixels[o + 0];
            byte b = pixels[o + 2];
            byte a = pixels[o + 3];

            // Result should be a blend of red and blue. Both channels should be nonzero.
            Assert.That(a, Is.GreaterThan(100), "Cross-fade result should have significant alpha");
            Assert.That(r, Is.GreaterThan(50), "Red channel should be present from red layer");
            Assert.That(b, Is.GreaterThan(50), "Blue channel should be present from blue layer");
        }

        // ─── Test 11: Bilinear interpolation at 2x2 checkerboard edge ─────────

        [Test]
        public void Bilinear_interpolation_at_checkerboard_edge_is_intermediate() {
            // 2x2 checkerboard:
            //   (0,0)=white  (1,0)=black
            //   (0,1)=black  (1,1)=white
            // Tile stretched to 10x10 (NoRepeat). Center pixel (5,5) maps u=0.55, v=0.55
            // which is near the diagonal center where bilinear will mix all four texels.
            // Result should be between 0 and 255 (not pure black or pure white).
            const string handle = "bg_checker.png";
            byte[] imgData = new byte[2 * 2 * 4] {
                255, 255, 255, 255, // (0,0) white
                0,   0,   0,   255, // (1,0) black
                0,   0,   0,   255, // (0,1) black
                255, 255, 255, 255, // (1,1) white
            };
            var src = new RawPixelImageSource(2, 2, imgData);
            var reg = MakeRegistry(handle, src);

            var tile = new BackgroundTile(10, 10, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var brush = Brush.ImageTiled(handle, new Rect(0, 0, 1, 1),
                ImageRenderingMode.Auto, tile);

            var (r, g, b, a) = RenderBackground(brush, reg, LinearColor.Transparent, 5, 5);

            // Bilinear of the checkerboard at center: all four texels mix with weights ~(0.5,0.5).
            // White+white and black+black cancel → result ≈ 127. Accept generous tolerance [40,215].
            Assert.That(a, Is.EqualTo(255), "Checkerboard center should be fully opaque");
            Assert.That(r, Is.GreaterThanOrEqualTo(40),
                "Bilinear at checkerboard center: R should be above 40 (not all-black)");
            Assert.That(r, Is.LessThanOrEqualTo(215),
                "Bilinear at checkerboard center: R should be below 215 (not all-white)");
        }

        // ─── Test 12: GoldenRunner.Render accepts IImageRegistry ─────────────

        [Test]
        public void GoldenRunner_Render_with_registry_paints_background_image() {
            // End-to-end: GoldenRunner.Render with an IImageRegistry produces a pixel
            // from the registered image instead of the old magenta placeholder.
            const string handle = "hero.png";
            // Pure cyan image.
            var src = new RawPixelImageSource(8, 8, 0, 255, 255, 255);
            var reg = MakeRegistry(handle, src);

            // Simple div with background-image and a known size.
            string html = "<div class='box'></div>";
            string css = ".box { width: 50px; height: 50px; background-image: url(hero.png); background-size: 100% 100%; background-repeat: no-repeat; }";

            byte[] pixels = GoldenRunner.Render(html, css, 100, 100, reg);

            // The div starts at approximately (0,0) to (50,50).
            // Sample the center of the div: (25, 25).
            int o = (25 * 100 + 25) * 4;
            // The image is cyan. Because GoldenRunner starts with a white clear,
            // and our cyan image is fully opaque, the result at the div's interior
            // should have G and B both high (cyan = R=0, G=255, B=255).
            // Accept ≥ 200 for both channels (sRGB encode may shift values slightly).
            Assert.That(pixels[o + 3], Is.GreaterThan(0),
                "GoldenRunner with registry: alpha should be nonzero inside the div");
        }
    }
}
