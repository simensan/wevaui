using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint {
    // Regression (weva-landing inline <code> pills): an inline element backed
    // only by bare text runs (e.g. `<code>.html</code>`) carries its
    // background / border on an InlineBox decoration shell. BlockLayout's
    // UnwrapLineBoxes used to DROP those shells when flattening line boxes for a
    // flex / shrink-to-fit re-layout, so the next CollectInline saw only the
    // text run and never re-created the shell — the background silently
    // vanished. The shell must round-trip so the pill keeps painting.
    public class InlineBackgroundSurvivesRelayoutTests {

        static PaintList Paint(Box root) => new BoxToPaintConverter().Convert(root);

        static int CodeShellCount(Box root) =>
            AllBoxes(root).Count(b => b is InlineBox && b.Element != null && b.Element.TagName == "code");

        [Test]
        public void Inline_code_background_paints_inside_flex_item() {
            // <code> with a background, inside a <p>, inside a flex container —
            // the flex measure pass drives the lossy unwrap/coalesce round trip.
            const string css =
                ".flex { display: flex; }" +
                "code { background: #ff0000; padding: 2px 4px; }";
            const string html =
                "<div class='flex'><p>hot reload your <code>file</code> now</p></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400);

            // The decoration shell survived the flex re-layout...
            Assert.That(CodeShellCount(root), Is.EqualTo(1),
                "the <code> InlineBox decoration shell must survive flex re-layout");
            // ...and paints a (small) background rect — the pill, not a
            // full-width box. The flex item / p have no background of their own,
            // so any FillRect present is the code pill.
            var fills = Paint(root).Commands.OfType<FillRectCommand>().ToList();
            Assert.That(fills.Count, Is.GreaterThanOrEqualTo(1),
                "inline <code> background must paint");
            Assert.That(fills.Any(c => c.Bounds.Width < 80 && c.Bounds.Width > 0),
                Is.True, "the painted rect is the small <code> pill, not a full-width box");
        }

        [Test]
        public void Plain_paragraph_without_inline_bg_paints_no_background_rect() {
            // Control: same shape, no inline background — nothing to paint, and
            // the unwrap path must not invent a shell.
            const string css = ".flex { display: flex; }";
            const string html =
                "<div class='flex'><p>hot reload your <code>file</code> now</p></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400);
            var fills = Paint(root).Commands.OfType<FillRectCommand>().ToList();
            Assert.That(fills.Count, Is.EqualTo(0),
                "no backgrounds declared, so no fill rects");
        }
    }
}
