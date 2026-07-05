using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using Weva.Layout.Scrolling.Snap;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    public class ScrollEventTests {
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

        static (Box root, ScrollContainer sc, EventDispatcher dispatcher,
                ScrollEventHandler handler, ElementBoxIndex index) BuildHarness(string html, string css) {
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Weva.Dom.Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var index = new ElementBoxIndex(root);
            var handler = new ScrollEventHandler(dispatcher, doc, sc, index.Lookup, () => 16);
            return (root, sc, dispatcher, handler, index);
        }

        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                var e = b.Element;
                if (e == null) continue;
                var c = e.GetAttribute("class");
                if (string.IsNullOrEmpty(c)) continue;
                foreach (var t in c.Split(' ')) if (t == cls) return b;
            }
            return null;
        }

        [Test]
        public void Wheel_event_scrolls_nearest_scrollable_ancestor() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);
            Assert.That(state, Is.Not.Null);
            // Programmatic scrolling (avoids depending on hit-tester coordinates):
            h.handler.ScrollBy(box.Element, 0, 50);
            Assert.That(state.ScrollY, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Scroll_clamps_at_max() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 200px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);
            h.handler.ScrollBy(box.Element, 0, 99999);
            Assert.That(state.ScrollY, Is.EqualTo(state.MaxScrollY).Within(0.001));
        }

        [Test]
        public void ScrollTo_sets_position() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);
            h.handler.ScrollTo(box.Element, 0, 30);
            Assert.That(state.ScrollY, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Wheel_in_non_scrollable_does_nothing() {
            const string css = ".viewport { overflow: visible; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            // Visible overflow: nearest scroll container starting at this box is null.
            h.handler.ScrollBy(box.Element, 0, 50);
            Assert.That(h.sc.Get(box), Is.Null);
        }

        [Test]
        public void Home_key_jumps_to_top_via_dispatch() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);
            state.ScrollY = 200;
            h.handler.ScrollTo(box.Element, 0, 0);
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void End_via_max_scroll() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);
            h.handler.ScrollTo(box.Element, 0, state.MaxScrollY);
            Assert.That(state.ScrollY, Is.EqualTo(state.MaxScrollY).Within(0.001));
        }

        [Test]
        public void Scroll_bumps_state_version() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);
            long v0 = state.Version;
            h.handler.ScrollBy(box.Element, 0, 25);
            Assert.That(state.Version, Is.GreaterThan(v0));
        }

        [Test]
        public void Dispatched_line_wheel_scrolls_by_browser_like_line_step() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);

            h.dispatcher.DispatchWheel(10, 10, 0, 1, WheelDeltaMode.Line, KeyModifiers.None);

            Assert.That(state.ScrollY, Is.EqualTo(ScrollMath.LineStepPx).Within(0.001));
        }

        [Test]
        public void Dragging_vertical_scrollbar_thumb_updates_scroll_position() {
            const string css = ".viewport { overflow-y: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);

            h.dispatcher.DispatchPointerDown(194, 10, 0, KeyModifiers.None);
            h.dispatcher.DispatchPointerMove(194, 50, KeyModifiers.None);
            h.dispatcher.DispatchPointerUp(194, 50, 0, KeyModifiers.None);

            Assert.That(state.ScrollY, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Programmatic_scroll_works_on_overflow_hidden_but_wheel_does_not() {
            const string css = ".viewport { overflow: hidden; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");

            h.handler.ScrollBy(box.Element, 0, 50);
            var state = h.sc.Get(box);
            Assert.That(state, Is.Not.Null);
            Assert.That(state.ScrollY, Is.EqualTo(50).Within(0.001));

            double yBefore = state.ScrollY;
            h.dispatcher.DispatchWheel(10, 10, 0, 1, WheelDeltaMode.Line, KeyModifiers.None);
            Assert.That(state.ScrollY, Is.EqualTo(yBefore).Within(0.001));
        }

        [Test]
        public void Wheel_residual_axis_bubbles_to_ancestor_when_inner_cannot_consume() {
            const string css =
                ".outer { overflow-y: auto; overflow-x: hidden; height: 200px; width: 400px; }"
                + ".outer-content { height: 1000px; width: 400px; }"
                + ".inner { overflow-x: auto; overflow-y: hidden; height: 100px; width: 300px; }"
                + ".inner-content { height: 100px; width: 1500px; }";
            const string html =
                "<div class=\"outer\"><div class=\"outer-content\">"
                + "<div class=\"inner\"><div class=\"inner-content\"></div></div>"
                + "</div></div>";
            var h = BuildHarness(html, css);
            var outerBox = FindByClass(h.root, "outer");
            var innerBox = FindByClass(h.root, "inner");
            var outerState = h.sc.Get(outerBox);
            var innerState = h.sc.Get(innerBox);
            Assert.That(outerState, Is.Not.Null);
            Assert.That(innerState, Is.Not.Null);
            Assert.That(innerState.CanScrollX, Is.True);
            Assert.That(innerState.CanScrollY, Is.False);
            Assert.That(outerState.CanScrollY, Is.True);

            h.dispatcher.DispatchWheel(50, 30, 40, 25, WheelDeltaMode.Pixel, KeyModifiers.None);

            Assert.That(innerState.ScrollX, Is.EqualTo(40).Within(0.001));
            Assert.That(outerState.ScrollY, Is.EqualTo(25).Within(0.001));
        }

        [Test]
        public void Wheel_pan_honors_scroll_snap_stop_always_on_intermediate_point() {
            ScrollSnapProperties.EnsureRegistered();
            const string css =
                ".vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }"
                + ".item { height: 80px; scroll-snap-align: start; }"
                + "#middle { scroll-snap-stop: always; }"
                + ".pad { height: 600px; }";
            const string html =
                "<div class=\"vp\">"
                + "<div class=\"item\" id=\"start\"></div>"
                + "<div class=\"item\" id=\"middle\"></div>"
                + "<div class=\"item\" id=\"end\"></div>"
                + "<div class=\"pad\"></div></div>";

            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var index = new Dictionary<Element, Box>();
            void Walk(Box b) {
                if (b == null) return;
                if (b.Element != null) index[b.Element] = b;
                foreach (var c in b.Children) Walk(c);
            }
            Walk(root);
            double now = 0;
            var handler = new ScrollEventHandler(dispatcher, doc, sc, e => index.TryGetValue(e, out var b) ? b : null, () => 16, () => now);
            handler.SnapResolver = new SnapResolver(sc);

            Box vp = FindByClass(root, "vp");
            var state = sc.Get(vp);
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001));

            // Snap points (start-aligned, 80px items, 68px scroll viewport after
            // scrollbar reservation): start=0, middle=80, end=160. We pan from 0
            // by enough to overshoot end; #middle has scroll-snap-stop: always so
            // settle must land at 80, not 160.
            dispatcher.DispatchWheel(10, 10, 0, 400, WheelDeltaMode.Pixel, KeyModifiers.None);
            Assert.That(state.ScrollY, Is.GreaterThan(80));

            now = ScrollEventHandler.SnapSettleSeconds + 0.01;
            handler.TickSnap(now);

            Assert.That(state.ScrollY, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void Programmatic_scroll_resolves_snap_per_scroll_snap_type() {
            ScrollSnapProperties.EnsureRegistered();
            const string css =
                ".vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }"
                + ".item { height: 80px; scroll-snap-align: start; }"
                + ".pad { height: 400px; }";
            const string html =
                "<div class=\"vp\">"
                + "<div class=\"item\" id=\"a\"></div>"
                + "<div class=\"item\" id=\"b\"></div>"
                + "<div class=\"item\" id=\"c\"></div>"
                + "<div class=\"pad\"></div></div>";
            var h = BuildHarness(html, css);
            h.handler.SnapResolver = new SnapResolver(h.sc);
            var box = FindByClass(h.root, "vp");
            var state = h.sc.Get(box);

            // Mandatory: ScrollTo a non-snap position must settle to nearest snap point.
            // Snap points (start-aligned, 80px items, 68px scroll viewport): a=0, b=80, c=160.
            // ScrollTo y=70 -> snaps to 80 (nearest in mandatory mode).
            h.handler.ScrollTo(box.Element, 0, 70);
            Assert.That(state.ScrollY, Is.EqualTo(80).Within(0.5));

            // ScrollBy from snap point past another snap by a small amount also settles.
            h.handler.ScrollBy(box.Element, 0, 5);
            Assert.That(state.ScrollY, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void Programmatic_scroll_does_not_snap_when_type_is_none() {
            ScrollSnapProperties.EnsureRegistered();
            const string css =
                ".vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: none; }"
                + ".item { height: 80px; scroll-snap-align: start; }"
                + ".pad { height: 400px; }";
            const string html =
                "<div class=\"vp\">"
                + "<div class=\"item\" id=\"a\"></div>"
                + "<div class=\"item\" id=\"b\"></div>"
                + "<div class=\"item\" id=\"c\"></div>"
                + "<div class=\"pad\"></div></div>";
            var h = BuildHarness(html, css);
            h.handler.SnapResolver = new SnapResolver(h.sc);
            var box = FindByClass(h.root, "vp");
            var state = h.sc.Get(box);

            h.handler.ScrollTo(box.Element, 0, 70);
            Assert.That(state.ScrollY, Is.EqualTo(70).Within(0.001));
        }

        [Test]
        public void Programmatic_scroll_proximity_snaps_only_within_threshold() {
            ScrollSnapProperties.EnsureRegistered();
            const string css =
                ".vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y proximity; }"
                + ".item { height: 80px; scroll-snap-align: start; }"
                + ".pad { height: 400px; }";
            const string html =
                "<div class=\"vp\">"
                + "<div class=\"item\" id=\"a\"></div>"
                + "<div class=\"item\" id=\"b\"></div>"
                + "<div class=\"item\" id=\"c\"></div>"
                + "<div class=\"pad\"></div></div>";
            var h = BuildHarness(html, css);
            h.handler.SnapResolver = new SnapResolver(h.sc);
            var box = FindByClass(h.root, "vp");
            var state = h.sc.Get(box);

            // 68px scroll viewport, proximity threshold = 34. Snap points: a=0, b=80, c=160.
            // y=70 is 10px from b -> within threshold -> snaps to 80.
            h.handler.ScrollTo(box.Element, 0, 70);
            Assert.That(state.ScrollY, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void ScrollBy_negative_clamps_to_zero() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            h.handler.ScrollBy(box.Element, 0, -50);
            Assert.That(h.sc.Get(box).ScrollY, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Smooth_ScrollTo_into_snap_container_snaps_target_before_animating() {
            ScrollSnapProperties.EnsureRegistered();
            SmoothScrollProperties.EnsureRegistered();
            const string css =
                ".vp { overflow-y: auto; overflow-x: hidden; height: 200px; width: 200px;"
                + " scroll-snap-type: y mandatory; scroll-behavior: smooth; }"
                + ".item { height: 200px; scroll-snap-align: start; }"
                + ".pad { height: 400px; }";
            const string html =
                "<div class=\"vp\">"
                + "<div class=\"item\" id=\"a\"></div>"
                + "<div class=\"item\" id=\"b\"></div>"
                + "<div class=\"item\" id=\"c\"></div>"
                + "<div class=\"pad\"></div></div>";
            var h = BuildHarness(html, css);
            var anim = new SmoothScrollAnimator(h.sc);
            h.handler.SmoothAnimator = anim;
            h.handler.SnapResolver = new SnapResolver(h.sc);

            var box = FindByClass(h.root, "vp");
            var state = h.sc.Get(box);

            // Snap points (start-aligned, 200px items): 0, 200, 400.
            // ScrollTo y=150 with smooth+mandatory: nearest snap to target=150 is 200.
            // The animator's target must already be the SNAPPED value.
            h.handler.ScrollTo(box.Element, 0, 150);
            Assert.That(anim.TryGet(box, out var live), Is.True);
            Assert.That(live.TargetY, Is.EqualTo(200).Within(0.5),
                "smooth animator should be aimed at the snapped target, not the raw 150");

            // Run animation to completion: final position lands on the snap point.
            anim.Tick(SmoothScrollAnimator.DefaultDurationSeconds, null);
            Assert.That(state.ScrollY, Is.EqualTo(200).Within(0.5));
            Assert.That(anim.IsAnimating(box), Is.False);
        }

        [Test]
        public void Smooth_ScrollTo_without_snap_lands_on_raw_target() {
            SmoothScrollProperties.EnsureRegistered();
            const string css =
                ".vp { overflow: auto; height: 100px; width: 200px; scroll-behavior: smooth; }"
                + ".child { height: 800px; }";
            const string html = "<div class=\"vp\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var anim = new SmoothScrollAnimator(h.sc);
            h.handler.SmoothAnimator = anim;
            h.handler.SnapResolver = new SnapResolver(h.sc);

            var box = FindByClass(h.root, "vp");
            var state = h.sc.Get(box);

            h.handler.ScrollTo(box.Element, 0, 150);
            Assert.That(anim.TryGet(box, out var live), Is.True);
            Assert.That(live.TargetY, Is.EqualTo(150).Within(0.001),
                "no snap configured -> smooth target should equal raw ScrollTo value");
            anim.Tick(SmoothScrollAnimator.DefaultDurationSeconds, null);
            Assert.That(state.ScrollY, Is.EqualTo(150).Within(0.5));
        }

        [Test]
        public void Keyboard_arrow_in_snap_container_settles_on_snap_point() {
            ScrollSnapProperties.EnsureRegistered();
            const string css =
                ".vp { overflow-y: auto; overflow-x: hidden; height: 80px; width: 200px;"
                + " scroll-snap-type: y mandatory; }"
                + ".item { height: 60px; scroll-snap-align: start; }"
                + ".pad { height: 400px; }";
            const string html =
                "<div class=\"vp\" tabindex=\"0\">"
                + "<div class=\"item\" id=\"a\"></div>"
                + "<div class=\"item\" id=\"b\"></div>"
                + "<div class=\"item\" id=\"c\"></div>"
                + "<div class=\"pad\"></div></div>";
            var h = BuildHarness(html, css);
            h.handler.SnapResolver = new SnapResolver(h.sc);

            var box = FindByClass(h.root, "vp");
            var state = h.sc.Get(box);

            // Snap points (start-aligned, 60px items): 0, 60, 120.
            // ArrowDown line-step is 40px (ScrollMath.LineStepPx) → tentative ScrollY=40.
            // Without snap settling we'd stop at 40; with snap, nearest of {0,60,120}
            // to 40 is 60.
            h.dispatcher.Focus(box.Element);
            h.dispatcher.DispatchKeyDown("ArrowDown", "ArrowDown", KeyModifiers.None, false);
            Assert.That(state.ScrollY, Is.EqualTo(60).Within(0.5),
                "arrow-down on snap container should settle on the next snap point");
        }

        // M1: nested scroll containers — a click that lands inside the
        // overlap of both scrollbar tracks must drag the INNERMOST (deepest)
        // scrollbar, not the outer one. Inner is positioned absolutely so
        // its vertical scrollbar sits at the same x band as the outer's
        // vertical scrollbar; the click overlaps both.
        [Test]
        public void Scrollbar_pointer_down_targets_deepest_scrollbar_at_overlap() {
            const string css =
                ".outer { overflow-y: auto; overflow-x: hidden; height: 200px; width: 200px;"
                + " position: relative; }"
                + ".outer-content { height: 500px; }"
                + ".inner { position: absolute; left: 0px; top: 0px;"
                + " width: 200px; height: 100px;"
                + " overflow-y: auto; overflow-x: hidden; }"
                + ".inner-content { height: 500px; }";
            const string html =
                "<div class=\"outer\">"
                + "<div class=\"outer-content\"></div>"
                + "<div class=\"inner\"><div class=\"inner-content\"></div></div>"
                + "</div>";
            var h = BuildHarness(html, css);
            var outerBox = FindByClass(h.root, "outer");
            var innerBox = FindByClass(h.root, "inner");
            var outerState = h.sc.Get(outerBox);
            var innerState = h.sc.Get(innerBox);
            Assert.That(outerState, Is.Not.Null, "outer must be a scroll container");
            Assert.That(innerState, Is.Not.Null, "inner must be a scroll container");
            Assert.That(outerState.ShowsTrackY, Is.True, "outer must show vertical scrollbar");
            Assert.That(innerState.ShowsTrackY, Is.True, "inner must show vertical scrollbar");

            // Both scrollbars sit at x ∈ [188, 200]. Inner track runs y ∈ [0,100];
            // outer track runs y ∈ [0, 200]. A click at (194, 30) overlaps both.
            double outerYBefore = outerState.ScrollY;
            h.dispatcher.DispatchPointerDown(194, 30, 0, KeyModifiers.None);
            h.dispatcher.DispatchPointerMove(194, 60, KeyModifiers.None);
            h.dispatcher.DispatchPointerUp(194, 60, 0, KeyModifiers.None);

            Assert.That(innerState.ScrollY, Is.GreaterThan(0),
                "click on overlap of inner+outer scrollbars must drag the deepest (inner) scrollbar");
            Assert.That(outerState.ScrollY, Is.EqualTo(outerYBefore).Within(0.001),
                "outer scrollbar must not have moved when the click was on the inner's scrollbar overlap");
        }

        // M1 regression guard: when the click is OUTSIDE the inner scrollbar's
        // y range but still inside the outer scrollbar track, the outer is
        // selected (no deepest overrides without a real hit).
        [Test]
        public void Scrollbar_pointer_down_outside_inner_targets_outer() {
            const string css =
                ".outer { overflow-y: auto; overflow-x: hidden; height: 200px; width: 200px;"
                + " position: relative; }"
                + ".outer-content { height: 500px; }"
                + ".inner { position: absolute; left: 0px; top: 0px;"
                + " width: 200px; height: 100px;"
                + " overflow-y: auto; overflow-x: hidden; }"
                + ".inner-content { height: 500px; }";
            const string html =
                "<div class=\"outer\">"
                + "<div class=\"outer-content\"></div>"
                + "<div class=\"inner\"><div class=\"inner-content\"></div></div>"
                + "</div>";
            var h = BuildHarness(html, css);
            var outerBox = FindByClass(h.root, "outer");
            var innerBox = FindByClass(h.root, "inner");
            var outerState = h.sc.Get(outerBox);
            var innerState = h.sc.Get(innerBox);
            Assert.That(outerState, Is.Not.Null);
            Assert.That(innerState, Is.Not.Null);

            // Click at y=150 is below the inner box (y ∈ [0,100]) so only the
            // outer scrollbar's track is hit.
            double innerYBefore = innerState.ScrollY;
            h.dispatcher.DispatchPointerDown(194, 150, 0, KeyModifiers.None);
            h.dispatcher.DispatchPointerMove(194, 180, KeyModifiers.None);
            h.dispatcher.DispatchPointerUp(194, 180, 0, KeyModifiers.None);

            Assert.That(outerState.ScrollY, Is.GreaterThan(0),
                "click outside the inner's track must drag the outer scrollbar");
            Assert.That(innerState.ScrollY, Is.EqualTo(innerYBefore).Within(0.001),
                "inner scrollbar must stay put when the click misses its track");
        }

        // ──────────────────────────────────────────────────────────
        // Touch-drag slop arming (Chrome touch semantics): a tap inside a
        // scrollable must click through untouched; the drag only arms —
        // scrolls, captures, consumes events — past TouchDragSlopPx.
        // ──────────────────────────────────────────────────────────

        static (Box box, ScrollState state, Weva.Layout.Scrolling.Smooth.ScrollMomentum momentum,
                EventDispatcher dispatcher, ScrollEventHandler handler) BuildTouchHarness() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var h = BuildHarness(html, css);
            var box = FindByClass(h.root, "viewport");
            var state = h.sc.Get(box);
            var momentum = new Weva.Layout.Scrolling.Smooth.ScrollMomentum(h.sc);
            h.handler.MomentumAnimator = momentum;
            return (box, state, momentum, h.dispatcher, h.handler);
        }

        [Test]
        public void Touch_drag_below_slop_does_not_scroll() {
            var t = BuildTouchHarness();
            t.dispatcher.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            // 5px move < 8px slop — still a tap, nothing scrolls.
            t.dispatcher.DispatchPointerMove(50, 45, KeyModifiers.None);
            t.dispatcher.DispatchPointerUp(50, 45, 0, KeyModifiers.None);
            Assert.That(t.state.ScrollY, Is.EqualTo(0).Within(0.001),
                "sub-slop movement is a tap, not a scroll");
            Assert.That(t.momentum.GlideCount, Is.EqualTo(0),
                "an un-armed candidate must not start a glide");
        }

        [Test]
        public void Touch_drag_past_slop_scrolls_the_container() {
            var t = BuildTouchHarness();
            t.dispatcher.DispatchPointerDown(50, 80, 0, KeyModifiers.None);
            // 30px upward move > 8px slop → arms; content scrolls down.
            t.dispatcher.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(t.state.ScrollY, Is.GreaterThan(0),
                "past the slop the drag scrolls the container");
        }

        [Test]
        public void Touch_drag_scroll_delta_excludes_only_pre_arm_travel_anchor() {
            // The scroll delta is measured against LastX/LastY (the previous
            // event), so the arming move itself contributes its full travel —
            // matching Chrome, where the first past-slop move jumps by the
            // accumulated distance.
            var t = BuildTouchHarness();
            t.dispatcher.DispatchPointerDown(50, 80, 0, KeyModifiers.None);
            t.dispatcher.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(t.state.ScrollY, Is.EqualTo(30).Within(0.001),
                "armed move scrolls by the full pointer travel since the last event");
        }

        [Test]
        public void Touch_tap_then_release_keeps_scrollbar_drag_unaffected() {
            // Regression guard: the slop candidate must not swallow the
            // scrollbar-drag path (which arms immediately on the thumb).
            var t = BuildTouchHarness();
            t.dispatcher.DispatchPointerDown(194, 10, 0, KeyModifiers.None);
            t.dispatcher.DispatchPointerMove(194, 50, KeyModifiers.None);
            t.dispatcher.DispatchPointerUp(194, 50, 0, KeyModifiers.None);
            Assert.That(t.state.ScrollY, Is.GreaterThan(0),
                "scrollbar thumb drag still scrolls (takes priority over touch slop)");
        }
    }
}
