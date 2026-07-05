using NUnit.Framework;
using Weva.Animation;
using Weva.Css.Animation;

namespace Weva.Tests.Css.Animation {
    public class AnimationShorthandParserTests {
        const double Eps = 1e-6;

        [Test]
        public void Empty_returns_empty_list() {
            Assert.That(AnimationShorthandParser.Parse("").Count, Is.EqualTo(0));
            Assert.That(AnimationShorthandParser.Parse("none").Count, Is.EqualTo(0));
        }

        [Test]
        public void Name_only_yields_default_zero_duration() {
            var s = AnimationShorthandParser.Parse("spin");
            Assert.That(s.Count, Is.EqualTo(1));
            Assert.That(s[0].Name, Is.EqualTo("spin"));
            Assert.That(s[0].DurationSeconds, Is.EqualTo(0).Within(Eps));
            Assert.That(s[0].IterationCount, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Name_and_duration() {
            var s = AnimationShorthandParser.Parse("spin 1s");
            Assert.That(s[0].DurationSeconds, Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Two_times_are_duration_then_delay() {
            var s = AnimationShorthandParser.Parse("spin 1s 0.5s");
            Assert.That(s[0].DurationSeconds, Is.EqualTo(1).Within(Eps));
            Assert.That(s[0].DelaySeconds, Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Iteration_count_infinite() {
            var s = AnimationShorthandParser.Parse("spin 1s infinite");
            Assert.That(double.IsPositiveInfinity(s[0].IterationCount), Is.True);
        }

        [Test]
        public void Iteration_count_numeric() {
            var s = AnimationShorthandParser.Parse("spin 1s 2");
            Assert.That(s[0].IterationCount, Is.EqualTo(2).Within(Eps));
        }

        [Test]
        public void Direction_alternate() {
            var s = AnimationShorthandParser.Parse("spin 1s alternate");
            Assert.That(s[0].Direction, Is.EqualTo(PlaybackDirection.Alternate));
        }

        [Test]
        public void Fill_mode_forwards() {
            var s = AnimationShorthandParser.Parse("spin 1s forwards");
            Assert.That(s[0].FillMode, Is.EqualTo(FillMode.Forwards));
        }

        [Test]
        public void Easing_ease_in_out() {
            var s = AnimationShorthandParser.Parse("spin 1s ease-in-out");
            Assert.That(s[0].Easing, Is.SameAs(EaseInOutEasing.Instance));
        }

        [Test]
        public void Paused_play_state() {
            var s = AnimationShorthandParser.Parse("spin 1s paused");
            Assert.That(s[0].Paused, Is.True);
        }

        [Test]
        public void Comma_list_two_animations() {
            var s = AnimationShorthandParser.Parse("a 1s, b 2s linear");
            Assert.That(s.Count, Is.EqualTo(2));
            Assert.That(s[0].Name, Is.EqualTo("a"));
            Assert.That(s[1].Name, Is.EqualTo("b"));
            Assert.That(s[1].DurationSeconds, Is.EqualTo(2).Within(Eps));
            Assert.That(s[1].Easing, Is.SameAs(LinearEasing.Instance));
        }

        [Test]
        public void Linear_function_easing_parses_alongside_cubic_bezier_and_steps() {
            var lin = AnimationShorthandParser.Parse("foo 1s linear(0, 0.5 50%, 1)");
            Assert.That(lin.Count, Is.EqualTo(1));
            Assert.That(lin[0].Name, Is.EqualTo("foo"));
            Assert.That(lin[0].DurationSeconds, Is.EqualTo(1.0).Within(Eps));
            Assert.That(lin[0].Easing, Is.InstanceOf<PiecewiseLinearEasing>());

            var cb = AnimationShorthandParser.Parse("foo 1s cubic-bezier(0.1,0.2,0.3,0.4)");
            Assert.That(cb[0].Easing, Is.InstanceOf<CubicBezierEasing>());

            var st = AnimationShorthandParser.Parse("foo 1s steps(4, end)");
            Assert.That(st[0].Easing, Is.InstanceOf<StepsEasing>());
        }

        [Test]
        public void Full_shorthand_all_components() {
            var s = AnimationShorthandParser.Parse("slide 2s ease-out 0.5s 3 alternate forwards paused");
            Assert.That(s[0].Name, Is.EqualTo("slide"));
            Assert.That(s[0].DurationSeconds, Is.EqualTo(2).Within(Eps));
            Assert.That(s[0].DelaySeconds, Is.EqualTo(0.5).Within(Eps));
            Assert.That(s[0].IterationCount, Is.EqualTo(3).Within(Eps));
            Assert.That(s[0].Direction, Is.EqualTo(PlaybackDirection.Alternate));
            Assert.That(s[0].FillMode, Is.EqualTo(FillMode.Forwards));
            Assert.That(s[0].Paused, Is.True);
            Assert.That(s[0].Easing, Is.SameAs(EaseOutEasing.Instance));
        }
    }
}
