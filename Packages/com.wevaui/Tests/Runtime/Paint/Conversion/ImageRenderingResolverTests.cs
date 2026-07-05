using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // CSS `image-rendering` resolution. Game UI use case: pixel-art games
    // need point sampling on scaled icons/sprites. The resolver collapses
    // CSS spec values into a small enum that backends consume.
    public class ImageRenderingResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("img"));

        [Test]
        public void Default_is_auto() {
            Assert.That(ImageRenderingResolver.Resolve(Style()), Is.EqualTo(ImageRenderingMode.Auto));
        }

        [Test]
        public void Pixelated_keyword_resolves_to_pixelated() {
            var s = Style();
            s.Set("image-rendering", "pixelated");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.Pixelated));
        }

        [Test]
        public void Crisp_edges_keyword_resolves_to_crisp_edges() {
            var s = Style();
            s.Set("image-rendering", "crisp-edges");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.CrispEdges));
        }

        [Test]
        public void Smooth_high_quality_collapse_to_auto() {
            // CSS spec values that we don't model differently are silently
            // mapped to Auto so authors don't get unexpected nearest-
            // neighbor filtering on images they wrote `smooth` for.
            var s = Style();
            s.Set("image-rendering", "smooth");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.Auto));
            s.Set("image-rendering", "high-quality");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.Auto));
        }

        [Test]
        public void Unknown_keyword_falls_back_to_auto() {
            var s = Style();
            s.Set("image-rendering", "magic");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.Auto));
        }

        [Test]
        public void Mixed_case_keyword_still_resolves() {
            var s = Style();
            s.Set("image-rendering", "PIXELATED");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.Pixelated));
            s.Set("image-rendering", "Crisp-Edges");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.CrispEdges));
        }

        [Test]
        public void Whitespace_around_value_is_tolerated() {
            var s = Style();
            s.Set("image-rendering", "  pixelated  ");
            Assert.That(ImageRenderingResolver.Resolve(s), Is.EqualTo(ImageRenderingMode.Pixelated));
        }

        [Test]
        public void Null_style_returns_auto() {
            Assert.That(ImageRenderingResolver.Resolve(null), Is.EqualTo(ImageRenderingMode.Auto));
        }

        [Test]
        public void Image_brush_carries_image_rendering_through_factory() {
            var b = Brush.Image("sprite-id", new Rect(0, 0, 16, 16), ImageRenderingMode.Pixelated);
            Assert.That(b.ImageRendering, Is.EqualTo(ImageRenderingMode.Pixelated));
            Assert.That(b.ImageHandle, Is.EqualTo("sprite-id"));
        }

        [Test]
        public void Solid_color_brush_defaults_image_rendering_to_auto() {
            var b = Brush.SolidColor(LinearColor.Black);
            Assert.That(b.ImageRendering, Is.EqualTo(ImageRenderingMode.Auto));
        }
    }
}
