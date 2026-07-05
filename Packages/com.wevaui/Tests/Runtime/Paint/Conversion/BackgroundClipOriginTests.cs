using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // CSS Backgrounds 3 §3.7 / §3.8: background-clip / background-origin.
    // The resolver returns two box-local rects:
    //   * paintRect — what FillRect actually draws into (border-clip / padding-clip / content-clip)
    //   * originRect — reference for background-position resolution
    public class BackgroundClipOriginTests {
        static BlockBox MakeBox(double w, double h, double bL, double bT, double bR, double bB,
                                double pL, double pT, double pR, double pB) {
            var bb = new BlockBox();
            bb.Width = w; bb.Height = h;
            bb.BorderLeft = bL; bb.BorderTop = bT; bb.BorderRight = bR; bb.BorderBottom = bB;
            bb.PaddingLeft = pL; bb.PaddingTop = pT; bb.PaddingRight = pR; bb.PaddingBottom = pB;
            return bb;
        }

        [Test]
        public void Default_clip_is_border_box_origin_is_padding_box() {
            var box = MakeBox(200, 100, 4, 4, 4, 4, 8, 8, 8, 8);
            var style = new ComputedStyle(new Element("div"));
            BackgroundClipOrigin.Resolve(style, box, out var paint, out var origin);
            // paint = border-box = full bounds.
            Assert.That(paint.X, Is.EqualTo(0));
            Assert.That(paint.Y, Is.EqualTo(0));
            Assert.That(paint.Width, Is.EqualTo(200));
            Assert.That(paint.Height, Is.EqualTo(100));
            // origin = padding-box = inset by border widths.
            Assert.That(origin.X, Is.EqualTo(4));
            Assert.That(origin.Y, Is.EqualTo(4));
            Assert.That(origin.Width, Is.EqualTo(192));
            Assert.That(origin.Height, Is.EqualTo(92));
        }

        [Test]
        public void Clip_padding_box_insets_paint_rect_by_borders() {
            var box = MakeBox(200, 100, 4, 4, 4, 4, 8, 8, 8, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("background-clip", "padding-box");
            BackgroundClipOrigin.Resolve(style, box, out var paint, out _);
            Assert.That(paint.X, Is.EqualTo(4));
            Assert.That(paint.Y, Is.EqualTo(4));
            Assert.That(paint.Width, Is.EqualTo(192));
            Assert.That(paint.Height, Is.EqualTo(92));
        }

        [Test]
        public void Clip_content_box_insets_paint_rect_by_border_plus_padding() {
            var box = MakeBox(200, 100, 4, 4, 4, 4, 8, 8, 8, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("background-clip", "content-box");
            BackgroundClipOrigin.Resolve(style, box, out var paint, out _);
            Assert.That(paint.X, Is.EqualTo(12));
            Assert.That(paint.Y, Is.EqualTo(12));
            Assert.That(paint.Width, Is.EqualTo(176));
            Assert.That(paint.Height, Is.EqualTo(76));
        }

        [Test]
        public void Origin_border_box_uses_full_bounds() {
            var box = MakeBox(200, 100, 4, 4, 4, 4, 8, 8, 8, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("background-origin", "border-box");
            BackgroundClipOrigin.Resolve(style, box, out _, out var origin);
            Assert.That(origin.X, Is.EqualTo(0));
            Assert.That(origin.Y, Is.EqualTo(0));
            Assert.That(origin.Width, Is.EqualTo(200));
            Assert.That(origin.Height, Is.EqualTo(100));
        }

        [Test]
        public void Origin_content_box_excludes_padding_and_border() {
            var box = MakeBox(200, 100, 4, 4, 4, 4, 8, 8, 8, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("background-origin", "content-box");
            BackgroundClipOrigin.Resolve(style, box, out _, out var origin);
            Assert.That(origin.X, Is.EqualTo(12));
            Assert.That(origin.Y, Is.EqualTo(12));
            Assert.That(origin.Width, Is.EqualTo(176));
            Assert.That(origin.Height, Is.EqualTo(76));
        }

        [Test]
        public void Mixed_clip_and_origin_independent() {
            var box = MakeBox(200, 100, 4, 4, 4, 4, 8, 8, 8, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("background-clip", "content-box");
            style.Set("background-origin", "border-box");
            BackgroundClipOrigin.Resolve(style, box, out var paint, out var origin);
            Assert.That(paint.Width, Is.EqualTo(176));
            Assert.That(origin.Width, Is.EqualTo(200));
        }

        [Test]
        public void Zero_box_dimensions_yield_zero_rects() {
            var box = MakeBox(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var style = new ComputedStyle(new Element("div"));
            BackgroundClipOrigin.Resolve(style, box, out var paint, out var origin);
            Assert.That(paint.Width, Is.EqualTo(0));
            Assert.That(origin.Width, Is.EqualTo(0));
        }

        [Test]
        public void Unknown_keyword_falls_back_to_default() {
            var box = MakeBox(200, 100, 4, 0, 0, 0, 0, 0, 0, 0);
            var style = new ComputedStyle(new Element("div"));
            style.Set("background-clip", "magic");
            BackgroundClipOrigin.Resolve(style, box, out var paint, out _);
            // Default = border-box.
            Assert.That(paint.Width, Is.EqualTo(200));
        }
    }
}
