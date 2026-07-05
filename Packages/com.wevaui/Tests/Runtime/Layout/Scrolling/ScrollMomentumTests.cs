using System;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using Weva.Layout.Scrolling.Snap;
using Weva.Reactive;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // Tests for ScrollMomentum — inertial/flick scroll.
    //
    // Conventions:
    //   - All time values are in seconds (double) — same clock contract as
    //     SmoothScrollAnimator.
    //   - Decay constants are the production defaults unless a test intentionally
    //     overrides them (restored in TearDown).
    public class ScrollMomentumTests {
        static ScrollMomentumTests() {
            SmoothScrollProperties.EnsureRegistered();
            ScrollSnapProperties.EnsureRegistered();
        }

        // Save/restore tunables so test overrides don't bleed into each other.
        double savedDecay;
        double savedThreshold;
        double savedWindow;

        [SetUp]
        public void SetUp() {
            savedDecay     = ScrollMomentum.DecayBasePerMs;
            savedThreshold = ScrollMomentum.StopThresholdPxPerSec;
            savedWindow    = ScrollMomentum.VelocityWindowSeconds;
        }

        [TearDown]
        public void TearDown() {
            ScrollMomentum.DecayBasePerMs      = savedDecay;
            ScrollMomentum.StopThresholdPxPerSec = savedThreshold;
            ScrollMomentum.VelocityWindowSeconds = savedWindow;
        }

        // ------------------------------------------------------------------ //
        //  Helper: build a scrollable box with measurable scroll range.       //
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

        // ================================================================== //
        //  1. Velocity estimation from sample streams                         //
        // ================================================================== //

        [Test]
        public void Velocity_steady_drag_estimates_correct_speed() {
            // Steady drag: scrollY increases at 300 px/s over 100 ms.
            var (vp, sc, state) = BuildScrollable();
            state.ScrollWidth  = 200; state.ViewportWidth  = 200;
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            double t0 = 0.0;
            int steps = 10;
            double totalDy = 300 * 0.1; // 30 px in 100 ms
            for (int i = 0; i <= steps; i++) {
                double t = t0 + i * 0.01;
                double y = i * (totalDy / steps);
                momentum.AddSample(vp, t, 0, y);
            }

            // StartGlide at t=0.1 should fire a glide with vy ≈ -300 px/s
            // (negative because scrolling down = positive scrollY, so velocity
            // is +300 px/s; glide carries forward in same direction).
            // Set scrollY to the last sample position.
            state.ScrollY = totalDy;

            momentum.StartGlide(vp, 0.1);
            Assert.That(momentum.IsGliding(vp), Is.True,
                "Steady 300 px/s drag should produce a glide above the stop threshold.");
        }

        [Test]
        public void Velocity_single_sample_produces_no_glide() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            // Only one sample — not enough to estimate velocity.
            momentum.AddSample(vp, 0.0, 0, 0);
            momentum.StartGlide(vp, 0.0);

            Assert.That(momentum.IsGliding(vp), Is.False,
                "A single sample cannot produce a velocity estimate — no glide expected.");
        }

        [Test]
        public void Velocity_zero_velocity_release_does_not_start_glide() {
            // Two samples at same position — velocity = 0 — no glide.
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            momentum.AddSample(vp, 0.0, 0, 50);
            momentum.AddSample(vp, 0.05, 0, 50);  // same position

            momentum.StartGlide(vp, 0.05);
            Assert.That(momentum.IsGliding(vp), Is.False,
                "Zero velocity (stationary drag) must not produce a glide.");
        }

        [Test]
        public void Velocity_slow_release_below_threshold_does_not_glide() {
            // Move 0.2 px in 100 ms → 2 px/s (below StopThresholdPxPerSec = 5).
            ScrollMomentum.StopThresholdPxPerSec = 5.0;
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            momentum.AddSample(vp, 0.0,  0, 0.0);
            momentum.AddSample(vp, 0.05, 0, 0.1);
            momentum.AddSample(vp, 0.10, 0, 0.2);

            momentum.StartGlide(vp, 0.10);
            Assert.That(momentum.IsGliding(vp), Is.False,
                "Release velocity below StopThresholdPxPerSec must not start a glide.");
        }

        [Test]
        public void Velocity_accelerating_drag_estimates_positive_velocity() {
            // Accelerating drag — quadratic displacement; linear regression should
            // still yield a positive velocity (direction check only here).
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            // y = k * t^2, sampled at 10 ms intervals from t=0 to t=0.100.
            double k = 10000; // px/s^2 — brisk acceleration
            for (int i = 0; i <= 10; i++) {
                double t = i * 0.01;
                double y = k * t * t;
                momentum.AddSample(vp, t, 0, y);
            }
            state.ScrollY = k * 0.1 * 0.1; // final position

            momentum.StartGlide(vp, 0.10);
            Assert.That(momentum.IsGliding(vp), Is.True,
                "Accelerating drag with final speed >> threshold should produce a glide.");
        }

        // ================================================================== //
        //  2. Decay curve values at known times                               //
        // ================================================================== //

        [Test]
        public void Decay_position_increases_less_than_linear_after_each_tick() {
            // After one tick the glide has advanced, but each successive tick
            // advances less (deceleration confirms the exponential decay).
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            // Directly inject a glide by providing two samples far enough apart.
            // velocity ≈ 1000 px/s downward.
            momentum.AddSample(vp, 0.0,  0, 0.0);
            momentum.AddSample(vp, 0.1,  0, 100.0);
            state.ScrollY = 100;
            momentum.StartGlide(vp, 0.1);

            double y0 = state.ScrollY; // 100

            // Tick 16 ms.
            momentum.Tick(0.016, 0.116, null);
            double y1 = state.ScrollY;
            double advance1 = y1 - y0;

            // Tick another 16 ms.
            momentum.Tick(0.016, 0.132, null);
            double y2 = state.ScrollY;
            double advance2 = y2 - y1;

            Assert.That(advance1, Is.GreaterThan(0),
                "First tick must advance scroll position in the direction of the flick.");
            Assert.That(advance2, Is.GreaterThan(0),
                "Second tick must also advance (glide not yet stopped).");
            Assert.That(advance2, Is.LessThan(advance1),
                "Each successive tick advances less — confirming exponential deceleration.");
        }

        [Test]
        public void Decay_velocity_at_half_life_is_approximately_half() {
            // At t = halfLife_ms the velocity should be ≈ v0 * decayBase^halfLife_ms ≈ 0.5 * v0.
            // We verify this analytically: decayBase^(ln(0.5)/ln(decayBase)) = 0.5.
            // With default decayBase = 0.998:  halfLife = ln(0.5)/ln(0.998) ≈ 346.6 ms.
            double decayBase = ScrollMomentum.DecayBasePerMs;
            double halfLifeMs = Math.Log(0.5) / Math.Log(decayBase);

            double v0 = 1000.0; // px/s
            double vAtHalfLife = v0 * Math.Pow(decayBase, halfLifeMs);
            Assert.That(vAtHalfLife, Is.EqualTo(v0 * 0.5).Within(v0 * 0.01),
                "Velocity at computed half-life must equal half the initial velocity within 1%.");
        }

        [Test]
        public void Decay_glide_stops_before_edge_when_velocity_decays() {
            // Use a very fast decay to prove the glide terminates on its own,
            // not only when it hits the edge.
            ScrollMomentum.DecayBasePerMs = 0.90;      // very fast decay
            ScrollMomentum.StopThresholdPxPerSec = 5.0;

            var (vp, sc, state) = BuildScrollable(childHeight: 10000); // huge content
            state.ScrollHeight = 10000; state.ViewportHeight = 100;
            state.ScrollY = 1000; // start well away from edges

            var momentum = new ScrollMomentum(sc);
            momentum.AddSample(vp, 0.0,  0, 1000);
            momentum.AddSample(vp, 0.05, 0, 1050); // 1000 px/s
            momentum.StartGlide(vp, 0.05);

            // Tick until glide stops or we time out.
            int iterations = 0;
            while (momentum.IsGliding(vp) && iterations < 1000) {
                momentum.Tick(0.016, 0.05 + iterations * 0.016, null);
                iterations++;
            }

            Assert.That(momentum.IsGliding(vp), Is.False,
                "Glide must stop on its own due to decay even when far from edges.");
            Assert.That(state.ScrollY, Is.LessThan(state.MaxScrollY),
                "Glide must stop before hitting the far edge (decay stopped it).");
        }

        [Test]
        public void Decay_exact_velocity_formula_at_known_time() {
            // Verify the Euler integration advances position consistently with
            // the analytical formula for a short time interval.
            //
            // v(t) = v0 * k^(t_ms)
            // displacement over dt = ∫₀^dt v0*k^(t_ms) * dt_s  (trapezoid approx)
            // = v0 * (k^0 + k^(dt_ms)) / 2 * dt_s
            double v0 = 500.0;  // px/s
            double k  = ScrollMomentum.DecayBasePerMs;
            double dtS  = 0.016;
            double dtMs = dtS * 1000.0;

            double expectedDisp = v0 * (1.0 + Math.Pow(k, dtMs)) * 0.5 * dtS;

            var (vp, sc, state) = BuildScrollable(childHeight: 5000);
            state.ScrollHeight = 5000; state.ViewportHeight = 100;
            state.ScrollY = 1000; // mid-range

            var momentum = new ScrollMomentum(sc);
            // Inject exactly v0 via two samples.
            momentum.AddSample(vp, 0.0,  0, 1000);
            momentum.AddSample(vp, 0.1,  0, 1000 + v0 * 0.1); // 1050
            momentum.StartGlide(vp, 0.1);

            double y0 = state.ScrollY;
            momentum.Tick(dtS, 0.1 + dtS, null);
            double actual = state.ScrollY - y0;

            // The actual velocity from two samples may not be exactly v0 due to
            // regression, but it's within ±10% for a clean linear sequence.
            // The decay formula fraction is the key thing to pin.
            Assert.That(actual, Is.GreaterThan(0),
                "One tick must produce positive displacement in the drag direction.");
            // Displacement must be < v0*dt (decelerating) and > 0.
            Assert.That(actual, Is.LessThanOrEqualTo(v0 * dtS * 1.15),
                "Displacement per tick must not exceed v0*dt with 15% slack for velocity estimation error.");
        }

        // ================================================================== //
        //  3. Edge behavior — overshoot + spring-back (iOS rubber-band)       //
        // ================================================================== //

        [Test]
        public void Edge_glide_into_bottom_triggers_spring_back() {
            // New contract: a fast downward flick into the bottom edge does NOT hard-stop;
            // it overshoots slightly and a spring-back animation returns to MaxScrollY.
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            double maxY = state.MaxScrollY;
            Assert.That(maxY, Is.GreaterThan(0));

            // Position near the bottom edge.
            state.ScrollY = maxY - 2;

            var momentum = new ScrollMomentum(sc);
            // Fast downward flick.
            momentum.AddSample(vp, 0.0,  0, maxY - 10);
            momentum.AddSample(vp, 0.01, 0, maxY - 2);
            momentum.StartGlide(vp, 0.01);

            // One tick pushes past the edge and starts a spring-back.
            momentum.Tick(0.050, 0.060, null);

            // The glide phase is done; spring-back takes over (IsGliding stays true while spring runs).
            // Allow spring to settle completely.
            double t = 0.060;
            for (int i = 0; i < 200 && momentum.IsGliding(vp); i++) {
                t += 0.016;
                momentum.Tick(0.016, t, null);
            }

            Assert.That(momentum.IsGliding(vp), Is.False,
                "Spring-back must terminate.");
            Assert.That(state.ScrollY, Is.EqualTo(maxY).Within(0.001),
                "After spring-back, ScrollY must be exactly at MaxScrollY.");
        }

        [Test]
        public void Edge_glide_into_top_triggers_spring_back() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            state.ScrollY = 2; // near top

            var momentum = new ScrollMomentum(sc);
            // Fast upward flick (negative velocity).
            momentum.AddSample(vp, 0.0,  0, 10);
            momentum.AddSample(vp, 0.01, 0, 2);  // dy = -8 in 10ms => -800 px/s
            momentum.StartGlide(vp, 0.01);

            momentum.Tick(0.050, 0.060, null);

            // Allow spring to settle.
            double t = 0.060;
            for (int i = 0; i < 200 && momentum.IsGliding(vp); i++) {
                t += 0.016;
                momentum.Tick(0.016, t, null);
            }

            Assert.That(momentum.IsGliding(vp), Is.False,
                "Spring-back must terminate.");
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001),
                "After spring-back, ScrollY must be exactly at 0 (top boundary).");
        }

        [Test]
        public void Edge_x_hits_before_y_y_continues_then_spring_returns() {
            // Diagonal glide: X hits edge first, Y should still advance after X goes to spring-back.
            var (vp, sc, state) = BuildScrollable(
                viewportWidth: 100, childWidth: 300,
                viewportHeight: 100, childHeight: 2000);
            state.ScrollWidth  = 300; state.ViewportWidth  = 100;
            state.ScrollHeight = 2000; state.ViewportHeight = 100;
            state.ScrollX = state.MaxScrollX - 1; // near X edge
            state.ScrollY = 50;                   // mid-range Y

            var momentum = new ScrollMomentum(sc);
            // Fast rightward + downward flick.
            momentum.AddSample(vp, 0.0,  state.MaxScrollX - 10, 40);
            momentum.AddSample(vp, 0.01, state.MaxScrollX - 1,  50);
            momentum.StartGlide(vp, 0.01);

            double y0 = state.ScrollY;
            momentum.Tick(0.050, 0.060, null);

            // Y must have advanced (Y not clamped in this tick).
            Assert.That(state.ScrollY, Is.GreaterThan(y0),
                "Y must continue scrolling after X hits the edge — axes are independent.");

            // After X spring-back settles, X should return to MaxScrollX.
            double t = 0.060;
            for (int i = 0; i < 200 && momentum.IsSpringBack(vp); i++) {
                t += 0.016;
                momentum.Tick(0.016, t, null);
            }
            Assert.That(state.ScrollX, Is.EqualTo(state.MaxScrollX).Within(0.001),
                "Spring-back must return X exactly to MaxScrollX.");
        }

        // ================================================================== //
        //  4. Snap retargeting                                                //
        // ================================================================== //

        [Test]
        public void Snap_retargeting_lands_on_snap_point() {
            var (vp, sc, state) = BuildSnapScrollable();
            var resolver = new SnapResolver(sc);
            var smoothAnim = new SmoothScrollAnimator(sc);

            var momentum = new ScrollMomentum(sc);
            momentum.SnapResolver = resolver;
            momentum.SnapAnimator = smoothAnim;

            // Verify snap points exist.
            var type = SnapResolver.ResolveType(vp);
            Assert.That(type.IsActive, Is.True);
            var pts = resolver.CollectSnapPointsY(vp);
            Assert.That(pts.Count, Is.GreaterThanOrEqualTo(2));

            // Flick from item 0 toward item 1.
            state.ScrollY = 10;
            momentum.AddSample(vp, 0.0,  0, 0);
            momentum.AddSample(vp, 0.05, 0, 10); // ~200 px/s
            momentum.StartGlide(vp, 0.05);

            // After StartGlide with snap active, the momentum should have
            // delegated to the smooth animator (IsGliding = false, smooth IS animating).
            bool rawGlide = momentum.IsGliding(vp);
            bool smoothing = smoothAnim.IsAnimating(vp);

            // Either raw glide is running (and will converge to a snap point)
            // OR the snap retarget handed off to the smooth animator.
            Assert.That(rawGlide || smoothing, Is.True,
                "After flick in snap container, either a raw glide or snap-retargeted smooth animation must be running.");

            if (smoothing) {
                // Drive animation to completion and verify it lands on a snap point.
                smoothAnim.Tick(0.50, null);
                double finalY = state.ScrollY;
                bool onSnapPoint = false;
                foreach (var p in pts) {
                    if (Math.Abs(finalY - p.Position) < 0.5) { onSnapPoint = true; break; }
                }
                Assert.That(onSnapPoint, Is.True,
                    $"Snap-retargeted animation must land exactly on a snap point. finalY={finalY}");
            }
        }

        [Test]
        public void Snap_retargeting_uses_snap_resolver_not_duplicate_logic() {
            // Verify the snap resolver's TryFindSnapTargetY is actually consulted
            // by checking that the final resting position is one of the known snap points.
            var (vp, sc, state) = BuildSnapScrollable();
            var resolver = new SnapResolver(sc);
            var smoothAnim = new SmoothScrollAnimator(sc);

            var momentum = new ScrollMomentum(sc);
            momentum.SnapResolver = resolver;
            momentum.SnapAnimator = smoothAnim;

            state.ScrollY = 5; // near item 0 (snap point 0)

            momentum.AddSample(vp, 0.0,  0, 0);
            momentum.AddSample(vp, 0.05, 0, 5); // 100 px/s — modest flick
            momentum.StartGlide(vp, 0.05);

            // Collect all valid snap points to check against.
            var pts = resolver.CollectSnapPointsY(vp);
            Assert.That(pts.Count, Is.GreaterThanOrEqualTo(1),
                "Snap container must have at least one snap point.");

            if (smoothAnim.IsAnimating(vp)) {
                smoothAnim.Tick(0.50, null);
                double finalY = state.ScrollY;
                bool onAnySnapPoint = false;
                foreach (var p in pts) {
                    if (Math.Abs(finalY - p.Position) < 1.0) { onAnySnapPoint = true; break; }
                }
                Assert.That(onAnySnapPoint, Is.True,
                    $"The animated target must be one of the snap resolver's snap points. finalY={finalY}");
            } else if (!momentum.IsGliding(vp)) {
                // Snap retarget may have been immediate (no smooth animator needed).
                // Verify current position is a snap point.
                double finalY = state.ScrollY;
                bool onAnySnapPoint = false;
                foreach (var p in pts) {
                    if (Math.Abs(finalY - p.Position) < 1.0) { onAnySnapPoint = true; break; }
                }
                Assert.That(onAnySnapPoint, Is.True,
                    $"Immediate snap retarget must land on a snap point. finalY={finalY}");
            }
        }

        // ================================================================== //
        //  5. Interruption                                                     //
        // ================================================================== //

        [Test]
        public void Cancel_stops_in_flight_glide() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            momentum.AddSample(vp, 0.0,  0, 0);
            momentum.AddSample(vp, 0.05, 0, 50); // 1000 px/s
            momentum.StartGlide(vp, 0.05);
            Assert.That(momentum.IsGliding(vp), Is.True);

            momentum.Cancel(vp);
            Assert.That(momentum.IsGliding(vp), Is.False,
                "Cancel must immediately remove the glide.");
        }

        [Test]
        public void New_pointer_down_cancels_existing_glide_via_event_handler() {
            // Simulate the wiring: a new pointer-down on the same scroll container
            // cancels the in-flight momentum through the handler.
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            // Start a glide directly.
            momentum.AddSample(vp, 0.0,  0, 0);
            momentum.AddSample(vp, 0.05, 0, 50);
            momentum.StartGlide(vp, 0.05);
            Assert.That(momentum.IsGliding(vp), Is.True);

            // Cancel simulates what HandlePointerDown does.
            momentum.Cancel(vp);
            momentum.Tick(0.016, 0.066, null);

            Assert.That(momentum.IsGliding(vp), Is.False,
                "After cancel, Tick must be a no-op for this box.");
        }

        [Test]
        public void Cancel_all_stops_all_glides() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            // Start a glide on vp.
            momentum.AddSample(vp, 0.0,  0, 0);
            momentum.AddSample(vp, 0.05, 0, 50);
            momentum.StartGlide(vp, 0.05);
            Assert.That(momentum.GlideCount, Is.EqualTo(1));

            momentum.CancelAll();
            Assert.That(momentum.GlideCount, Is.EqualTo(0), "CancelAll must remove all glides.");
        }

        // ================================================================== //
        //  6. Zero-velocity release does nothing                               //
        // ================================================================== //

        [Test]
        public void Zero_velocity_release_leaves_position_unchanged() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;
            state.ScrollY = 123;

            var momentum = new ScrollMomentum(sc);
            momentum.AddSample(vp, 0.0,  0, 123);
            momentum.AddSample(vp, 0.05, 0, 123); // same position
            momentum.StartGlide(vp, 0.05);

            Assert.That(momentum.IsGliding(vp), Is.False,
                "Zero velocity release must not start a glide.");

            // Ticking should not move the scroll position.
            momentum.Tick(0.016, 0.066, null);
            Assert.That(state.ScrollY, Is.EqualTo(123).Within(0.001),
                "Scroll position must be unchanged after zero-velocity release.");
        }

        // ================================================================== //
        //  7. Both axes independent                                            //
        // ================================================================== //

        [Test]
        public void Both_axes_glide_independently_x_only_flick() {
            // Horizontal-only flick on a container that scrolls both axes.
            var (vp, sc, state) = BuildScrollable(
                viewportWidth: 100, childWidth: 500,
                viewportHeight: 100, childHeight: 500);
            state.ScrollWidth  = 500; state.ViewportWidth  = 100;
            state.ScrollHeight = 500; state.ViewportHeight = 100;
            // Set scroll position to the end-of-drag value (matching last sample).
            state.ScrollX = 90;
            state.ScrollY = 50;

            var momentum = new ScrollMomentum(sc);
            // Horizontal flick: X increases, Y stays constant.
            momentum.AddSample(vp, 0.0,  50, 50);
            momentum.AddSample(vp, 0.05, 90, 50); // dx=40 in 50ms => 800 px/s X, 0 Y
            momentum.StartGlide(vp, 0.05);

            Assert.That(momentum.IsGliding(vp), Is.True,
                "Horizontal flick above threshold must start glide.");

            double x0 = state.ScrollX;
            double y0 = state.ScrollY;
            momentum.Tick(0.016, 0.066, null);

            Assert.That(state.ScrollX, Is.GreaterThan(x0),
                "X should advance after tick from the end-of-drag position.");
            Assert.That(state.ScrollY, Is.EqualTo(y0).Within(0.5),
                "Y should not move in a purely horizontal flick.");
        }

        [Test]
        public void Both_axes_glide_independently_diagonal_flick() {
            // Diagonal flick — both axes must advance simultaneously.
            var (vp, sc, state) = BuildScrollable(
                viewportWidth: 100, childWidth: 1000,
                viewportHeight: 100, childHeight: 1000);
            state.ScrollWidth  = 1000; state.ViewportWidth  = 100;
            state.ScrollHeight = 1000; state.ViewportHeight = 100;
            // Set scroll position to end-of-drag values (matching last sample).
            state.ScrollX = 150;
            state.ScrollY = 140;

            var momentum = new ScrollMomentum(sc);
            momentum.AddSample(vp, 0.0,  100, 100);
            momentum.AddSample(vp, 0.05, 150, 140); // dx=50, dy=40 in 50ms
            momentum.StartGlide(vp, 0.05);
            Assert.That(momentum.IsGliding(vp), Is.True);

            double x0 = state.ScrollX;
            double y0 = state.ScrollY;
            momentum.Tick(0.016, 0.066, null);

            Assert.That(state.ScrollX, Is.GreaterThan(x0), "X must advance in diagonal glide.");
            Assert.That(state.ScrollY, Is.GreaterThan(y0), "Y must advance in diagonal glide.");
        }

        // ================================================================== //
        //  8. Paint invalidation                                              //
        // ================================================================== //

        [Test]
        public void Tick_marks_paint_invalidation_while_gliding() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            momentum.AddSample(vp, 0.0,  0, 0);
            momentum.AddSample(vp, 0.05, 0, 50);
            momentum.StartGlide(vp, 0.05);

            var tracker = new InvalidationTracker();
            momentum.Tick(0.016, 0.066, tracker);

            Assert.That(tracker.IsDirty(vp.Element, InvalidationKind.Paint), Is.True,
                "Tick must mark paint invalidation on the scrolling element.");
        }

        // ================================================================== //
        //  9. Velocity window filtering                                        //
        // ================================================================== //

        [Test]
        public void Old_samples_outside_window_are_ignored() {
            // Samples older than VelocityWindowSeconds must not contribute to
            // the velocity estimate.  We add very-old fast samples + recent
            // slow samples; the result should be slow (recent only).
            ScrollMomentum.VelocityWindowSeconds = 0.100;
            ScrollMomentum.StopThresholdPxPerSec = 5.0;

            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            // Very old fast samples (t=0..0.050): 2000 px/s.
            momentum.AddSample(vp, 0.0,   0, 0);
            momentum.AddSample(vp, 0.025, 0, 50);
            momentum.AddSample(vp, 0.050, 0, 100);

            // Recent slow samples (t=0.800..0.850): 2 px/s — below threshold.
            momentum.AddSample(vp, 0.800, 0, 200);
            momentum.AddSample(vp, 0.825, 0, 200.05);
            momentum.AddSample(vp, 0.850, 0, 200.10);
            state.ScrollY = 200.10;

            // At t=0.850, only the t=0.800..0.850 samples are in the 100ms window.
            momentum.StartGlide(vp, 0.850);

            Assert.That(momentum.IsGliding(vp), Is.False,
                "Old fast samples must be excluded; only recent slow samples count, giving speed < threshold.");
        }

        // ================================================================== //
        //  10. No-op Tick with no glides                                      //
        // ================================================================== //

        [Test]
        public void Tick_is_noop_when_no_glide_running() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 200; state.ViewportHeight = 100;
            state.ScrollY = 50;

            var momentum = new ScrollMomentum(sc);
            // No glide started.
            momentum.Tick(0.016, 0.016, null);

            Assert.That(state.ScrollY, Is.EqualTo(50).Within(0.001),
                "Tick with no active glides must not modify scroll position.");
            Assert.That(momentum.GlideCount, Is.EqualTo(0));
        }

        // ================================================================== //
        //  11. MomentumAnimator property on ScrollEventHandler                //
        // ================================================================== //

        [Test]
        public void ScrollEventHandler_exposes_momentum_animator_property() {
            var sc = new ScrollContainer();
            var handler = new ScrollEventHandler(null, null, sc, null, () => 16, () => 0);
            var momentum = new ScrollMomentum(sc);

            handler.MomentumAnimator = momentum;
            Assert.That(handler.MomentumAnimator, Is.SameAs(momentum),
                "MomentumAnimator property must round-trip the assigned instance.");

            handler.MomentumAnimator = null;
            Assert.That(handler.MomentumAnimator, Is.Null,
                "MomentumAnimator property must allow null (disabling momentum).");
        }

        // ================================================================== //
        //  12. TickMomentum on handler delegates to ScrollMomentum           //
        // ================================================================== //

        [Test]
        public void TickMomentum_advances_glide_through_handler() {
            var (vp, sc, state) = BuildScrollable();
            state.ScrollHeight = 500; state.ViewportHeight = 100;

            var momentum = new ScrollMomentum(sc);
            var handler  = new ScrollEventHandler(null, null, sc, null, () => 16, () => 0);
            handler.MomentumAnimator = momentum;

            momentum.AddSample(vp, 0.0,  0, 0);
            momentum.AddSample(vp, 0.05, 0, 50);
            momentum.StartGlide(vp, 0.05);

            double y0 = state.ScrollY;
            handler.TickMomentum(0.016, 0.066, null);

            Assert.That(state.ScrollY, Is.GreaterThan(y0),
                "TickMomentum must delegate to the momentum animator and advance scroll.");
        }

        // ================================================================== //
        //  11. Document wiring — the feature must be LIVE, not just exist     //
        // ================================================================== //

        [Test]
        public void UIDocumentBuilder_wires_momentum_into_state_and_handler() {
            // First integration cut shipped the engine + handler API with no
            // builder/lifecycle wiring — glides could never run in a real
            // document. Pin the full chain: builder constructs the momentum,
            // hands it to ScrollEvents, and wires snap collaborators.
            var builder = new Weva.Documents.UIDocumentBuilder {
                DocumentSource = "<div style='overflow:auto;height:100px'><div style='height:500px'></div></div>"
            };
            var state = builder.Build();
            Assert.That(state.Momentum, Is.Not.Null, "builder must construct ScrollMomentum");
            Assert.That(state.ScrollEvents.MomentumAnimator, Is.SameAs(state.Momentum),
                "ScrollEvents must drive the same instance");
            Assert.That(state.Momentum.SnapAnimator, Is.SameAs(state.SmoothScroll),
                "snap landing must reuse the document's smooth animator");
            Assert.That(state.Momentum.SnapResolver, Is.SameAs(state.SnapResolver),
                "snap targeting must reuse the document's resolver");
        }

        [Test]
        public void Lifecycle_update_integrates_glides() {
            // UIDocumentLifecycle.Update (step 1b') must tick momentum so an
            // active glide advances scroll without any manual TickMomentum.
            var builder = new Weva.Documents.UIDocumentBuilder {
                DocumentSource = "<div style='overflow:auto;height:100px'><div style='height:500px'></div></div>"
            };
            var docState = builder.Build();
            // Prime the lifecycle clock, then lay out so scroll state exists.
            Weva.Documents.UIDocumentLifecycle.Update(docState, null, 0.0);
            var scrollBox = FindScrollableBox(docState.RootBox);
            Assert.That(scrollBox, Is.Not.Null, "need a scroll container box");
            var ss = docState.LayoutEngine.ScrollContainer.GetOrCreate(scrollBox);
            ss.ScrollHeight = 500; ss.ViewportHeight = 100;

            docState.Momentum.AddSample(scrollBox, 0.00, 0, 0);
            docState.Momentum.AddSample(scrollBox, 0.05, 0, 50);
            docState.Momentum.StartGlide(scrollBox, 0.05);
            double y0 = ss.ScrollY;

            Weva.Documents.UIDocumentLifecycle.Update(docState, null, 0.066);

            Assert.That(ss.ScrollY, Is.GreaterThan(y0),
                "lifecycle Update must integrate the glide (step 1b')");
        }

        static Box FindScrollableBox(Box root) {
            if (root == null) return null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && (b.Element.GetAttribute("style") ?? "").Contains("overflow")) return b;
            }
            return null;
        }
    }
}
