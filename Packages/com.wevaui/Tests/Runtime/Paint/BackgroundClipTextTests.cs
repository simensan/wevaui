using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint {
    // CSS Backgrounds 4 `background-clip: text`: the background is painted only
    // where the element's glyphs are, never as a box. Full glyph-clipped
    // gradient fill is a future feature; until then the engine must at least NOT
    // paint the background as an opaque rectangle behind the text (weva-landing
    // `.num.grad` stats rendered the gradient as a box over the digits). The
    // text still renders in its `color` fallback.
    public class BackgroundClipTextTests {

        static PaintList Paint(Box root) => new BoxToPaintConverter().Convert(root);

        [Test]
        public void Background_clip_text_suppresses_the_background_box() {
            const string html =
                "<div class='g' style='width:120px;height:40px;background:#ff0000;background-clip:text'>1767</div>";
            var (root, _, _) = Build(html, null, viewportWidth: 400);
            var fills = Paint(root).Commands.OfType<FillRectCommand>().ToList();
            // No background fill rect should be emitted for the clipped element.
            Assert.That(fills.Any(c => c.Bounds.Width > 50 && c.Bounds.Height > 20), Is.False,
                "background-clip:text must not paint the gradient/color as a box");
        }

        [Test]
        public void Clip_text_with_transparent_color_attaches_gradient_to_glyphs() {
            // The canonical gradient-text pattern: transparent text fill lets
            // the clipped gradient show through. The glyph DrawTextCommand must
            // carry the gradient (and it must survive the cached-replay copy).
            const string html =
                "<div style='width:120px;height:40px;background:linear-gradient(90deg,#ff0000,#0000ff);background-clip:text;color:transparent'>1767</div>";
            var (root, _, _) = Build(html, null, viewportWidth: 400);
            var texts = Paint(root).Commands.OfType<DrawTextCommand>().ToList();
            Assert.That(texts.Count, Is.GreaterThan(0), "a glyph command is emitted");
            Assert.That(texts.Any(t => t.TextFillGradient != null), Is.True,
                "the glyph command must carry the background gradient as its text fill");
        }

        [Test]
        public void Clip_text_with_opaque_color_does_not_attach_gradient() {
            // Chrome semantics: an opaque `color` paints over the clipped
            // gradient, so the gradient can never show — keep the solid fast
            // path (no gradient attached).
            const string html =
                "<div style='width:120px;height:40px;background:linear-gradient(90deg,#ff0000,#0000ff);background-clip:text;color:#8fb8ff'>1767</div>";
            var (root, _, _) = Build(html, null, viewportWidth: 400);
            var texts = Paint(root).Commands.OfType<DrawTextCommand>().ToList();
            Assert.That(texts.Count, Is.GreaterThan(0), "a glyph command is emitted");
            Assert.That(texts.All(t => t.TextFillGradient == null), Is.True,
                "an opaque text colour covers the clipped gradient — no gradient fill attached");
        }

        [Test]
        public void Plain_background_still_paints_a_box_without_clip_text() {
            // Control: identical element WITHOUT background-clip:text paints the
            // background box as usual — guards the suppression from over-firing.
            const string html =
                "<div class='g' style='width:120px;height:40px;background:#ff0000'>1767</div>";
            var (root, _, _) = Build(html, null, viewportWidth: 400);
            var fills = Paint(root).Commands.OfType<FillRectCommand>().ToList();
            Assert.That(fills.Any(c => c.Bounds.Width > 50 && c.Bounds.Height > 20), Is.True,
                "a normal background must still paint its box");
        }
    }
}
