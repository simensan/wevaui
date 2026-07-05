using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Testing.Goldens;

namespace Weva.Tests.Goldens {
    public class RasterizerPrimitiveTests {
        [Test]
        public void FillRect_emits_solid_color_at_expected_pixels() {
            var r = new SoftwareRasterizer(16, 16);
            r.Clear(0, 0, 0, 0);
            var bounds = new Rect(4, 4, 8, 8);
            var brush = Brush.SolidColor(new LinearColor(1f, 0f, 0f, 1f));
            r.Submit(new FillRectCommand(bounds, brush));

            int Pixel(int x, int y) => (y * 16 + x) * 4;
            // Outside the rect: untouched.
            Assert.That(r.Pixels[Pixel(2, 2) + 3], Is.EqualTo((byte)0), "outside should remain transparent");
            // Inside the rect: opaque red. Linear (1,0,0) -> sRGB byte 255.
            Assert.That(r.Pixels[Pixel(8, 8) + 0], Is.EqualTo((byte)255));
            Assert.That(r.Pixels[Pixel(8, 8) + 1], Is.EqualTo((byte)0));
            Assert.That(r.Pixels[Pixel(8, 8) + 2], Is.EqualTo((byte)0));
            Assert.That(r.Pixels[Pixel(8, 8) + 3], Is.EqualTo((byte)255));
        }

        [Test]
        public void Clip_stack_constrains_subsequent_draws() {
            var r = new SoftwareRasterizer(16, 16);
            r.Clear(0, 0, 0, 0);
            r.Submit(new PushClipCommand(new Rect(0, 0, 8, 8)));
            r.Submit(new FillRectCommand(new Rect(0, 0, 16, 16),
                Brush.SolidColor(new LinearColor(0f, 1f, 0f, 1f))));
            r.Submit(new PopClipCommand());

            int Pixel(int x, int y) => (y * 16 + x) * 4;
            Assert.That(r.Pixels[Pixel(4, 4) + 1], Is.EqualTo((byte)255), "clipped region should still draw");
            Assert.That(r.Pixels[Pixel(12, 12) + 3], Is.EqualTo((byte)0), "outside the clip should remain untouched");
        }

        [Test]
        public void Clip_path_circle_constrains_subsequent_draws() {
            var r = new SoftwareRasterizer(16, 16);
            r.Clear(0, 0, 0, 0);
            r.Submit(new PushClipPathCommand(new CircleClipPathShape(8, 8, 4)));
            r.Submit(new FillRectCommand(new Rect(0, 0, 16, 16),
                Brush.SolidColor(new LinearColor(0f, 1f, 0f, 1f))));
            r.Submit(new PopClipPathCommand());

            int Pixel(int x, int y) => (y * 16 + x) * 4;
            Assert.That(r.Pixels[Pixel(8, 8) + 1], Is.EqualTo((byte)255), "inside the circle should draw");
            Assert.That(r.Pixels[Pixel(2, 2) + 3], Is.EqualTo((byte)0), "outside the circle should remain untouched");
        }

        [Test]
        public void Backdrop_filter_modifies_existing_pixels_inside_bounds() {
            var r = new SoftwareRasterizer(8, 8);
            r.Clear(200, 100, 50, 255);
            r.Submit(new DrawBackdropFilterCommand(
                new Rect(0, 0, 4, 4),
                BorderRadii.Zero,
                new FilterChain(new FilterFunction[] { new BrightnessFilter(0) })));

            int Pixel(int x, int y) => (y * 8 + x) * 4;
            Assert.That(r.Pixels[Pixel(2, 2) + 0], Is.EqualTo((byte)0), "filtered region should be darkened");
            Assert.That(r.Pixels[Pixel(6, 6) + 0], Is.EqualTo((byte)200), "outside backdrop bounds should be untouched");
        }

