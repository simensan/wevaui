using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Scrolling.Smooth;
using Weva.Reactive;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling.Smooth {
    public class SmoothScrollAnimatorTests {
        static SmoothScrollAnimatorTests() { SmoothScrollProperties.EnsureRegistered(); }

        static (Box vp, ScrollContainer sc, ScrollState state) BuildSmoothViewport(double childHeight = 500, string behavior = "smooth") {
            string css = $".vp {{ overflow: auto; height: 100px; width: 200px; scroll-behavior: {behavior}; }} .child {{ height: {childHeight}px; }}";
            string html = "<div class=\"vp\"><div class=\"child\"></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box vp = null;
            foreach (var b in AllBoxes(root)) {
                var c = b.Element?.GetAttribute("class");
                if (c == "vp") { vp = b; break; }
            }
            return (vp, sc, sc.Get(vp));
        }

        [Test]
        public void Programmatic_target_arrives_after_full_duration() {
            var (vp, sc, state) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 100, 0.25);
            // After full duration, scroll Y should equal target.
            anim.Tick(0.25, null);
            Assert.That(state.ScrollY, Is.EqualTo(100).Within(0.1));
            Assert.That(anim.IsAnimating(vp), Is.False);
        }

        [Test]
        public void Mid_flight_retarget_replaces_old_and_extends_duration_from_new_origin() {
            var (vp, sc, state) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 100, 0.25);
            anim.Tick(0.10, null); // partway
            double mid = state.ScrollY;
            Assert.That(mid, Is.GreaterThan(0).And.LessThan(100));

            // Retarget — origin is `mid`, full new ramp.
            anim.Animate(vp, 0, 50, 0.25);
            // Right after retarget, position should still equal mid (no tick yet).
            anim.Tick(0, null);
            Assert.That(state.ScrollY, Is.EqualTo(mid).Within(0.5));
            // After full new duration we land at 50.
            anim.Tick(0.25, null);
            Assert.That(state.ScrollY, Is.EqualTo(50).Within(0.5));
        }

        [Test]
        public void Auto_behavior_is_instant_through_handler() {
            var (vp, sc, state) = BuildSmoothViewport(behavior: "auto");
            // `IsSmooth` returns false for non-smooth; the handler routes around the animator.
            Assert.That(SmoothScrollAnimator.IsSmooth(vp), Is.False);
            var anim = new SmoothScrollAnimator(sc);
            // Animate should still work directly, but the IsSmooth flag means
            // ScrollEventHandler would NOT route through it.
            anim.Animate(vp, 0, 50, 0);
            Assert.That(state.ScrollY, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Wheel_input_on_smooth_container_animates_instead_of_jumping() {
            var (vp, sc, state) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            // Simulate the handler-driven path.
            anim.Animate(vp, 0, 80, 0.25);
            // First tick partway: state.ScrollY between 0 and 80.
            anim.Tick(0.05, null);
            Assert.That(state.ScrollY, Is.GreaterThan(0).And.LessThan(80));
        }

        [Test]
        public void Animation_respects_ease_out_curve_midpoint_above_linear() {
            var (vp, sc, state) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 100, 0.25);
            anim.Tick(0.125, null); // half the duration
            // Linear midpoint = 50; ease-out midpoint > 50.
            Assert.That(state.ScrollY, Is.GreaterThan(50));
        }

        [Test]
        public void Cancel_clears_running_state() {
            var (vp, sc, _) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 80, 0.25);
            Assert.That(anim.IsAnimating(vp), Is.True);
            anim.Cancel(vp);
            Assert.That(anim.IsAnimating(vp), Is.False);
        }

        [Test]
        public void Two_containers_animate_independently() {
            string css = ".vp { overflow: auto; height: 100px; width: 200px; scroll-behavior: smooth; } .child { height: 500px; }";
            string html = "<div><div class=\"vp\" id=\"a\"><div class=\"child\"></div></div><div class=\"vp\" id=\"b\"><div class=\"child\"></div></div></div>";
            var (root, _, _) = Build(html, css);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box a = null, b = null;
            foreach (var box in AllBoxes(root)) {
                var id = box.Element?.GetAttribute("id");
                if (id == "a") a = box;
                else if (id == "b") b = box;
            }
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(a, 0, 100, 0.25);
            anim.Animate(b, 0, 50, 0.25);
            anim.Tick(0.25, null);
            Assert.That(sc.Get(a).ScrollY, Is.EqualTo(100).Within(0.5));
            Assert.That(sc.Get(b).ScrollY, Is.EqualTo(50).Within(0.5));
        }

        [Test]
        public void Tick_marks_paint_invalidation() {
            var (vp, sc, _) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            var tracker = new InvalidationTracker();
            anim.Animate(vp, 0, 80, 0.25);
            anim.Tick(0.05, tracker);
            // The smooth animator marks the viewport's element with paint invalidation.
            Assert.That(tracker.IsDirty(vp.Element, InvalidationKind.Paint), Is.True);
        }

        [Test]
        public void Done_animation_removes_from_running_set() {
            var (vp, sc, _) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 80, 0.10);
            anim.Tick(0.05, null);
            Assert.That(anim.IsAnimating(vp), Is.True);
            anim.Tick(0.10, null);
            Assert.That(anim.IsAnimating(vp), Is.False);
            Assert.That(anim.RunningCount, Is.EqualTo(0));
        }

        [Test]
        public void Property_scroll_behavior_is_registered() {
            SmoothScrollProperties.EnsureRegistered();
            Assert.That(Weva.Css.Cascade.CssProperties.TryGet("scroll-behavior", out var p), Is.True);
            Assert.That(p.InitialValue, Is.EqualTo("auto"));
            Assert.That(p.IsInherited, Is.False);
        }

        [Test]
        public void Zero_duration_lands_target_immediately() {
            var (vp, sc, state) = BuildSmoothViewport();
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 75, 0);
            Assert.That(state.ScrollY, Is.EqualTo(75).Within(0.001));
            Assert.That(anim.IsAnimating(vp), Is.False);
        }

        // Regression: menu.html "Jump to bottom" button on .smooth-area must
        // ramp toward MaxScrollY rather than teleport — and must clamp to the
        // actual end-of-content even when callers pass PositiveInfinity.
        [Test]
        public void Jump_to_bottom_clamps_to_max_and_animates() {
            var (vp, sc, state) = BuildSmoothViewport(childHeight: 500);
            var anim = new SmoothScrollAnimator(sc);
            double max = state.MaxScrollY;
            Assert.That(max, Is.GreaterThan(0));
            anim.Animate(vp, 0, double.PositiveInfinity, 0.25);
            // Mid-flight we are not yet at max.
            anim.Tick(0.05, null);
            Assert.That(state.ScrollY, Is.GreaterThan(0).And.LessThan(max));
            // Full duration lands at clamped max, not infinity.
            anim.Tick(0.25, null);
            Assert.That(state.ScrollY, Is.EqualTo(max).Within(0.5));
            Assert.That(anim.IsAnimating(vp), Is.False);
        }

        // Regression I16: a layout shrink mid-flight (e.g. content collapsing)
        // must re-target the eased trajectory to the new MaxScrollY rather than
        // letting the per-tick clamp truncate motion at the new edge.
        [Test]
        public void Mid_flight_max_shrink_retargets_smoothly_to_new_max() {
            var (vp, sc, state) = BuildSmoothViewport(childHeight: 700);
            double originalMax = state.MaxScrollY;
            Assert.That(originalMax, Is.GreaterThan(500));
            var anim = new SmoothScrollAnimator(sc);
            anim.Animate(vp, 0, 500, 0.25);

            anim.Tick(0.10, null);
            double midBeforeShrink = state.ScrollY;
            Assert.That(midBeforeShrink, Is.GreaterThan(0).And.LessThan(500));

            // Simulate content collapse: MaxScrollY shrinks below the target.
            state.ScrollHeight = state.ViewportHeight + 300;
            double newMax = state.MaxScrollY;
            Assert.That(newMax, Is.EqualTo(300).Within(0.001));

            // Tick out the remainder of the original duration. Easing should
            // reach the clamped max at the original end-of-animation time, not
            // earlier (abrupt clamp) and not at the stale 500 target.
            anim.Tick(0.15, null);
            Assert.That(state.ScrollY, Is.EqualTo(newMax).Within(0.5));
            Assert.That(anim.IsAnimating(vp), Is.False);

            // The trajectory should not have snapped to the new max at the
            // moment of shrink — it should have continued easing from the
            // pre-shrink position toward the new max over the remaining time.
            // A partial tick after shrink should land strictly between the
            // pre-shrink position and the new max.
            var (vp2, sc2, state2) = BuildSmoothViewport(childHeight: 700);
            var anim2 = new SmoothScrollAnimator(sc2);
            anim2.Animate(vp2, 0, 500, 0.25);
            anim2.Tick(0.05, null);
            double pre = state2.ScrollY;
            Assert.That(pre, Is.GreaterThan(0).And.LessThan(300));
            state2.ScrollHeight = state2.ViewportHeight + 300;
            anim2.Tick(0.10, null);
            Assert.That(state2.ScrollY, Is.GreaterThan(pre));
            Assert.That(state2.ScrollY, Is.LessThan(300));
        }
    }
}
