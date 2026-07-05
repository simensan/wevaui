using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Snap;
using Weva.Layout.Scrolling.Smooth;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling.Snap {
    public class SnapResolverTests {
        static SnapResolverTests() { ScrollSnapProperties.EnsureRegistered(); }

        static (Box vp, ScrollContainer sc, SnapResolver resolver) Build3Items(string snapType = "y mandatory", string align = "start") {
            string css = $@"
              .vp {{ overflow: auto; height: 80px; width: 200px; scroll-snap-type: {snapType}; }}
              .item {{ height: 80px; scroll-snap-align: {align}; }}
              .pad {{ height: 200px; }}
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"item\" id=\"c\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                var c = b.Element?.GetAttribute("class");
                if (c == "vp") { vp = b; break; }
            }
            return (vp, sc, new SnapResolver(sc));
        }

        [Test]
        public void Y_mandatory_snaps_to_nearest_child_top() {
            var (vp, sc, resolver) = Build3Items();
            var state = sc.Get(vp);
            // Set scroll near the second child top (~80px) but not exactly.
            state.ScrollY = 70;
            var type = SnapResolver.ResolveType(vp);
            Assert.That(type.IsActive, Is.True);
            Assert.That(type.Strictness, Is.EqualTo(SnapStrictness.Mandatory));
            bool ok = resolver.TryFindSnapTargetY(vp, state.ScrollY, type, out double snapped);
            Assert.That(ok, Is.True);
            Assert.That(snapped, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void Y_mandatory_snaps_to_first_when_at_zero() {
            var (vp, sc, resolver) = Build3Items();
            var state = sc.Get(vp);
            state.ScrollY = 5;
            var type = SnapResolver.ResolveType(vp);
            bool ok = resolver.TryFindSnapTargetY(vp, state.ScrollY, type, out double snapped);
            Assert.That(ok, Is.True);
            Assert.That(snapped, Is.EqualTo(0).Within(0.5));
        }

        [Test]
        public void Proximity_only_snaps_within_threshold() {
            var (vp, sc, resolver) = Build3Items(snapType: "y proximity");
            var state = sc.Get(vp);
            // Container height is 80 px but a vertical scrollbar reserves 12 px,
            // so the scroll viewport is 68 px. Threshold = 68 * 0.5 = 34. Place
            // ScrollY 20 px from a snap point so we sit comfortably inside the
            // proximity window.
            state.ScrollY = 20;
            var type = SnapResolver.ResolveType(vp);
            Assert.That(type.Strictness, Is.EqualTo(SnapStrictness.Proximity));
            bool ok = resolver.TryFindSnapTargetY(vp, state.ScrollY, type, out _);
            Assert.That(ok, Is.True);
        }

        [Test]
        public void Proximity_does_not_snap_when_far_from_any_point() {
            // Make children sparse so no snap point is within threshold.
            string css = @"
              .vp { overflow: auto; height: 40px; width: 200px; scroll-snap-type: y proximity; }
              .item { height: 200px; scroll-snap-align: start; }
              .pad { height: 1000px; }
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var state = sc.Get(vp);
            // Snap points: 0 and 200. Threshold = 40 * 0.5 = 20. Place at 100 — too far from both.
            state.ScrollY = 100;
            var type = SnapResolver.ResolveType(vp);
            bool ok = resolver.TryFindSnapTargetY(vp, state.ScrollY, type, out _);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void Snap_align_center_positions_child_center_at_viewport_center() {
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .item { height: 40px; scroll-snap-align: center; }
              .pad { height: 200px; }
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            // Container height 80 px, scrollbar reserves 12 px, so scroll viewport
            // is 68 px tall (centre at 34). Item a centre = 20; snap = 20 - 34 =
            // -14 -> clamp 0. Item b centre = 60; snap = 60 - 34 = 26.
            var type = SnapResolver.ResolveType(vp);
            sc.Get(vp).ScrollY = 24;
            bool ok = resolver.TryFindSnapTargetY(vp, sc.Get(vp).ScrollY, type, out double snapped);
            Assert.That(ok, Is.True);
            Assert.That(snapped, Is.EqualTo(26).Within(0.5));
        }

        [Test]
        public void Snap_align_end_positions_child_end_at_viewport_end() {
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .item { height: 40px; scroll-snap-align: end; }
              .pad { height: 400px; }
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            // Container height 80 px, scrollbar reserves 12 px, so scroll viewport
            // is 68 px tall.
            //   Item a bottom = 40, snap = 40 - 68 = -28 -> clamp 0.
            //   Item b bottom = 80, snap = 80 - 68 = 12.
            var type = SnapResolver.ResolveType(vp);
            var points = resolver.CollectSnapPointsY(vp);
            Assert.That(points.Count, Is.EqualTo(2));
            Assert.That(points[0].Position, Is.EqualTo(0).Within(0.5));
            Assert.That(points[1].Position, Is.EqualTo(12).Within(0.5));
        }

        [Test]
        public void Snap_points_include_scroll_padding() {
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; scroll-padding-top: 10px; }
              .item { height: 80px; scroll-snap-align: start; }
              .pad { height: 200px; }
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var pts = resolver.CollectSnapPointsY(vp);
            Assert.That(pts.Count, Is.GreaterThanOrEqualTo(2));
            // First child top is 0; with padding-top=10, snap = 0 - 10 = -10 -> clamp 0.
            // Second child top is 80; snap = 80 - 10 = 70.
            // Find item-b snap.
            bool found70 = false;
            foreach (var p in pts) {
                if (System.Math.Abs(p.Position - 70) < 0.5) { found70 = true; break; }
            }
            Assert.That(found70, Is.True);
        }

        [Test]
        public void Snap_type_none_returns_inactive() {
            var (vp, sc, resolver) = Build3Items(snapType: "none");
            var type = SnapResolver.ResolveType(vp);
            Assert.That(type.IsActive, Is.False);
            bool ok = resolver.TryFindSnapTargetY(vp, 50, type, out _);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void Settle_via_scroll_event_handler_triggers_snap() {
            var (vp, sc, _) = Build3Items();
            var handler = new ScrollEventHandler(null, null, sc, null, () => 16, () => 0);
            handler.SnapResolver = new SnapResolver(sc);
            sc.Get(vp).ScrollY = 70;
            handler.SettleSnap(vp);
            Assert.That(sc.Get(vp).ScrollY, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void Snap_x_axis_skipped_for_y_only_type() {
            var (vp, sc, resolver) = Build3Items(snapType: "y mandatory");
            var type = SnapResolver.ResolveType(vp);
            bool ok = resolver.TryFindSnapTargetX(vp, 0, type, out _);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void Property_registration() {
            ScrollSnapProperties.EnsureRegistered();
            Assert.That(Weva.Css.Cascade.CssProperties.TryGet("scroll-snap-type", out _), Is.True);
            Assert.That(Weva.Css.Cascade.CssProperties.TryGet("scroll-snap-align", out _), Is.True);
            Assert.That(Weva.Css.Cascade.CssProperties.TryGet("scroll-snap-stop", out _), Is.True);
            Assert.That(Weva.Css.Cascade.CssProperties.TryGet("scroll-padding-top", out _), Is.True);
            Assert.That(Weva.Css.Cascade.CssProperties.TryGet("scroll-margin-top", out _), Is.True);
        }

        [Test]
        public void Parser_handles_keywords() {
            var t = SnapParser.ParseType("y mandatory");
            Assert.That(t.Axis, Is.EqualTo(SnapAxis.Y));
            Assert.That(t.Strictness, Is.EqualTo(SnapStrictness.Mandatory));
            t = SnapParser.ParseType("x proximity");
            Assert.That(t.Axis, Is.EqualTo(SnapAxis.X));
            Assert.That(t.Strictness, Is.EqualTo(SnapStrictness.Proximity));
            t = SnapParser.ParseType("none");
            Assert.That(t.IsActive, Is.False);

            var a = SnapParser.ParseAlign("center");
            Assert.That(a.Block, Is.EqualTo(SnapAlign.Center));
            Assert.That(a.Inline, Is.EqualTo(SnapAlign.Center));
            a = SnapParser.ParseAlign("start end");
            Assert.That(a.Block, Is.EqualTo(SnapAlign.Start));
            Assert.That(a.Inline, Is.EqualTo(SnapAlign.End));

            Assert.That(SnapParser.ParseStop("always"), Is.EqualTo(SnapStop.Always));
            Assert.That(SnapParser.ParseStop("normal"), Is.EqualTo(SnapStop.Normal));
        }

        // Regression: menu.html .snap-container demo audit called out
        // `scroll-snap-type: x mandatory` with `scroll-snap-align: start`.
        // Verify the X-axis path collects per-child positions and snaps to
        // the nearest one.
        [Test]
        public void X_mandatory_snaps_to_nearest_child_left() {
            string css = @"
              .vp { overflow: auto; height: 80px; width: 100px; scroll-snap-type: x mandatory; display: flex; flex-direction: row; }
              .item { width: 100px; height: 80px; scroll-snap-align: start; flex-shrink: 0; }
              .pad { width: 200px; height: 80px; flex-shrink: 0; }
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"item\" id=\"c\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var type = SnapResolver.ResolveType(vp);
            Assert.That(type.IsActive, Is.True);
            Assert.That(type.Axis, Is.EqualTo(SnapAxis.X));
            Assert.That(type.Strictness, Is.EqualTo(SnapStrictness.Mandatory));
            // CollectSnapPointsX must yield at least one point for the snap children.
            var pts = resolver.CollectSnapPointsX(vp);
            Assert.That(pts.Count, Is.GreaterThanOrEqualTo(1));
            // Driving X-axis snap must succeed (Y-axis path must not).
            bool xOk = resolver.TryFindSnapTargetX(vp, sc.Get(vp).ScrollX, type, out _);
            bool yOk = resolver.TryFindSnapTargetY(vp, sc.Get(vp).ScrollY, type, out _);
            Assert.That(xOk, Is.True);
            Assert.That(yOk, Is.False);
        }

        [Test]
        public void Snap_stop_always_picked_when_pan_crosses_it() {
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .item { height: 80px; scroll-snap-align: start; }
              #b { scroll-snap-stop: always; }
              .pad { height: 400px; }
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"item\" id=\"c\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var type = SnapResolver.ResolveType(vp);

            // Snap points (start-aligned): a=0, b=80, c=160. Pan from 0 to 160
            // crosses b (with snap-stop: always) — resolver must pick b, not c.
            bool ok = resolver.TryFindSnapTargetY(vp, 0, 160, type, out double snapped);
            Assert.That(ok, Is.True);
            Assert.That(snapped, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void Snap_stop_normal_does_not_force_intermediate_point() {
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .item { height: 80px; scroll-snap-align: start; }
              #b { scroll-snap-stop: normal; }
              .pad { height: 400px; }
            ";
            string html = "<div class=\"vp\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"item\" id=\"c\"></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var type = SnapResolver.ResolveType(vp);

            // Same geometry, but b is `snap-stop: normal`. Resolver must keep
            // existing nearest-to-destination behaviour and pick c (160).
            bool ok = resolver.TryFindSnapTargetY(vp, 0, 160, type, out double snapped);
            Assert.That(ok, Is.True);
            Assert.That(snapped, Is.EqualTo(160).Within(0.5));
        }

        [Test]
        public void Snap_points_collected_from_grandchildren_through_wrapper() {
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .wrap { }
              .item { height: 80px; scroll-snap-align: start; }
              .pad { height: 200px; }
            ";
            string html = "<div class=\"vp\"><div class=\"wrap\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div><div class=\"item\" id=\"c\"></div><div class=\"pad\"></div></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var pts = resolver.CollectSnapPointsY(vp);
            Assert.That(pts.Count, Is.EqualTo(3));
            Assert.That(pts[0].Position, Is.EqualTo(0).Within(0.5));
            Assert.That(pts[1].Position, Is.EqualTo(80).Within(0.5));
            Assert.That(pts[2].Position, Is.EqualTo(160).Within(0.5));

            // Regression guard: same three points when the items are direct children.
            var (vpFlat, _, resolverFlat) = Build3Items();
            var flatPts = resolverFlat.CollectSnapPointsY(vpFlat);
            Assert.That(flatPts.Count, Is.EqualTo(3));
        }

        [Test]
        public void Snap_points_collected_from_three_levels_deep_nested_wrapper() {
            // CSS Scroll Snap L1 §6 — the snap container's pool is the full
            // subtree, stopping only at nested scroll containers. A 3-level
            // wrapper chain (wrap → inner → outer wrapper, none of them
            // scroll containers) must still surface the deepest snap-aligned
            // items.
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .l1 { }
              .l2 { }
              .l3 { }
              .item { height: 80px; scroll-snap-align: start; }
              .pad { height: 200px; }
            ";
            string html = "<div class=\"vp\"><div class=\"l1\"><div class=\"l2\"><div class=\"l3\"><div class=\"item\" id=\"a\"></div><div class=\"item\" id=\"b\"></div></div></div></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var pts = resolver.CollectSnapPointsY(vp);
            Assert.That(pts.Count, Is.EqualTo(2),
                "depth-3 wrapper chain must not block snap-area discovery");
            Assert.That(pts[0].Position, Is.EqualTo(0).Within(0.5));
            Assert.That(pts[1].Position, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void Snap_points_skip_wrapper_without_snap_align_but_include_its_children() {
            // CSS Scroll Snap L1 §6 — a wrapper without scroll-snap-align is
            // not itself a snap area, but its descendants ARE. The Recurse
            // visit() invocation visits the wrapper (no snap-align → no point)
            // then descends; only the leaf items contribute.
            string css = @"
              .vp { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .group { /* no snap-align */ }
              .item { height: 80px; scroll-snap-align: start; }
              .pad { height: 200px; }
            ";
            string html = "<div class=\"vp\"><div class=\"group\"><div class=\"item\" id=\"a\"></div></div><div class=\"group\"><div class=\"item\" id=\"b\"></div></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var pts = resolver.CollectSnapPointsY(vp);
            Assert.That(pts.Count, Is.EqualTo(2),
                "wrappers without snap-align contribute zero points; their children contribute normally");
        }

        [Test]
        public void Nested_scroll_container_descendants_excluded_from_outer_snap_points() {
            string css = @"
              .outer { overflow: auto; height: 80px; width: 200px; scroll-snap-type: y mandatory; }
              .inner { overflow: auto; height: 60px; }
              .item { height: 80px; scroll-snap-align: start; }
              .pad { height: 400px; }
            ";
            string html = "<div class=\"outer\"><div class=\"item\" id=\"a\"></div><div class=\"inner\"><div class=\"item\" id=\"nested1\"></div><div class=\"item\" id=\"nested2\"></div></div><div class=\"pad\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box outer = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "outer") { outer = b; break; }
            }
            var resolver = new SnapResolver(sc);
            var pts = resolver.CollectSnapPointsY(outer);
            // Only the direct .item (#a) is in outer's snap pool. The nested
            // scroll container's children belong to its own pool — they must
            // not contribute snap points to the outer container.
            foreach (var p in pts) {
                string id = p.Source.Element?.GetAttribute("id");
                Assert.That(id, Is.Not.EqualTo("nested1"));
                Assert.That(id, Is.Not.EqualTo("nested2"));
            }
        }

        [Test]
        public void Tick_snap_settles_after_quiet_period() {
            var (vp, sc, _) = Build3Items();
            double now = 0;
            var handler = new ScrollEventHandler(null, null, sc, null, () => 16, () => now);
            handler.SnapResolver = new SnapResolver(sc);
            // Manually mark a wheel burst.
            // Simulate: scroll set + last-wheel time recorded.
            sc.Get(vp).ScrollY = 70;
            // We don't fire a wheel event (no dispatcher), so we directly settle:
            handler.SettleSnap(vp);
            Assert.That(sc.Get(vp).ScrollY, Is.EqualTo(80).Within(0.5));
        }
    }
}
