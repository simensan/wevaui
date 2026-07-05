using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // Pins paint-time stacking order: BoxToPaintConverter must consult
    // z-index when its parent establishes a stacking context, instead of
    // walking children in raw document order. The previous behaviour
    // shipped the v0.8 demo with HUD/world rendered in HTML order even
    // though the CSS asked for HUD on top.
    public class StackingOrderPaintTests {
        // Brute-force scan: returns the index of the first solid-colour
        // FillRect whose dominant channel matches `channel` ('R', 'G', 'B').
        // We don't care which DOM box authored it — only the paint order
        // of distinguishable colours, which is exactly what the converter
        // is responsible for.
        static int FindFillIndex(System.Collections.Generic.List<PaintCommand> cmds, char channel) {
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.SolidColor) {
                    var c = fr.Brush.Color;
                    switch (channel) {
                        case 'R': if (c.R > 0.5f && c.G < 0.3f && c.B < 0.3f) return i; break;
                        case 'G': if (c.G > 0.5f && c.R < 0.3f && c.B < 0.3f) return i; break;
                        case 'B': if (c.B > 0.5f && c.R < 0.3f && c.G < 0.3f) return i; break;
                    }
                }
            }
            return -1;
        }

        [Test]
        public void Higher_zindex_paints_after_lower_zindex_regardless_of_doc_order() {
            // Document order [A, B] with A z=10 and B z=1. The converter
            // must emit B's fill BEFORE A's fill so A ends up on top.
            const string html =
                "<div class=\"a\"></div>" +
                "<div class=\"b\"></div>";
            const string css = @"
                .a { position: relative; z-index: 10; width: 100px; height: 100px; background-color: red; }
                .b { position: relative; z-index: 1;  width: 100px; height: 100px; background-color: blue; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int aIdx = FindFillIndex(cmds, 'R');
            int bIdx = FindFillIndex(cmds, 'B');
            Assert.That(aIdx, Is.GreaterThanOrEqualTo(0), "Expected red fill from .a");
            Assert.That(bIdx, Is.GreaterThanOrEqualTo(0), "Expected blue fill from .b");
            // A (z=10) paints AFTER B (z=1) — the painter's algorithm
            // means later == on top.
            Assert.That(aIdx, Is.GreaterThan(bIdx),
                "z-index:10 sibling must paint after z-index:1 sibling, even though A precedes B in the source.");
        }

        [Test]
        public void Three_siblings_paint_in_zindex_order_with_auto_in_middle() {
            // Document order [A=auto, B=5, C=-1]. Spec paint order:
            // negative-z first, then z=auto/0 in doc order, then positive-z.
            // Expected sequence: C, A, B.
            const string html =
                "<div class=\"a\"></div>" +
                "<div class=\"b\"></div>" +
                "<div class=\"c\"></div>";
            const string css = @"
                .a { position: relative; width: 100px; height: 100px; background-color: red; }
                .b { position: relative; z-index: 5;  width: 100px; height: 100px; background-color: lime; }
                .c { position: relative; z-index: -1; width: 100px; height: 100px; background-color: blue; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;

            int aIdx = FindFillIndex(cmds, 'R');
            int bIdx = FindFillIndex(cmds, 'G');
            int cIdx = FindFillIndex(cmds, 'B');
            Assert.That(aIdx, Is.GreaterThanOrEqualTo(0), "Expected red fill from .a (z:auto)");
            Assert.That(bIdx, Is.GreaterThanOrEqualTo(0), "Expected green fill from .b (z:5)");
            Assert.That(cIdx, Is.GreaterThanOrEqualTo(0), "Expected blue fill from .c (z:-1)");

            // Spec order: negative-z (C), then auto/0 (A), then positive-z (B).
            Assert.That(cIdx, Is.LessThan(aIdx),
                "z-index:-1 must paint before z-index:auto siblings.");
            Assert.That(aIdx, Is.LessThan(bIdx),
                "z-index:auto must paint before z-index:5 siblings.");
        }
    }
}
