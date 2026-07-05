using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    public class ScrollbarsPropertiesTests {
        static (Box root, ScrollContainer sc) BuildScroll(string css, string html) {
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            return (root, sc);
        }

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

        [Test]
        public void Scrollbar_width_keyword_controls_reserved_gutter_thickness() {
            const string htmlFmt = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            const string baseCss = ".viewport { overflow: scroll; height: 100px; width: 200px; }"
                                   + ".child { height: 300px; width: 50px; }";

            var auto = BuildScroll(baseCss, htmlFmt);
            var none = BuildScroll(baseCss + ".viewport { scrollbar-width: none; }", htmlFmt);
            var thin = BuildScroll(baseCss + ".viewport { scrollbar-width: thin; }", htmlFmt);

            var autoBox = FindByClass(auto.root, "viewport");
            var noneBox = FindByClass(none.root, "viewport");
            var thinBox = FindByClass(thin.root, "viewport");

            double autoVW = auto.sc.Get(autoBox).ViewportWidth;
            double noneVW = none.sc.Get(noneBox).ViewportWidth;
            double thinVW = thin.sc.Get(thinBox).ViewportWidth;

            Assert.That(noneVW - autoVW, Is.EqualTo(ScrollMath.ScrollbarTrackThicknessPx).Within(0.001),
                "scrollbar-width:none must reserve zero gutter (gain full track thickness vs auto)");
            Assert.That(autoVW + ScrollMath.ScrollbarTrackThicknessPx, Is.EqualTo(200).Within(0.001));
            Assert.That(noneVW, Is.EqualTo(200).Within(0.001));
            Assert.That(thinVW, Is.EqualTo(200 - ScrollMath.ScrollbarThinThicknessPx).Within(0.001));
            Assert.That(thinVW, Is.GreaterThan(autoVW));
            Assert.That(thinVW, Is.LessThan(noneVW));
            Assert.That(ScrollMath.ScrollbarThinThicknessPx, Is.LessThan(ScrollMath.ScrollbarTrackThicknessPx));
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

        static (Box root, ScrollContainer sc, EventDispatcher dispatcher, ScrollEventHandler handler) BuildHarness(string html, string css) {
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
            return (root, sc, dispatcher, handler);
        }

        const string NestedHtml =
            "<div class=\"outer\"><div class=\"outer-content\">"
            + "<div class=\"inner\"><div class=\"inner-content\"></div></div>"
            + "</div></div>";
        const string NestedCssBase =
            ".outer { overflow-y: auto; overflow-x: hidden; height: 200px; width: 400px; }"
            + ".outer-content { height: 1000px; width: 400px; }"
            + ".inner { overflow-x: auto; overflow-y: hidden; height: 100px; width: 300px; }"
            + ".inner-content { height: 100px; width: 1500px; }";

        [Test]
        public void Overscroll_behavior_contain_blocks_residual_axis_bubble_to_ancestor() {
            string css = NestedCssBase + ".inner { overscroll-behavior: contain; }";
            var h = BuildHarness(NestedHtml, css);
            var outerBox = FindByClass(h.root, "outer");
            var innerBox = FindByClass(h.root, "inner");
            var outerState = h.sc.Get(outerBox);
            var innerState = h.sc.Get(innerBox);
            Assert.That(innerState.CanScrollX, Is.True);
            Assert.That(innerState.CanScrollY, Is.False);
            Assert.That(outerState.CanScrollY, Is.True);

            h.dispatcher.DispatchWheel(50, 30, 40, 25, WheelDeltaMode.Pixel, KeyModifiers.None);

            Assert.That(innerState.ScrollX, Is.EqualTo(40).Within(0.001),
                "inner still consumes the axis it can scroll");
            Assert.That(outerState.ScrollY, Is.EqualTo(0).Within(0.001),
                "overscroll-behavior:contain must block residual-axis bubble to outer");
        }

        [Test]
        public void Overscroll_behavior_default_auto_still_bubbles_residual_axis() {
            var h = BuildHarness(NestedHtml, NestedCssBase);
            var outerBox = FindByClass(h.root, "outer");
            var innerBox = FindByClass(h.root, "inner");
            var outerState = h.sc.Get(outerBox);
            var innerState = h.sc.Get(innerBox);

            h.dispatcher.DispatchWheel(50, 30, 40, 25, WheelDeltaMode.Pixel, KeyModifiers.None);

            Assert.That(innerState.ScrollX, Is.EqualTo(40).Within(0.001));
            Assert.That(outerState.ScrollY, Is.EqualTo(25).Within(0.001),
                "default (auto) preserves A20 residual-axis bubble behavior");
        }

        [Test]
        public void Overscroll_behavior_contain_does_not_block_local_scrolling() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; overscroll-behavior: contain; }"
                               + ".child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);

            h.handler.ScrollBy(box.Element, 0, 60);
            Assert.That(state.ScrollY, Is.EqualTo(60).Within(0.001),
                "contain must not block scrolling inside the container itself");
        }
    }
}
