using NUnit.Framework;
using UnityEngine;
using Weva.EditorTools.Preview;
using Weva.Paint;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.EditorTests.Preview {
    public class SoftwarePainterTests {
        [Test]
        public void Constructor_initializes_pixels_to_default() {
            var p = new SoftwarePainter(4, 3);
            Assert.That(p.Width, Is.EqualTo(4));
            Assert.That(p.Height, Is.EqualTo(3));
            Assert.That(p.Pixels.Length, Is.EqualTo(12));
            foreach (var px in p.Pixels) {
                Assert.That(px.r, Is.EqualTo(0));
                Assert.That(px.g, Is.EqualTo(0));
                Assert.That(px.b, Is.EqualTo(0));
                Assert.That(px.a, Is.EqualTo(0));
            }
        }

        [Test]
        public void Clear_fills_every_pixel() {
            var p = new SoftwarePainter(2, 2);
            p.Clear(new Color32(10, 20, 30, 255));
            foreach (var px in p.Pixels) {
                Assert.That(px.r, Is.EqualTo(10));
                Assert.That(px.g, Is.EqualTo(20));
                Assert.That(px.b, Is.EqualTo(30));
                Assert.That(px.a, Is.EqualTo(255));
            }
        }

        [Test]
        public void FillRect_paints_expected_pixels_and_leaves_others_alone() {
            var p = new SoftwarePainter(4, 4);
            p.Clear(new Color32(0, 0, 0, 255));
            p.FillRect(new SoftwarePainter.RectI(1, 1, 2, 2), new Color32(255, 0, 0, 255));
            for (int y = 0; y < 4; y++) {
                for (int x = 0; x < 4; x++) {
                    var px = p.Pixels[y * 4 + x];
                    bool inside = x >= 1 && x <= 2 && y >= 1 && y <= 2;
                    if (inside) {
                        Assert.That(px.r, Is.EqualTo(255), "x=" + x + " y=" + y);
                    } else {
                        Assert.That(px.r, Is.EqualTo(0), "x=" + x + " y=" + y);
                    }
                }
            }
        }

        [Test]
        public void FillRect_respects_canvas_bounds() {
            var p = new SoftwarePainter(3, 3);
            p.Clear(new Color32(0, 0, 0, 255));
            p.FillRect(new SoftwarePainter.RectI(-2, -2, 10, 10), new Color32(0, 255, 0, 255));
            for (int i = 0; i < p.Pixels.Length; i++) {
                Assert.That(p.Pixels[i].g, Is.EqualTo(255));
            }
        }

        [Test]
        public void FillRect_alpha_blends_over_existing_pixels() {
            var p = new SoftwarePainter(1, 1);
            p.Clear(new Color32(0, 0, 0, 255));
            p.FillRect(new SoftwarePainter.RectI(0, 0, 1, 1), new Color32(255, 255, 255, 128));
            var px = p.Pixels[0];
            Assert.That(px.r, Is.GreaterThan(100));
            Assert.That(px.r, Is.LessThan(160));
            Assert.That(px.a, Is.EqualTo(255));
        }

        [Test]
        public void StrokeRect_paints_only_the_border() {
            var p = new SoftwarePainter(5, 5);
            p.Clear(new Color32(0, 0, 0, 255));
            p.StrokeRect(new SoftwarePainter.RectI(0, 0, 5, 5), 1, new Color32(0, 0, 255, 255));
            // Center pixel must be untouched
            Assert.That(p.Pixels[2 * 5 + 2].b, Is.EqualTo(0));
            // Each corner pixel is on the border
            Assert.That(p.Pixels[0 * 5 + 0].b, Is.EqualTo(255));
            Assert.That(p.Pixels[0 * 5 + 4].b, Is.EqualTo(255));
            Assert.That(p.Pixels[4 * 5 + 0].b, Is.EqualTo(255));
            Assert.That(p.Pixels[4 * 5 + 4].b, Is.EqualTo(255));
        }

        [Test]
        public void PushClip_restricts_subsequent_fills() {
            var p = new SoftwarePainter(4, 4);
            p.Clear(new Color32(0, 0, 0, 255));
            ((IRenderBackend)p).Submit(new PushClipCommand(new Rect(1, 1, 2, 2), BorderRadii.Zero));
            p.FillRect(new SoftwarePainter.RectI(0, 0, 4, 4), new Color32(255, 0, 0, 255));
            ((IRenderBackend)p).Submit(new PopClipCommand());
            // Only pixels (1,1)..(2,2) inside the clip should be red
            for (int y = 0; y < 4; y++) {
                for (int x = 0; x < 4; x++) {
                    var px = p.Pixels[y * 4 + x];
                    bool inside = x >= 1 && x <= 2 && y >= 1 && y <= 2;
                    if (inside) Assert.That(px.r, Is.EqualTo(255));
                    else Assert.That(px.r, Is.EqualTo(0));
                }
            }
        }

        [Test]
        public void Submit_FillRectCommand_renders_solid_brush() {
            var p = new SoftwarePainter(3, 3);
            p.Clear(new Color32(0, 0, 0, 255));
            var brush = Brush.SolidColor(new LinearColor(1f, 0f, 0f, 1f));
            ((IRenderBackend)p).Submit(new FillRectCommand(new Rect(0, 0, 3, 3), brush, BorderRadii.Zero));
            var px = p.Pixels[4];
            Assert.That(px.r, Is.GreaterThan(200));
            Assert.That(px.g, Is.LessThan(50));
            Assert.That(px.b, Is.LessThan(50));
            Assert.That(px.a, Is.EqualTo(255));
        }

        [Test]
        public void PushOpacity_attenuates_subsequent_fills() {
            var p = new SoftwarePainter(1, 1);
            p.Clear(new Color32(0, 0, 0, 255));
            ((IRenderBackend)p).Submit(new PushOpacityCommand(0.5f));
            p.FillRect(new SoftwarePainter.RectI(0, 0, 1, 1), new Color32(255, 255, 255, 255));
            ((IRenderBackend)p).Submit(new PopOpacityCommand());
            var px = p.Pixels[0];
            Assert.That(px.r, Is.GreaterThan(100));
            Assert.That(px.r, Is.LessThan(160));
        }
    }
}
