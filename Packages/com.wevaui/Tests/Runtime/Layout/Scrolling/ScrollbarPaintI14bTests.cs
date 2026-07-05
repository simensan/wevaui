using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Paint;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // I14b — paint-side wire-up for scrollbar-width / scrollbar-color /
    // scrollbar-gutter and per-axis overscroll-behavior longhands.
    public class ScrollbarPaintI14bTests {
        static BlockBox MakeBox(double w, double h) {
            var b = new BlockBox();
            b.X = 0; b.Y = 0; b.Width = w; b.Height = h;
            b.Element = new Element("div");
            b.Style = new ComputedStyle(b.Element);
            return b;
        }

        static ScrollState VerticalScrollable(double viewportH, double contentH, double width) {
            return new ScrollState {
                ViewportHeight = viewportH,
                ViewportWidth = width - ScrollMath.ScrollbarTrackThicknessPx,
                ScrollHeight = contentH,
                ScrollWidth = width - ScrollMath.ScrollbarTrackThicknessPx,
                OverflowX = ScrollOverflow.Hidden,
                OverflowY = ScrollOverflow.Scroll,
            };
        }

        static List<FillRectCommand> Fills(PaintList list) {
            var fills = new List<FillRectCommand>();
            foreach (var c in list.Commands) if (c is FillRectCommand f) fills.Add(f);
            return fills;
        }

        // Subtask 1: scrollbar-width: thin → 8px track painted.
        [Test]
        public void ScrollbarWidth_thin_paints_8px_track_I14b() {
            var box = MakeBox(200, 100);
            box.Style.Set("scrollbar-width", "thin");
            var state = VerticalScrollable(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            var fills = Fills(list);
            Assert.That(fills.Count, Is.EqualTo(2), "thin track + thumb (2 fills)");
            Assert.That(fills[0].Bounds.Width, Is.EqualTo(ScrollMath.ScrollbarThinThicknessPx).Within(0.001),
                "scrollbar-width: thin must paint an 8px-wide vertical track");
            Assert.That(fills[1].Bounds.Width, Is.EqualTo(ScrollMath.ScrollbarThinThicknessPx).Within(0.001),
                "thumb must match track thickness");
        }

        // Subtask 1: scrollbar-width: none → no track painted.
        [Test]
        public void ScrollbarWidth_none_emits_no_paint_commands_I14b() {
            var box = MakeBox(200, 100);
            box.Style.Set("scrollbar-width", "none");
            var state = VerticalScrollable(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            Assert.That(list.Commands.Count, Is.EqualTo(0),
                "scrollbar-width: none must suppress the entire scrollbar UA paint");
        }

        // Subtask 1 regression: default scrollbar-width: auto → 12px track.
        [Test]
        public void ScrollbarWidth_auto_default_paints_12px_track_I14b_regression() {
            var box = MakeBox(200, 100);
            // No scrollbar-width set; default = auto.
            var state = VerticalScrollable(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            var fills = Fills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            Assert.That(fills[0].Bounds.Width, Is.EqualTo(ScrollMath.ScrollbarTrackThicknessPx).Within(0.001),
                "default (auto) must keep the 12px track unchanged");
        }

        // Subtask 3: scrollbar-color: red blue → painter consumes both.
        [Test]
        public void ScrollbarColor_two_colors_apply_to_thumb_and_track_I14b() {
            var box = MakeBox(200, 100);
            box.Style.Set("scrollbar-color", "red blue");
            var state = VerticalScrollable(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            var fills = Fills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            var trackBrush = fills[0].Brush;
            var thumbBrush = fills[1].Brush;
            // First color is thumb (`red`), second is track (`blue`) per CSS Scrollbars 1 §3.2.
            // The track is emitted first, then the thumb, so list[0] = track = blue, list[1] = thumb = red.
            Assert.That(trackBrush.Color.R, Is.LessThan(0.1f), "track color blue → low R");
            Assert.That(trackBrush.Color.B, Is.GreaterThan(0.5f), "track color blue → high B");
            Assert.That(thumbBrush.Color.R, Is.GreaterThan(0.5f), "thumb color red → high R");
            Assert.That(thumbBrush.Color.B, Is.LessThan(0.1f), "thumb color red → low B");
        }

        // Subtask 3: scrollbar-color: auto leaves the UA defaults in place.
        [Test]
        public void ScrollbarColor_auto_uses_ua_default_palette_I14b() {
            var boxAuto = MakeBox(200, 100);
            // No scrollbar-color set; default = auto.
            var stateAuto = VerticalScrollable(100, 500, 200);
            var listAuto = new PaintList();
            ScrollbarPaint.Emit(boxAuto, stateAuto, 0, 0, listAuto, null);
            var fillsAuto = Fills(listAuto);

            var boxExplicit = MakeBox(200, 100);
            boxExplicit.Style.Set("scrollbar-color", "auto");
            var stateExplicit = VerticalScrollable(100, 500, 200);
            var listExplicit = new PaintList();
            ScrollbarPaint.Emit(boxExplicit, stateExplicit, 0, 0, listExplicit, null);
            var fillsExplicit = Fills(listExplicit);

            Assert.That(fillsAuto.Count, Is.EqualTo(2));
            Assert.That(fillsExplicit.Count, Is.EqualTo(2));
            // Both paths must produce identical colors (the UA default palette).
            Assert.That(fillsExplicit[0].Brush.Color.R, Is.EqualTo(fillsAuto[0].Brush.Color.R).Within(0.001));
            Assert.That(fillsExplicit[1].Brush.Color.R, Is.EqualTo(fillsAuto[1].Brush.Color.R).Within(0.001));
        }
    }

    // Subtask 2 + 4 — integration-style tests for the cascade + layout.
    public class ScrollbarLayoutI14bTests {
        static Box FindByClass(Box root, string className) {
            foreach (var box in AllBoxes(root)) {
                var cls = box.Element?.GetAttribute("class");
                if (cls == null) continue;
                foreach (var part in cls.Split(' ')) {
                    if (part == className) return box;
                }
            }
            return null;
        }

        sealed class ElementBoxIndex {
            readonly Dictionary<Element, Box> map = new();
            public ElementBoxIndex(Box root) { Walk(root); }
            void Walk(Box b) {
                if (b == null) return;
                if (b.Element != null) map[b.Element] = b;
                foreach (var c in b.Children) Walk(c);
            }
            public Box Lookup(Element e) => e != null && map.TryGetValue(e, out var b) ? b : null;
        }

        // Subtask 2: overscroll-behavior-y: contain blocks Y bubble, allows X bubble.
        [Test]
        public void OverscrollBehaviorY_contain_blocks_y_bubble_allows_x_I14b() {
            const string html =
                "<div class=\"outer\"><div class=\"outer-content\">"
                + "<div class=\"inner\"><div class=\"inner-content\"></div></div>"
                + "</div></div>";
            // Outer scrolls both axes, inner cannot scroll either (no content overflow)
            // — so any wheel delta on inner becomes residual on BOTH axes and bubbles
            // up. Y-only containment must zero out Y residual while X passes through.
            const string css =
                ".outer { overflow: auto; height: 200px; width: 400px; }"
                + ".outer-content { height: 1000px; width: 2000px; }"
                + ".inner { overflow: hidden; height: 100px; width: 300px; "
                +          "overscroll-behavior-y: contain; }"
                + ".inner-content { height: 50px; width: 100px; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var index = new ElementBoxIndex(root);
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16);

            var outerBox = FindByClass(root, "outer");
            var outerState = sc.Get(outerBox);
            Assert.That(outerState.CanScrollX, Is.True, "outer must scroll X for this test");
            Assert.That(outerState.CanScrollY, Is.True, "outer must scroll Y for this test");

            // Wheel over the inner; inner can't scroll, so residual = full delta on both axes.
            dispatcher.DispatchWheel(50, 30, 40, 25, WheelDeltaMode.Pixel, KeyModifiers.None);

            Assert.That(outerState.ScrollX, Is.EqualTo(40).Within(0.001),
                "X residual must still bubble to outer (overscroll-behavior-x is auto)");
            Assert.That(outerState.ScrollY, Is.EqualTo(0).Within(0.001),
                "Y residual must be blocked by overscroll-behavior-y: contain");
        }

        // Subtask 2: overscroll-behavior-x: contain mirrors the above on the X axis.
        [Test]
        public void OverscrollBehaviorX_contain_blocks_x_bubble_allows_y_I14b() {
            const string html =
                "<div class=\"outer\"><div class=\"outer-content\">"
                + "<div class=\"inner\"><div class=\"inner-content\"></div></div>"
                + "</div></div>";
            const string css =
                ".outer { overflow: auto; height: 200px; width: 400px; }"
                + ".outer-content { height: 1000px; width: 2000px; }"
                + ".inner { overflow: hidden; height: 100px; width: 300px; "
                +          "overscroll-behavior-x: contain; }"
                + ".inner-content { height: 50px; width: 100px; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var index = new ElementBoxIndex(root);
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16);

            var outerBox = FindByClass(root, "outer");
            var outerState = sc.Get(outerBox);

            dispatcher.DispatchWheel(50, 30, 40, 25, WheelDeltaMode.Pixel, KeyModifiers.None);

            Assert.That(outerState.ScrollX, Is.EqualTo(0).Within(0.001),
                "X residual must be blocked by overscroll-behavior-x: contain");
            Assert.That(outerState.ScrollY, Is.EqualTo(25).Within(0.001),
                "Y residual must still bubble to outer (overscroll-behavior-y is auto)");
        }

        // Subtask 4: scrollbar-gutter: stable reserves the gutter even when no
        // overflow is present.
        [Test]
        public void ScrollbarGutter_stable_reserves_gutter_with_no_overflowing_content_I14b() {
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            const string baseCss = ".viewport { overflow: auto; height: 100px; width: 200px; }"
                                   + ".child { height: 50px; width: 50px; }";

            // Without stable: no overflow → no scrollbar reserved → viewport = 200.
            var (rootNoGutter, _, _) = Build(html, baseCss, viewportWidth: 800, viewportHeight: 600);
            var scNoGutter = new ScrollContainer();
            new ScrollLayout(scNoGutter).Run(rootNoGutter);

            // With stable: gutter reserved unconditionally → viewport = 200 - 12.
            var (rootStable, _, _) = Build(html, baseCss + ".viewport { scrollbar-gutter: stable; }",
                                            viewportWidth: 800, viewportHeight: 600);
            var scStable = new ScrollContainer();
            new ScrollLayout(scStable).Run(rootStable);

            var noGutterBox = FindByClass(rootNoGutter, "viewport");
            var stableBox = FindByClass(rootStable, "viewport");
            double noGutterVW = scNoGutter.Get(noGutterBox).ViewportWidth;
            double stableVW = scStable.Get(stableBox).ViewportWidth;

            Assert.That(noGutterVW, Is.EqualTo(200).Within(0.001),
                "scrollbar-gutter:auto + no overflow → full inner width");
            Assert.That(stableVW, Is.EqualTo(200 - ScrollMath.ScrollbarTrackThicknessPx).Within(0.001),
                "scrollbar-gutter:stable must reserve gutter space even with no overflow");
        }

        // Subtask 2 regression: overscroll-behavior shorthand still expands to
        // both longhands (CSS Overscroll Behavior 1 §2 — one value applies to
        // both axes).
        [Test]
        public void OverscrollBehavior_shorthand_expands_to_both_longhands_I14b() {
            const string html =
                "<div class=\"outer\"><div class=\"outer-content\">"
                + "<div class=\"inner\"><div class=\"inner-content\"></div></div>"
                + "</div></div>";
            const string css =
                ".outer { overflow: auto; height: 200px; width: 400px; }"
                + ".outer-content { height: 1000px; width: 2000px; }"
                + ".inner { overflow: hidden; height: 100px; width: 300px; "
                +          "overscroll-behavior: contain; }"
                + ".inner-content { height: 50px; width: 100px; }";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var index = new ElementBoxIndex(root);
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16);

            var outerBox = FindByClass(root, "outer");
            var outerState = sc.Get(outerBox);

            dispatcher.DispatchWheel(50, 30, 40, 25, WheelDeltaMode.Pixel, KeyModifiers.None);

            Assert.That(outerState.ScrollX, Is.EqualTo(0).Within(0.001),
                "shorthand contain must block X bubble");
            Assert.That(outerState.ScrollY, Is.EqualTo(0).Within(0.001),
                "shorthand contain must block Y bubble");
        }
    }
}
