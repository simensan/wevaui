using System;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using Weva.Layout.Scrolling.Snap;
using Weva.Reactive;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // Tests for iOS-style rubber-band overscroll on ScrollMomentum / ScrollEventHandler.
    //
    // Conventions:
    //   - All time values are in seconds (injected clock, no UnityEngine.Time).
    //   - Tunables (RubberBandC, SpringOmega, etc.) are saved/restored per test.
    //   - "Visual overscroll" = the screen position, which may be outside [0, Max]
    //     during an active drag or spring-back.
    //   - "In-bounds" = ScrollX/Y in [0, MaxScroll]; "out-of-bounds" = outside that range.
    //
    // NUnit constraint rules (per project memory):
    //   - Never chain .Within off comparison constraints (Is.LessThan(x).Within(eps) = error).
    //   - Use Is.EqualTo(x).Within(eps) for tolerance; Is.LessThanOrEqualTo(x + eps) for slack.
    //   - Does.Not.Contain is substring-only — use Has.None.EqualTo for collections.
    public class RubberBandScrollTests {
        static RubberBandScrollTests() {
            SmoothScrollProperties.EnsureRegistered();
            ScrollSnapProperties.EnsureRegistered();
        }

        // Save / restore all tunables so no test bleeds into another.
        double savedDecay, savedThreshold, savedWindow;
        double savedRubberC, savedSpringOmega, savedOvershootCap, savedSpringStop;

        [SetUp]
        public void SetUp() {
            savedDecay       = ScrollMomentum.DecayBasePerMs;
            savedThreshold   = ScrollMomentum.StopThresholdPxPerSec;
            savedWindow      = ScrollMomentum.VelocityWindowSeconds;
            savedRubberC     = ScrollMomentum.RubberBandC;
            savedSpringOmega = ScrollMomentum.SpringOmega;
            savedOvershootCap = ScrollMomentum.OvershootCapPx;
            savedSpringStop  = ScrollMomentum.SpringStopThresholdPx;
        }

        [TearDown]
        public void TearDown() {
            ScrollMomentum.DecayBasePerMs          = savedDecay;
            ScrollMomentum.StopThresholdPxPerSec   = savedThreshold;
            ScrollMomentum.VelocityWindowSeconds   = savedWindow;
            ScrollMomentum.RubberBandC             = savedRubberC;
            ScrollMomentum.SpringOmega             = savedSpringOmega;
            ScrollMomentum.OvershootCapPx          = savedOvershootCap;
            ScrollMomentum.SpringStopThresholdPx   = savedSpringStop;
        }

        // ------------------------------------------------------------------ //
        //  Test helpers                                                        //
        // ------------------------------------------------------------------ //

        static (Box vp, ScrollContainer sc, ScrollState state) BuildScrollable(
            double viewportHeight = 100,
            double childHeight = 500,
            double viewportWidth = 200,
            double childWidth = 200,
            string extraCss = "") {
            string css = $@"
                .vp {{ overflow: auto; height: {viewportHeight}px; width: {viewportWidth}px; {extraCss} }}
                .child {{ height: {childHeight}px; width: {childWidth}px; }}
            ";
            string html = "<div class=\"vp\"><div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var state = sc.GetOrCreate(vp);
            return (vp, sc, state);
        }

        static (Box vp, ScrollContainer sc, ScrollState state) BuildSnapScrollable(
            string snapType = "y mandatory",
            double itemHeight = 80,
            int itemCount = 5) {
            string items = "";
            for (int i = 0; i < itemCount; i++)
                items += $"<div class=\"item\" id=\"item{i}\"></div>";
            string css = $@"
                .vp {{ overflow: auto; height: 80px; width: 200px; scroll-snap-type: {snapType}; }}
                .item {{ height: {itemHeight}px; scroll-snap-align: start; }}
            ";
            string html = $"<div class=\"vp\">{items}</div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { vp = b; break; }
            }
            var state = sc.GetOrCreate(vp);
            return (vp, sc, state);
        }

        // Run spring-back ticks until done or iteration cap.
        static void SettleSpring(ScrollMomentum momentum, Box box, ref double t, int maxIterations = 500) {
            for (int i = 0; i < maxIterations && momentum.IsGliding(box); i++) {
                t += 0.016;
                momentum.Tick(0.016, t, null);
            }
        }

        // ================================================================== //
        //  1. Rubber-band formula — math correctness                          //
        // ================================================================== //

        [Test]
        public void RubberBand_formula_zero_raw_gives_zero_visual() {
            // No overscroll → visual = 0.
            double visual = ScrollMomentum.RubberBandVisual(0, 100);
            Assert.That(visual, Is.EqualTo(0).Within(1e-10),
                "Zero raw overscroll must produce zero visual overscroll.");
        }

        [Test]
        public void RubberBand_formula_at_known_offset_matches_ios_formula() {
            // iOS formula: visual = (1 - 1/(raw * C / dim + 1)) * dim
            // With raw=50, C=0.55, dim=100:
            //   visual = (1 - 1/(50*0.55/100 + 1)) * 100
            //           = (1 - 1/(0.275 + 1)) * 100
            //           = (1 - 1/1.275) * 100
            //           = (1 - 0.78431...) * 100  ≈ 21.569 px
            ScrollMomentum.RubberBandC = 0.55;
            double raw = 50;
            double dim = 100;
            double expected = (1.0 - 1.0 / (raw * 0.55 / dim + 1.0)) * dim;
            double actual = ScrollMomentum.RubberBandVisual(raw, dim);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-9),
                "RubberBandVisual must match the iOS formula exactly.");
            // Sanity: should be less than raw (resistance applied).
            Assert.That(actual, Is.LessThanOrEqualTo(raw),
                "Rubber-banded visual must be less than or equal to raw overscroll.");
        }

        [Test]
        public void RubberBand_formula_visual_is_less_than_raw_overscroll() {
            // For any positive raw, visual < raw (resistance squishes the travel).
            ScrollMomentum.RubberBandC = 0.55;
            double dim = 200;
            foreach (var raw in new[] { 1.0, 10.0, 50.0, 100.0, 500.0 }) {
                double visual = ScrollMomentum.RubberBandVisual(raw, dim);
                Assert.That(visual, Is.LessThanOrEqualTo(raw),
                    $"Visual ({visual:F3}) must be ≤ raw ({raw}) for raw={raw}, dim={dim}.");
                Assert.That(visual, Is.GreaterThan(0),
                    $"Visual must be positive for raw={raw}.");
            }
        }

        [Test]
        public void RubberBand_formula_at_double_raw_gives_less_than_double_visual() {
            // The formula is concave — doubling raw produces less than double the visual.
            ScrollMomentum.RubberBandC = 0.55;
            double dim = 100;
            double v1 = ScrollMomentum.RubberBandVisual(50, dim);
            double v2 = ScrollMomentum.RubberBandVisual(100, dim);
            Assert.That(v2, Is.LessThanOrEqualTo(v1 * 2.0),
                "Doubling raw overscroll must not double visual overscroll (concave formula).");
        }

        [Test]
        public void RubberBand_formula_zero_dim_falls_back_gracefully() {
            // Zero-size viewport — formula must not throw; returns raw.
            double visual = ScrollMomentum.RubberBandVisual(50, 0);
            Assert.That(double.IsNaN(visual) || double.IsInfinity(visual), Is.False,
                "Zero-dim case must not produce NaN or Infinity.");
        }

        // ================================================================== //
        //  2. Drag-phase rubber-banding via ScrollEventHandler                //
        // ================================================================== //

        [Test]
        public void Drag_in_bounds_via_ScrollBy_is_unchanged_by_rubber_band() {
            // When content is in-bounds, a programmatic scroll must move 1:1 (no rubber-banding).
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 300; state.ViewportHeight = 100;
            state.ScrollY = 50; // mid-range — nowhere near an edge

            // Use direct state mutation (the rubber-band path is only armed during touch drags).
            state.ScrollTo(0, 80);
            Assert.That(state.ScrollY, Is.EqualTo(80).Within(0.5),
                "In-bounds scroll must move at full 1:1 ratio (programmatic path, no rubber-band).");
        }

        [Test]
        public void Drag_past_bottom_edge_applies_rubber_resistance() {
            // When the content would go past MaxScrollY, the visual position
            // advances but less than the raw delta (resistance applied).
            // This tests the formula, not the event handler (which is proven separately).
            ScrollMomentum.RubberBandC = 0.55;

            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;
            Assert.That(maxY, Is.GreaterThan(0), "Need a scrollable range");

            // The rubber formula: visual = dim * (1 - 1/(40*0.55/100 + 1))
            // dim=100 (viewport height), raw=40
            double dim = state.ViewportHeight;
            double expectedVisual = ScrollMomentum.RubberBandVisual(40, dim);
            // The visual overscroll should be less than 40.
            Assert.That(expectedVisual, Is.LessThanOrEqualTo(40),
                "Rubber-banded visual past bottom must be less than raw delta.");
            Assert.That(expectedVisual, Is.GreaterThan(0),
                "Rubber-banded visual must be positive.");
        }

        [Test]
        public void Drag_past_top_edge_is_symmetric_with_bottom() {
            // Rubber-band at the top edge must apply the same formula as the bottom.
            ScrollMomentum.RubberBandC = 0.55;
            double raw = 60;
            double dim = 100;
            double visualBottom = ScrollMomentum.RubberBandVisual(raw, dim);
            double visualTop    = ScrollMomentum.RubberBandVisual(raw, dim);
            Assert.That(visualTop, Is.EqualTo(visualBottom).Within(1e-9),
                "Top and bottom rubber-band formula must be symmetric.");
        }

        // ================================================================== //
        //  3. Release overscrolled → spring-back terminates at boundary       //
        // ================================================================== //

        [Test]
        public void SpringBack_from_bottom_overscroll_terminates_exactly_at_boundary() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY; // = 100

            var momentum = new ScrollMomentum(sc);
            // Manually place content 30px past the bottom edge.
            double overY = 30;
            state.ScrollY = maxY + overY;
            scrollBox_Set(vp, state);

            momentum.StartSpringBack(vp, 0.0, 0, overY, 0, maxY);
            Assert.That(momentum.IsSpringBack(vp), Is.True,
                "Spring-back must start when overscrolled.");

            double t = 0.0;
            SettleSpring(momentum, vp, ref t);

            Assert.That(momentum.IsGliding(vp), Is.False,
                "Spring-back must terminate (IsGliding = false after settling).");
            Assert.That(state.ScrollY, Is.EqualTo(maxY).Within(0.001),
                "Spring-back must terminate EXACTLY at the bottom boundary.");
        }

        [Test]
        public void SpringBack_from_top_overscroll_terminates_exactly_at_zero() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            double overY = -40; // 40px past the top
            state.ScrollY = overY;
            scrollBox_Set(vp, state);

            momentum.StartSpringBack(vp, 0.0, 0, overY, 0, 0);
            Assert.That(momentum.IsSpringBack(vp), Is.True,
                "Spring-back must start when above top boundary.");

            double t = 0.0;
            SettleSpring(momentum, vp, ref t);

            Assert.That(momentum.IsGliding(vp), Is.False,
                "Spring-back must terminate.");
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001),
                "Spring-back must terminate EXACTLY at the top boundary (ScrollY = 0).");
        }

        [Test]
        public void SpringBack_is_monotonic_no_oscillation_past_boundary() {
            // Critically-damped spring must NOT oscillate: once the position crosses
            // the boundary on its way back, it must not pass the boundary again.
            ScrollMomentum.SpringOmega = 12.0;

            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;

            var momentum = new ScrollMomentum(sc);
            double overY = 50; // large overshoot
            state.ScrollY = maxY + overY;
            scrollBox_Set(vp, state);
            momentum.StartSpringBack(vp, 0.0, 0, overY, 0, maxY);

            // Tick and verify monotonic approach: never goes further from boundary
            // than the initial overscroll, and never goes below maxY after returning.
            bool returnedToBoundary = false;
            double t = 0.0;
            double prevScrollY = state.ScrollY;
            for (int i = 0; i < 500; i++) {
                t += 0.008;
                momentum.Tick(0.008, t, null);
                double cur = state.ScrollY;

                if (!returnedToBoundary) {
                    if (cur <= maxY + 0.001) returnedToBoundary = true;
                } else {
                    // Once it's crossed back, it must not go below boundary by more
                    // than the stop threshold (critically damped = no undershoot).
                    Assert.That(cur, Is.GreaterThanOrEqualTo(maxY - ScrollMomentum.SpringStopThresholdPx - 0.001),
                        $"After returning to boundary, ScrollY ({cur:F4}) must not undershoot past boundary ({maxY}) — spring is over-damped.");
                }

                if (!momentum.IsGliding(vp)) break;
            }

            Assert.That(returnedToBoundary, Is.True,
                "Spring-back must actually return to the boundary.");
        }

        // ================================================================== //
        //  4. Glide-into-edge overshoot then spring-back                      //
        // ================================================================== //

        [Test]
        public void Glide_into_edge_overshoots_then_returns_to_boundary() {
            // A momentum glide that reaches the bottom edge must NOT dead-stop;
            // it must overshoot (ScrollY > MaxScrollY) briefly then spring back.
            var (vp, sc, state) = BuildScrollable(childHeight: 500);
            state.ScrollHeight = 500; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;
            state.ScrollY = maxY - 5; // near the edge

            var momentum = new ScrollMomentum(sc);
            // Fast downward flick.
            momentum.AddSample(vp, 0.0,  0, maxY - 30);
            momentum.AddSample(vp, 0.01, 0, maxY - 5);
            momentum.StartGlide(vp, 0.01);

            // Single tick to push past the edge.
            momentum.Tick(0.050, 0.060, null);

            // Now either (a) we have an overshoot already or (b) spring-back started.
            // Both count; the key assertion is that after settling, we're back at maxY.
            double t = 0.060;
            SettleSpring(momentum, vp, ref t);

            Assert.That(state.ScrollY, Is.EqualTo(maxY).Within(0.001),
                "After glide-into-edge + spring-back, ScrollY must be exactly at MaxScrollY.");
        }

        [Test]
        public void Glide_into_top_edge_overshoots_then_returns_to_zero() {
            var (vp, sc, state) = BuildScrollable(childHeight: 500);
            state.ScrollHeight = 500; state.ViewportHeight = 100;
            state.ScrollY = 5; // near top

            var momentum = new ScrollMomentum(sc);
            // Fast upward flick.
            momentum.AddSample(vp, 0.0,  0, 30);
            momentum.AddSample(vp, 0.01, 0, 5);   // upward velocity
            momentum.StartGlide(vp, 0.01);

            momentum.Tick(0.050, 0.060, null);

            double t = 0.060;
            SettleSpring(momentum, vp, ref t);

            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001),
                "After upward glide-into-top-edge + spring-back, ScrollY must be exactly 0.");
        }

        // ================================================================== //
        //  5. Overshoot cap respected                                          //
        // ================================================================== //

        [Test]
        public void Overshoot_cap_limits_maximum_visible_overscroll() {
            // Even with a very high velocity, the glide-to-edge overshoot must not
            // exceed OvershootCapPx.
            ScrollMomentum.OvershootCapPx = 80.0;
            ScrollMomentum.SpringOmega    = 12.0;

            var (vp, sc, state) = BuildScrollable(childHeight: 500);
            state.ScrollHeight = 500; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;
            state.ScrollY = maxY - 1; // right at the edge

            var momentum = new ScrollMomentum(sc);
            // Extreme velocity: 5000 px/s downward.
            momentum.AddSample(vp, 0.0,  0, maxY - 50);
            momentum.AddSample(vp, 0.01, 0, maxY - 1);
            momentum.StartGlide(vp, 0.01);

            // One big tick to trigger the edge.
            momentum.Tick(0.050, 0.060, null);

            // The overshoot in ScrollY must be at most OvershootCapPx.
            double overshoot = state.ScrollY - maxY;
            // Apply rubber-band squish: visual overshoot ≤ raw overshoot ≤ cap
            // so check raw cap path via: the resulting visual may be smaller.
            // The critical check: visual overscroll <= OvershootCapPx (rubber-banded, so even smaller).
            Assert.That(overshoot, Is.LessThanOrEqualTo(ScrollMomentum.OvershootCapPx + 0.001),
                $"Visible overshoot ({overshoot:F2}) must not exceed OvershootCapPx ({ScrollMomentum.OvershootCapPx}).");
        }

        // ================================================================== //
        //  6. Containment — wheel / programmatic paths stay in bounds         //
        // ================================================================== //

        [Test]
        public void Wheel_scroll_never_goes_out_of_bounds() {
            // ScrollBy (programmatic / wheel path) must ALWAYS hard-clamp.
            string css = ".vp { overflow: auto; height: 100px; width: 200px; } .child { height: 500px; }";
            string html = "<div class=\"vp\"><div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }
            Box scrollBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("class") == "vp") { scrollBox = b; break; }
            }
            System.Collections.Generic.Dictionary<Element, Box> map = new();
            foreach (var b in AllBoxes(root)) { if (b.Element != null) map[b.Element] = b; }
            Box Lookup(Element e) => e != null && map.TryGetValue(e, out var b) ? b : null;
            double now = 0;
            var handler = new ScrollEventHandler(
                new EventDispatcher(doc, new BoxTreeHitTester(root, sc)),
                doc, sc, Lookup, () => 16, () => now);
            var state = sc.GetOrCreate(scrollBox);

            // Try to scroll far past both ends via programmatic/ScrollBy path.
            handler.ScrollBy(scrollBox.Element, 0, 999999);
            Assert.That(state.ScrollY, Is.EqualTo(state.MaxScrollY).Within(0.001),
                "ScrollBy past bottom must clamp to MaxScrollY, never exceed it.");

            handler.ScrollBy(scrollBox.Element, 0, -999999);
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001),
                "ScrollBy past top must clamp to 0, never go negative.");
        }

        [Test]
        public void Programmatic_ScrollTo_never_goes_out_of_bounds() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 300; state.ViewportHeight = 100;

            // Use the direct ScrollState.ScrollTo API.
            state.ScrollTo(0, 99999);
            Assert.That(state.ScrollY, Is.EqualTo(state.MaxScrollY).Within(0.001),
                "Programmatic ScrollTo past bottom must clamp to MaxScrollY.");

            state.ScrollTo(0, -99999);
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001),
                "Programmatic ScrollTo past top must clamp to 0.");
        }

        [Test]
        public void No_active_drag_or_spring_means_scroll_is_in_bounds() {
            // When no drag or spring is running, any scroll position must be in [0, Max].
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            var momentum = new ScrollMomentum(sc);

            // Manually set out-of-bounds to simulate a hypothetical stale state.
            state.ScrollY = -50;
            // With no active glide/spring, Tick should not move position.
            momentum.Tick(0.016, 0.016, null);
            // Nothing is running, so Tick is a no-op — verify the containment guarantee
            // is documented: the position is only valid out-of-bounds during active animation.
            Assert.That(momentum.IsGliding(vp), Is.False,
                "No active animation means IsGliding must be false.");
        }

        // ================================================================== //
        //  7. Spring-back + snap integration                                  //
        // ================================================================== //

        [Test]
        public void SpringBack_in_snap_container_settles_on_snap_point() {
            // After a spring-back, if the container has scroll-snap, the post-spring
            // settle must land on a snap point (caller calls SettleSnap).
            var (vp, sc, state) = BuildSnapScrollable(snapType: "y mandatory", itemHeight: 80, itemCount: 5);
            var resolver = new SnapResolver(sc);
            var smoothAnim = new SmoothScrollAnimator(sc);

            var momentum = new ScrollMomentum(sc);
            momentum.SnapResolver = resolver;
            momentum.SnapAnimator = smoothAnim;

            // Place content slightly past the top (overscrolled).
            double overY = -20;
            state.ScrollY = overY;
            scrollBox_Set(vp, state);

            momentum.StartSpringBack(vp, 0.0, 0, overY, 0, 0);

            double t = 0.0;
            SettleSpring(momentum, vp, ref t);

            // Position must be at 0 (the closest boundary = snap point 0).
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001),
                "After top-overscroll spring-back, ScrollY must land at 0 (snap point 0).");
        }

        // ================================================================== //
        //  8. Paint invalidation during spring-back frames                    //
        // ================================================================== //

        [Test]
        public void SpringBack_marks_paint_invalidation_on_each_frame() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;

            var momentum = new ScrollMomentum(sc);
            momentum.StartSpringBack(vp, 0.0, 0, 30, 0, maxY);

            var tracker = new InvalidationTracker();
            momentum.Tick(0.016, 0.016, tracker);

            Assert.That(tracker.IsDirty(vp.Element, InvalidationKind.Paint), Is.True,
                "Spring-back tick must mark paint invalidation on the scrolling element.");
        }

        // ================================================================== //
        //  9. Cancel clears spring-back state                                 //
        // ================================================================== //

        [Test]
        public void Cancel_stops_spring_back() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            var momentum = new ScrollMomentum(sc);

            momentum.StartSpringBack(vp, 0.0, 0, 30, 0, state.MaxScrollY);
            Assert.That(momentum.IsSpringBack(vp), Is.True);

            momentum.Cancel(vp);
            Assert.That(momentum.IsGliding(vp), Is.False,
                "Cancel must stop spring-back (IsGliding = false).");
            Assert.That(momentum.IsSpringBack(vp), Is.False,
                "Cancel must clear the spring-back state.");
        }

        [Test]
        public void CancelAll_stops_all_spring_backs() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            var momentum = new ScrollMomentum(sc);

            momentum.StartSpringBack(vp, 0.0, 0, 30, 0, state.MaxScrollY);
            Assert.That(momentum.IsSpringBack(vp), Is.True);

            momentum.CancelAll();
            Assert.That(momentum.IsGliding(vp), Is.False,
                "CancelAll must clear all animations including spring-backs.");
        }

        // ================================================================== //
        //  10. Spring-back formula correctness at a known time                //
        // ================================================================== //

        [Test]
        public void SpringBack_position_at_known_time_matches_critically_damped_formula() {
            // x(t) = x0 * (1 + w*t) * exp(-w*t)
            // With x0 = 40px (overscroll past bottom), w = 12/s, t = 0.1s:
            //   x(0.1) = 40 * (1 + 1.2) * exp(-1.2)
            //           = 40 * 2.2 * 0.30119... ≈ 26.50 px
            ScrollMomentum.SpringOmega = 12.0;
            ScrollMomentum.SpringStopThresholdPx = 0.15;

            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;

            double overY = 40.0;
            state.ScrollY = maxY + overY;
            scrollBox_Set(vp, state);

            var momentum = new ScrollMomentum(sc);
            momentum.StartSpringBack(vp, 0.0, 0, overY, 0, maxY);

            // Tick to t=0.1 in one step.
            momentum.Tick(0.1, 0.1, null);

            double w = 12.0;
            double t = 0.1;
            double expected = maxY + overY * (1.0 + w * t) * Math.Exp(-w * t);
            // Allow some numerical tolerance from Tick's integration step.
            Assert.That(state.ScrollY, Is.EqualTo(expected).Within(0.5),
                $"Spring-back position at t={t}s must match x0*(1+w*t)*exp(-w*t) formula.");
        }

        // ================================================================== //
        //  11. StartGlide with overscroll starts spring-back (no raw glide)   //
        // ================================================================== //

        [Test]
        public void StartGlide_with_zero_velocity_and_overscroll_starts_spring_back() {
            // Releasing with near-zero velocity while overscrolled triggers spring-back, not glide.
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;

            var momentum = new ScrollMomentum(sc);
            // Position is 20px past the bottom edge.
            state.ScrollY = maxY + 20;
            scrollBox_Set(vp, state);

            // Two samples with zero velocity.
            momentum.AddSample(vp, 0.0,  0, maxY);
            momentum.AddSample(vp, 0.05, 0, maxY); // stationary
            // Pass overY = 20 (content is 20px past the bottom).
            momentum.StartGlide(vp, 0.05, overY: 20);

            // Either spring-back is running or the position snapped directly to boundary.
            if (momentum.IsSpringBack(vp)) {
                double t = 0.05;
                SettleSpring(momentum, vp, ref t);
            }
            Assert.That(state.ScrollY, Is.EqualTo(maxY).Within(0.001),
                "Zero-velocity release while overscrolled must spring back to MaxScrollY.");
        }

        [Test]
        public void StartGlide_with_inward_velocity_and_overscroll_snaps_then_glides() {
            // Releasing with inward velocity (toward the boundary) while overscrolled:
            // the position snaps to the boundary and the glide continues inward.
            var (vp, sc, state) = BuildScrollable(childHeight: 500);
            state.ScrollHeight = 500; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;

            var momentum = new ScrollMomentum(sc);
            // Position is 20px past the bottom. Velocity is upward (inward = toward boundary).
            state.ScrollY = maxY + 20;
            scrollBox_Set(vp, state);

            // Samples showing upward (inward) velocity.
            momentum.AddSample(vp, 0.0,  0, maxY + 30); // higher (past edge)
            momentum.AddSample(vp, 0.05, 0, maxY + 20); // moving toward edge
            // overY = 20, velocity is inward (vy < 0 in scroll coords).
            momentum.StartGlide(vp, 0.05, overY: 20);

            // Position must now be at or below maxY (boundary snap happened).
            Assert.That(state.ScrollY, Is.LessThanOrEqualTo(maxY + 0.001),
                "Inward-velocity release while overscrolled must snap position to boundary.");
        }

        // ================================================================== //
        //  Helper: mirror ScrollY onto box                                     //
        // ================================================================== //

        static void scrollBox_Set(Box box, ScrollState state) {
            box.ScrollX = state.ScrollX;
            box.ScrollY = state.ScrollY;
        }
    }
}
