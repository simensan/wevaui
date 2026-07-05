using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // Coverage for ScrollEventHandler.EnableViewportDragScroll — the opt-in
    // gate for "drag anywhere to pan the whole document" touch scrolling.
    //
    // The viewport (the anonymous root scroll box) is special-cased in
    // HandlePointerDown: even when it overflows and a momentum animator is
    // attached, a touch-drag over it only arms a glide when the static flag is
    // on. Element-level scroll containers drag-scroll regardless. The flag
    // defaults to false, so the drag below the/​past the slop must NOT move the
    // viewport until the flag is flipped on.
    //
    // Driven through the full dispatcher pointer pipeline (down → move → up)
    // exactly like the touch-drag tests in ScrollEventTests, so this exercises
    // the real arming path rather than a unit seam.
    public class ViewportDragScrollGateTests {

        [TearDown]
        public void ResetFlag() {
            // Static flags — restore defaults so other tests aren't affected.
            ScrollEventHandler.EnableViewportDragScroll = false;
            ScrollEventHandler.RubberBandOverscroll = true;
        }

        sealed class ElementBoxIndex {
            readonly Dictionary<Element, Box> map = new();
            public ElementBoxIndex(Box root) { Walk(root); }
            void Walk(Box b) {
                if (b == null) return;
                if (b.Element != null) map[b.Element] = b;
                foreach (var c in b.Children) Walk(c);
            }
            public Box Lookup(Element e) =>
                e != null && map.TryGetValue(e, out var b) ? b : null;
        }

        // Builds a doc whose root content overflows the viewport (so a viewport
        // scroll state exists) with NO element-level scroll container, wires the
        // ScrollEventHandler with a momentum animator and the viewport root, and
        // returns everything the test needs to drive a drag.
        static (Box root, ScrollContainer sc, ScrollState vpState,
                EventDispatcher dispatcher, ScrollEventHandler handler) BuildViewportDragHarness() {
            // Tall content, no inner overflow → only the viewport scrolls.
            const string html = "<div id=\"content\" style=\"height:2000px\">tall</div>";
            var (root, _, _) = Build(html, null, viewportWidth: 400, viewportHeight: 300);
            var sc = new ScrollContainer();
            var sl = new ScrollLayout(sc);
            sl.Run(root);
            sl.RunViewportScroll(root, 400, 300);

            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var index = new ElementBoxIndex(root);
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16, () => 0) {
                ViewportRoot = sl.ViewportScrollRoot(root)
            };
            handler.MomentumAnimator = new ScrollMomentum(sc);

            var vpState = sc.Get(root);
            return (root, sc, vpState, dispatcher, handler);
        }

        [Test]
        public void Harness_establishes_viewport_scroll_state_with_extent() {
            var h = BuildViewportDragHarness();
            Assert.That(h.vpState, Is.Not.Null, "viewport scroll state must exist");
            Assert.That(h.handler.ViewportRoot, Is.Not.Null, "viewport root must be wired");
            Assert.That(h.vpState.MaxScrollY, Is.GreaterThan(0),
                "the viewport must have real scrollable extent for the drag gate to matter");
        }

        [Test]
        public void Viewport_drag_does_not_scroll_when_gate_disabled_by_default() {
            ScrollEventHandler.EnableViewportDragScroll = false; // explicit default
            var h = BuildViewportDragHarness();

            // A drag well past the 8px slop over the document body.
            h.dispatcher.DispatchPointerDown(50, 200, 0, KeyModifiers.None);
            h.dispatcher.DispatchPointerMove(50, 120, KeyModifiers.None); // 80px up, > slop
            h.dispatcher.DispatchPointerUp(50, 120, 0, KeyModifiers.None);

            Assert.That(h.vpState.ScrollY, Is.EqualTo(0).Within(0.001),
                "with the viewport drag gate OFF, dragging over the viewport must not scroll it");
        }

        [Test]
        public void Viewport_drag_scrolls_when_gate_enabled() {
            ScrollEventHandler.EnableViewportDragScroll = true;
            var h = BuildViewportDragHarness();

            h.dispatcher.DispatchPointerDown(50, 200, 0, KeyModifiers.None);
            h.dispatcher.DispatchPointerMove(50, 120, KeyModifiers.None); // 80px up, > slop
            // No PointerUp yet — assert the live drag already moved the viewport.

            Assert.That(h.vpState.ScrollY, Is.GreaterThan(0),
                "with the viewport drag gate ON, the same drag scrolls the viewport");
        }

        [Test]
        public void Gate_only_affects_viewport_not_inner_containers() {
            // Sanity: an element-level scroll container drag-scrolls regardless
            // of the viewport flag (the flag is viewport-specific). Build a doc
            // with an inner overflow:auto box; with the gate OFF, dragging the
            // inner box must still scroll it.
            ScrollEventHandler.EnableViewportDragScroll = false;
            const string css = ".inner { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"inner\"><div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);

            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var index = new ElementBoxIndex(root);
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16, () => 0);
            handler.MomentumAnimator = new ScrollMomentum(sc);

            Box innerBox = null;
            foreach (var b in AllBoxes(root)) {
                var cls = b.Element?.GetAttribute("class");
                if (cls != null && cls.Contains("inner")) { innerBox = b; break; }
            }
            Assert.That(innerBox, Is.Not.Null);
            var innerState = sc.Get(innerBox);
            Assert.That(innerState, Is.Not.Null);

            // The inner box occupies the top-left 200x100 region; drag inside it.
            dispatcher.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            dispatcher.DispatchPointerMove(50, 10, KeyModifiers.None); // 40px up, > slop
            Assert.That(innerState.ScrollY, Is.GreaterThan(0),
                "element-level scroll containers drag-scroll regardless of the viewport gate");
        }

        [Test]
        public void Single_axis_container_does_not_pan_the_unscrollable_axis() {
            // A container that only overflows on Y (its child fills the width)
            // must NOT pan/rubber-band in X on a diagonal drag — browsers only
            // pan the axis with scrollable overflow. Regression for "pans past
            // bounds in all directions even though it only has Y scroll".
            const string css = ".inner { overflow-x: hidden; overflow-y: auto; height: 100px; width: 200px; } .child { height: 500px; width: 80px; }";
            const string html = "<div class=\"inner\"><div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);

            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var index = new ElementBoxIndex(root);
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16, () => 0);
            handler.MomentumAnimator = new ScrollMomentum(sc);

            Box innerBox = null;
            foreach (var b in AllBoxes(root)) {
                var cls = b.Element?.GetAttribute("class");
                if (cls != null && cls.Contains("inner")) { innerBox = b; break; }
            }
            Assert.That(innerBox, Is.Not.Null);
            var innerState = sc.Get(innerBox);
            Assert.That(innerState, Is.Not.Null);
            Assert.That(innerState.MaxScrollX, Is.LessThanOrEqualTo(0.0001), "harness: no X overflow");
            Assert.That(innerState.MaxScrollY, Is.GreaterThan(0), "harness: Y overflow exists");

            // Diagonal drag (up + left): would move both axes if unconstrained.
            dispatcher.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            dispatcher.DispatchPointerMove(10, 10, KeyModifiers.None); // 40px up + 40px left, > slop

            Assert.That(innerState.ScrollY, Is.GreaterThan(0), "the Y-overflow axis scrolls");
            Assert.That(innerState.ScrollX, Is.EqualTo(0).Within(0.001),
                "the X axis has no overflow — it must NOT pan/rubber-band past its 0 bound");
        }

        [Test]
        public void Overflow_y_auto_only_does_not_drag_pan_x() {
            // The real settings-panel case: only `overflow-y: auto` is authored,
            // so overflow-x stays `visible` (ResolveOverflow reads the longhands;
            // it does NOT apply the §3 visible→auto cross-propagation). The drag
            // path must key off CanScrollX (false here) — NOT the raw MaxScrollX,
            // which can be a nonzero ≤scrollbar-thickness gutter divergence — so a
            // horizontal/diagonal drag never pans X. Regression for "i can still
            // drag on X in that window, even though it only has y scroll".
            const string css = ".inner { overflow-y: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"inner\"><div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);

            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var index = new ElementBoxIndex(root);
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16, () => 0);
            handler.MomentumAnimator = new ScrollMomentum(sc);

            Box innerBox = null;
            foreach (var b in AllBoxes(root)) {
                var cls = b.Element?.GetAttribute("class");
                if (cls != null && cls.Contains("inner")) { innerBox = b; break; }
            }
            Assert.That(innerBox, Is.Not.Null);
            var innerState = sc.Get(innerBox);
            Assert.That(innerState, Is.Not.Null);
            Assert.That(innerState.CanScrollY, Is.True, "harness: Y is user-scrollable");
            Assert.That(innerState.CanScrollX, Is.False,
                "overflow-x stayed `visible` — X must not be user-scrollable even if MaxScrollX>0");

            // Diagonal drag (up + left).
            dispatcher.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            dispatcher.DispatchPointerMove(10, 10, KeyModifiers.None); // 40px up + 40px left, > slop

            Assert.That(innerState.ScrollY, Is.GreaterThan(0), "the Y-overflow axis scrolls");
            Assert.That(innerState.ScrollX, Is.EqualTo(0).Within(0.001),
                "overflow-x:visible — a left drag must NOT pan X");
        }

        // Hit tester that reports the range <input> for points in the top
        // `sliderBandHeight` px (where the slider sits) and otherwise defers to
        // the real box-tree hit tester. Needed because the headless layout
        // harness doesn't lay out form-control boxes, so the slider has no box
        // for the geometric hit tester to find — but the live engine does, and
        // the arming-skip we're testing keys purely off the event target being
        // the range element.
        sealed class SliderBandHitTester : IHitTester {
            readonly BoxTreeHitTester fallback;
            readonly Element slider;
            readonly double bandHeight;
            public SliderBandHitTester(Box root, ScrollContainer sc, Element slider, double bandHeight) {
                fallback = new BoxTreeHitTester(root, sc);
                this.slider = slider;
                this.bandHeight = bandHeight;
            }
            public Element HitTest(double x, double y) =>
                y < bandHeight ? slider : fallback.HitTest(x, y);
        }

        [Test]
        public void Drag_on_range_slider_does_not_pan_the_scroll_container() {
            // Dragging an <input type=range> must adjust the slider, NOT pan the
            // scroll container it lives in. The scroll handler runs in the CAPTURE
            // phase (before the slider's bubble listener), so it has to proactively
            // skip arming when the gesture starts on a range control. Regression
            // for "when i move slider value the page pans".
            const string css =
                ".inner { overflow-y: auto; height: 100px; width: 200px; }"
                + " .child { height: 500px; }";
            const string html =
                "<div class=\"inner\">"
                + "<input type=\"range\" class=\"sl\" min=\"0\" max=\"100\" value=\"50\">"
                + "<div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);

            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            Element slider = null;
            foreach (var b in AllBoxes(root)) {
                var e = b.Element;
                if (e == null) continue;
                foreach (var c in EnumerateElements(e)) {
                    if (c.TagName == "input" && c.GetAttribute("type") == "range") { slider = c; break; }
                }
                if (slider != null) break;
            }
            Assert.That(slider, Is.Not.Null, "harness: range input is in the DOM");

            var index = new ElementBoxIndex(root);
            // elementToBox maps the slider element to the inner scroll box so
            // NearestScrollContainer would find a container IF arming weren't
            // skipped — making the assertion meaningful.
            Box innerBox = null;
            foreach (var b in AllBoxes(root)) {
                var cls = b.Element?.GetAttribute("class");
                if (cls != null && cls.Contains("inner")) { innerBox = b; break; }
            }
            Assert.That(innerBox, Is.Not.Null);
            Func<Element, Box> lookup = e => e == slider ? innerBox : index.Lookup(e);

            var dispatcher = new EventDispatcher(doc, new SliderBandHitTester(root, sc, slider, 20));
            var handler = new ScrollEventHandler(dispatcher, doc, sc, lookup, () => 16, () => 0);
            handler.MomentumAnimator = new ScrollMomentum(sc);

            var innerState = sc.Get(innerBox);
            Assert.That(innerState, Is.Not.Null);
            Assert.That(innerState.CanScrollY, Is.True, "harness: the container can scroll Y");

            // Drag starting ON the slider (top 20px band → target is the range). Must NOT pan.
            dispatcher.DispatchPointerDown(50, 10, 0, KeyModifiers.None);
            dispatcher.DispatchPointerMove(50, 70, KeyModifiers.None); // 60px down, > slop
            Assert.That(innerState.ScrollY, Is.EqualTo(0).Within(0.001),
                "a drag begun on the range slider must not pan the scroll container");

            // Contrast: the SAME drag begun below the band (plain child) DOES pan —
            // proving the skip is specific to the slider, not a blanket disable.
            dispatcher.DispatchPointerDown(50, 60, 0, KeyModifiers.None);
            dispatcher.DispatchPointerMove(50, 20, KeyModifiers.None); // 40px up, > slop
            Assert.That(innerState.ScrollY, Is.GreaterThan(0),
                "a drag begun on non-control content still drag-scrolls the container");
        }

        static IEnumerable<Element> EnumerateElements(Element e) {
            yield return e;
            foreach (var c in e.Children) {
                if (c is Element ce) {
                    foreach (var d in EnumerateElements(ce)) yield return d;
                }
            }
        }
    }
}
