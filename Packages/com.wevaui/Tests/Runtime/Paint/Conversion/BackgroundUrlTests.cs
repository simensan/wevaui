using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // background-image: url(...) detection. The resolver wraps a parsed
    // url() into Brush.Image so the renderer can sample a registered
    // texture. Tests pin handle extraction (with/without quotes), the
    // image-rendering propagation, and the precedence of url() vs
    // gradient layers within a single background-image declaration.
    public class BackgroundUrlTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds() => new Rect(0, 0, 100, 100);

        [Test]
        public void TryParseUrl_extracts_unquoted_handle() {
            Assert.That(BackgroundResolver.TryParseUrl("url(ui/heart-icon)", out var h), Is.True);
            Assert.That(h, Is.EqualTo("ui/heart-icon"));
        }

        [Test]
        public void TryParseUrl_extracts_double_quoted_handle() {
            Assert.That(BackgroundResolver.TryParseUrl("url(\"ui/heart-icon\")", out var h), Is.True);
            Assert.That(h, Is.EqualTo("ui/heart-icon"));
        }

        [Test]
        public void TryParseUrl_extracts_single_quoted_handle() {
            Assert.That(BackgroundResolver.TryParseUrl("url('ui/heart-icon')", out var h), Is.True);
            Assert.That(h, Is.EqualTo("ui/heart-icon"));
        }

        [Test]
        public void TryParseUrl_rejects_non_url_function() {
            Assert.That(BackgroundResolver.TryParseUrl("linear-gradient(red, blue)", out _), Is.False);
        }

        [Test]
        public void TryParseUrl_rejects_empty_handle() {
            Assert.That(BackgroundResolver.TryParseUrl("url()", out _), Is.False);
            Assert.That(BackgroundResolver.TryParseUrl("url(\"\")", out _), Is.False);
        }

        [Test]
        public void Background_image_url_produces_image_brush() {
            var s = Style();
            s.Set("background-image", "url(ui/heart-icon)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(brush.ImageHandle, Is.EqualTo("ui/heart-icon"));
        }

        [Test]
        public void Background_image_url_inherits_image_rendering_pixelated() {
            var s = Style();
            s.Set("background-image", "url(sprites/player)");
            s.Set("image-rendering", "pixelated");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.ImageRendering, Is.EqualTo(ImageRenderingMode.Pixelated));
        }

        [Test]
        public void Background_image_url_default_image_rendering_is_auto() {
            var s = Style();
            s.Set("background-image", "url(sprites/player)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush.ImageRendering, Is.EqualTo(ImageRenderingMode.Auto));
        }

        [Test]
        public void Multiple_layers_url_then_gradient_emits_both() {
            var s = Style();
            s.Set("background-image", "url(overlay), linear-gradient(red, blue)");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(output[0].ImageHandle, Is.EqualTo("overlay"));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.Gradient));
        }

        [Test]
        public void Url_layer_does_not_affect_solid_color_layer_in_layers_list() {
            var s = Style();
            s.Set("background-image", "url(icon)");
            s.Set("background-color", "red");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            // Order: image layer first (top), color last (bottom).
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.SolidColor));
        }

        [Test]
        public void Source_rect_is_full_image_in_v1() {
            // Background-position/size aren't yet wired; the brush carries
            // a (0,0,1,1) source rect so backends know to use the entire
            // registered image.
            var s = Style();
            s.Set("background-image", "url(icon)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush.ImageSourceRect.X, Is.EqualTo(0));
            Assert.That(brush.ImageSourceRect.Y, Is.EqualTo(0));
            Assert.That(brush.ImageSourceRect.Width, Is.EqualTo(1));
            Assert.That(brush.ImageSourceRect.Height, Is.EqualTo(1));
        }
    }
}
