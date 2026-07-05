using System;
using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // Regression: paint-time viewport-relative lengths (vh/vw in border-radius,
    // box-shadow blur, …) must resolve against the LIVE viewport the painter is
    // handed each frame (BoxToPaintConverter.ViewportWidth/Height), not the
    // build-time LengthContext frozen at construction. Before the fix, a
    // document built at one resolution kept rendering vh-sized corners/shadows
    // for that resolution forever — so they "looked different" at 4k vs 1080p
    // while the vh-based layout (which reads the live LayoutContext) scaled
    // correctly. See LengthContextFor + the ViewportWidth/Height setters.
    public class PaintViewportRelativeLengthTests {
        // The single rounded element; large enough that 10vh is never clamped to
        // half the box size at the viewports under test.
        const string Css = "#t { width:1000px; height:1000px; border-radius:10vh; background:red; }";

        static double RoundedFillRadius(System.Collections.Generic.IEnumerable<PaintCommand> cmds) {
            double max = 0;
            foreach (var c in cmds)
                if (c is FillRectCommand f) max = Math.Max(max, f.Radii.TopLeft.XRadius);
            return max;
        }

        static double FirstShadowBlur(System.Collections.Generic.IEnumerable<PaintCommand> cmds) {
            foreach (var c in cmds)
                if (c is DrawShadowCommand s) return s.Shadow.BlurRadius;
            return -1;
        }

        [Test]
        public void BoxShadow_vh_blur_tracks_the_live_paint_viewport() {
            // The real bug: BoxShadowResolver's PROCESS-STATIC result cache keyed
            // on (raw, fontSize, color) but NOT the viewport, so a vh blur froze
            // at whatever resolution first populated the cache. Separate trees +
            // converters here deliberately share that static cache, mirroring the
            // live "swap 4k <-> 1080" path.
            const string css = "#t { width:1000px; height:1000px; box-shadow:0 0 10vh red; }";

            var (rootA, _, _) = Build("<div id=\"t\"></div>", css, 1200);
            var convA = new BoxToPaintConverter { ViewportWidth = 1000, ViewportHeight = 1000 };
            Assert.That(FirstShadowBlur(convA.Convert(rootA).Commands), Is.EqualTo(100.0).Within(0.5),
                "10vh blur at a 1000px-tall viewport is 100px");

            var (rootB, _, _) = Build("<div id=\"t\"></div>", css, 1200);
            var convB = new BoxToPaintConverter { ViewportWidth = 2000, ViewportHeight = 2000 };
            Assert.That(FirstShadowBlur(convB.Convert(rootB).Commands), Is.EqualTo(200.0).Within(0.5),
                "the shared static shadow cache must not freeze the vh blur at the first viewport");
        }

        [Test]
        public void BorderRadius_vh_resolves_against_the_live_paint_viewport_height() {
            // Fresh tree + converter per viewport so this isolates LengthContextFor
            // (no cross-Convert cache in play). 10vh = 10% of viewport height.
            var (rootA, _, _) = Build("<div id=\"t\"></div>", Css, 1200);
            var convA = new BoxToPaintConverter { ViewportWidth = 1000, ViewportHeight = 1000 };
            Assert.That(RoundedFillRadius(convA.Convert(rootA).Commands), Is.EqualTo(100.0).Within(0.5),
                "10vh at a 1000px-tall viewport is 100px");

            var (rootB, _, _) = Build("<div id=\"t\"></div>", Css, 1200);
            var convB = new BoxToPaintConverter { ViewportWidth = 2000, ViewportHeight = 2000 };
            Assert.That(RoundedFillRadius(convB.Convert(rootB).Commands), Is.EqualTo(200.0).Within(0.5),
                "the SAME box at a 2000px-tall viewport is 200px — corners scale with the viewport");
        }

        [Test]
        public void Changing_the_viewport_invalidates_the_paint_cache() {
            // One converter, one tree: the box size never changes, only the
            // viewport. The cache must still miss so the vh radius re-resolves —
            // this is the fixed-px-box-with-vh-decoration case the box.Version /
            // DecorationVersion cache key alone does NOT cover.
            var (root, _, _) = Build("<div id=\"t\"></div>", Css, 1200);
            var conv = new BoxToPaintConverter { ViewportWidth = 1000, ViewportHeight = 1000 };
            Assert.That(RoundedFillRadius(conv.Convert(root).Commands), Is.EqualTo(100.0).Within(0.5));

            conv.ViewportWidth = 2000;
            conv.ViewportHeight = 2000;
            Assert.That(RoundedFillRadius(conv.Convert(root).Commands), Is.EqualTo(200.0).Within(0.5),
                "resize re-resolves the cached corner instead of serving the 100px one");
        }

        [Test]
        public void Unset_paint_viewport_falls_back_to_the_construction_length_context() {
            var (root, _, _) = Build("<div id=\"t\"></div>", Css, 1200);
            // ViewportWidth/Height left at 0 → LengthContext.Default (1080px tall):
            // 10vh = 108px. Proves the guard keeps headless Convert() callers on
            // the explicit context instead of silently zeroing the viewport.
            var conv = new BoxToPaintConverter();
            Assert.That(RoundedFillRadius(conv.Convert(root).Commands), Is.EqualTo(108.0).Within(0.5));
        }
    }
}