        [Test]
        public void Gradient_mask_fades_subtree_alpha() {
            var r = new SoftwareRasterizer(16, 4);
            r.Clear(0, 0, 0, 0);
            var gradient = new LinearGradient(90, new[] {
                new GradientStop(new LinearColor(1f, 1f, 1f, 0f), 0),
                new GradientStop(new LinearColor(1f, 1f, 1f, 1f), 1),
            });
            var mask = new MaskLayer(
                new Rect(0, 0, 16, 4),
                Brush.Gradient(gradient),
                MaskMode.Alpha,
                MaskComposite.Add,
                new BackgroundTile(16, 4, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat));

            r.Submit(new PushMaskCommand(new Rect(0, 0, 16, 4), MaskDefinition.Single(mask)));
            r.Submit(new FillRectCommand(new Rect(0, 0, 16, 4),
                Brush.SolidColor(new LinearColor(1f, 0f, 0f, 1f))));
            r.Submit(new PopMaskCommand());

            int Pixel(int x, int y) => (y * 16 + x) * 4;
            byte leftAlpha = r.Pixels[Pixel(1, 2) + 3];
            byte rightAlpha = r.Pixels[Pixel(14, 2) + 3];
            Assert.That(leftAlpha, Is.LessThan((byte)64));
            Assert.That(rightAlpha, Is.GreaterThan((byte)180));
        }

        [Test]
        public void Layered_gradient_masks_intersect_alpha() {
            var r = new SoftwareRasterizer(16, 16);
            r.Clear(0, 0, 0, 0);
            var horizontal = new LinearGradient(90, new[] {
                new GradientStop(new LinearColor(1f, 1f, 1f, 0f), 0),
                new GradientStop(new LinearColor(1f, 1f, 1f, 1f), 1),
            });
            var vertical = new LinearGradient(180, new[] {
                new GradientStop(new LinearColor(1f, 1f, 1f, 0f), 0),
                new GradientStop(new LinearColor(1f, 1f, 1f, 1f), 1),
            });
            var layers = new[] {
                new MaskLayer(
                    new Rect(0, 0, 16, 16),
                    Brush.Gradient(horizontal),
                    MaskMode.Alpha,
                    MaskComposite.Intersect,
                    new BackgroundTile(16, 16, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat)),
                new MaskLayer(
                    new Rect(0, 0, 16, 16),
                    Brush.Gradient(vertical),
                    MaskMode.Alpha,
                    MaskComposite.Add,
                    new BackgroundTile(16, 16, 0, 0, BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat)),
            };

            r.Submit(new PushMaskCommand(new Rect(0, 0, 16, 16), new MaskDefinition(layers)));
            r.Submit(new FillRectCommand(new Rect(0, 0, 16, 16),
                Brush.SolidColor(new LinearColor(1f, 0f, 0f, 1f))));
            r.Submit(new PopMaskCommand());

            int Pixel(int x, int y) => (y * 16 + x) * 4;
            byte topRight = r.Pixels[Pixel(14, 1) + 3];
            byte bottomLeft = r.Pixels[Pixel(1, 14) + 3];
            byte bottomRight = r.Pixels[Pixel(14, 14) + 3];
            Assert.That(topRight, Is.LessThan((byte)80));
            Assert.That(bottomLeft, Is.LessThan((byte)80));
            Assert.That(bottomRight, Is.GreaterThan((byte)160));
        }

        [Test]
        public void Opacity_stack_composes_multiplicatively() {
            var r = new SoftwareRasterizer(8, 8);
            r.Clear(0, 0, 0, 255);
            r.Submit(new PushOpacityCommand(0.5));
            r.Submit(new PushOpacityCommand(0.5));
            r.Submit(new FillRectCommand(new Rect(0, 0, 8, 8),
                Brush.SolidColor(new LinearColor(1f, 1f, 1f, 1f))));
            r.Submit(new PopOpacityCommand());
            r.Submit(new PopOpacityCommand());

            int o = (4 * 8 + 4) * 4;
            // Two stacked 0.5 opacities -> combined alpha 0.25 over black -> ~64.
            byte red = r.Pixels[o + 0];
            Assert.That(red, Is.GreaterThanOrEqualTo((byte)55).And.LessThanOrEqualTo((byte)75),
                "stacked 0.5*0.5 opacity over black should land near 64; got " + red);
        }

        [Test]
        public void PNG_round_trips_RGBA_buffer() {
            int w = 12, h = 7;
            byte[] rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int i = (y * w + x) * 4;
                    rgba[i + 0] = (byte)(x * 17);
                    rgba[i + 1] = (byte)(y * 31);
                    rgba[i + 2] = (byte)((x + y) * 13);
                    rgba[i + 3] = 255;
                }
            }
            byte[] png = PngWriter.Encode(rgba, w, h);
            var decoded = PngReader.Decode(png);
            Assert.That(decoded.Width, Is.EqualTo(w));
            Assert.That(decoded.Height, Is.EqualTo(h));
            Assert.That(decoded.Rgba, Is.EqualTo(rgba));
        }
    }
}
