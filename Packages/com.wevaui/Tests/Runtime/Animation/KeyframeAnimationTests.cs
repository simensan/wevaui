using System.Collections.Generic;
using NUnit.Framework;
using Weva.Animation;

namespace Weva.Tests.Animation {
    public class KeyframeAnimationTests {
        const double Eps = 1e-6;

        static Keyframe Frame(double position, params (string, string)[] props) {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in props) d[k] = v;
            return new Keyframe(position, d);
        }

        [Test]
        public void Implicit_zero_and_one_keyframes_are_synthesized() {
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0.5, ("opacity", "0.5"))
            });
            Assert.That(anim.Keyframes.Count, Is.EqualTo(3));
            Assert.That(anim.Keyframes[0].Position, Is.EqualTo(0));
            Assert.That(anim.Keyframes[2].Position, Is.EqualTo(1));
        }

        [Test]
        public void Existing_endpoints_are_not_duplicated() {
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0, ("opacity", "0")),
                Frame(1, ("opacity", "1"))
            });
            Assert.That(anim.Keyframes.Count, Is.EqualTo(2));
        }

        [Test]
        public void Tick_at_midpoint_blends_numeric_values() {
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0, ("opacity", "0")),
                Frame(1, ("opacity", "10"))
            });
            var inst = new AnimationInstance(anim, 1.0, 0, LinearEasing.Instance, 1, FillMode.None,
                PlaybackDirection.Normal, 0);
            var sample = inst.Tick(0.5);
            Assert.That(sample, Is.Not.Null);
            // Opacity flows through the typed fast path (per-anim CssNumber
            // mutated in place per Tick); sample[k] is intentionally not
            // populated to keep the hot path alloc-free. Read the typed
            // value directly and materialise on demand.
            Assert.That(inst.TypedSample.TryGetValue("opacity", out var typed), Is.True);
            Assert.That(((Weva.Css.Values.CssNumber)typed).Value, Is.EqualTo(5).Within(Eps));
        }

        [Test]
        public void IterationCount_clamps_active_window() {
            // Use `flex-grow` here (Number kind in PropertyKindRegistry) so
            // the interpolator does real numeric lerp at the midpoint. The
            // placeholder "x" used by sibling tests is Discrete by default
            // (unknown property -> step-at-0.5 in ValueInterpolator), which
            // is correct semantically but defeats this test's "halfway = 5"
            // assertion.
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0, ("flex-grow", "0")),
                Frame(1, ("flex-grow", "10"))
            });
            // Two iterations of 1s each, no fill mode = no value once finished.
            var inst = new AnimationInstance(anim, 1.0, 0, LinearEasing.Instance, 2, FillMode.None,
                PlaybackDirection.Normal, 0);
            // At t = 1.5 we are in the second iteration, halfway -> 5.
            var mid = inst.Tick(1.5);
            Assert.That(mid, Is.Not.Null);
            Assert.That(double.Parse(mid["flex-grow"], System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(5).Within(Eps));
            // At t = 2.5 the animation is finished and FillMode.None means no contribution.
            var after = inst.Tick(2.5);
            Assert.That(after, Is.Null);
        }

        [Test]
        public void FillMode_forwards_holds_final_value_after_animation_ends() {
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0, ("x", "0")),
                Frame(1, ("x", "10"))
            });
            var inst = new AnimationInstance(anim, 1.0, 0, LinearEasing.Instance, 1, FillMode.Forwards,
                PlaybackDirection.Normal, 0);
            var sample = inst.Tick(2.0);
            Assert.That(sample, Is.Not.Null);
            Assert.That(double.Parse(sample["x"], System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(10).Within(Eps));
        }

        [Test]
        public void FillMode_backwards_applies_first_frame_during_delay() {
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0, ("x", "0")),
                Frame(1, ("x", "10"))
            });
            var inst = new AnimationInstance(anim, 1.0, 1.0, LinearEasing.Instance, 1, FillMode.Backwards,
                PlaybackDirection.Normal, 0);
            // During the 1s delay, backwards fill should already report the start value.
            var sample = inst.Tick(0.5);
            Assert.That(sample, Is.Not.Null);
            Assert.That(double.Parse(sample["x"], System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void FillMode_none_returns_null_outside_active_window() {
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0, ("x", "0")),
                Frame(1, ("x", "10"))
            });
            var inst = new AnimationInstance(anim, 1.0, 0.5, LinearEasing.Instance, 1, FillMode.None,
                PlaybackDirection.Normal, 0);
            Assert.That(inst.Tick(0.25), Is.Null);
            Assert.That(inst.Tick(2.0), Is.Null);
        }

        [Test]
        public void Reverse_direction_runs_animation_backwards() {
            var anim = new KeyframeAnimation("a", new[] {
                Frame(0, ("x", "0")),
                Frame(1, ("x", "10"))
            });
            var inst = new AnimationInstance(anim, 1.0, 0, LinearEasing.Instance, 1, FillMode.None,
                PlaybackDirection.Reverse, 0);
            // At t = 0+ we are at the high end of the animation.
            var sample = inst.Tick(0.0001);
            Assert.That(sample, Is.Not.Null);
            Assert.That(double.Parse(sample["x"], System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(10).Within(0.01));
        }
    }
}
