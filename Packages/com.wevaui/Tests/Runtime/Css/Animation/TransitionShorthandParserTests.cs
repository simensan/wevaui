using System.Collections.Generic;
using NUnit.Framework;
using Weva.Animation;
using Weva.Css.Animation;

namespace Weva.Tests.Css.Animation {
    public class TransitionShorthandParserTests {
        const double Eps = 1e-6;

        [Test]
        public void Empty_string_returns_empty_list() {
            var specs = TransitionShorthandParser.Parse("");
            Assert.That(specs.Count, Is.EqualTo(0));
        }

        [Test]
        public void None_returns_empty_list() {
            Assert.That(TransitionShorthandParser.Parse("none").Count, Is.EqualTo(0));
        }

        [Test]
        public void Property_then_duration() {
            var s = TransitionShorthandParser.Parse("background-color 0.2s");
            Assert.That(s.Count, Is.EqualTo(1));
            Assert.That(s[0].Property, Is.EqualTo("background-color"));
            Assert.That(s[0].DurationSeconds, Is.EqualTo(0.2).Within(Eps));
            Assert.That(s[0].DelaySeconds, Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Duration_in_milliseconds_converts_to_seconds() {
            var s = TransitionShorthandParser.Parse("color 200ms");
            Assert.That(s[0].DurationSeconds, Is.EqualTo(0.2).Within(Eps));
        }

        [Test]
        public void Duration_first_then_easing_then_property() {
            var s = TransitionShorthandParser.Parse("200ms ease background-color");
            Assert.That(s[0].Property, Is.EqualTo("background-color"));
            Assert.That(s[0].DurationSeconds, Is.EqualTo(0.2).Within(Eps));
            Assert.That(s[0].Easing, Is.SameAs(EaseEasing.Instance));
        }

        [Test]
        public void Property_duration_easing_delay() {
            var s = TransitionShorthandParser.Parse("background-color 0.2s ease 0.5s");
            Assert.That(s[0].Property, Is.EqualTo("background-color"));
            Assert.That(s[0].DurationSeconds, Is.EqualTo(0.2).Within(Eps));
            Assert.That(s[0].DelaySeconds, Is.EqualTo(0.5).Within(Eps));
            Assert.That(s[0].Easing, Is.SameAs(EaseEasing.Instance));
        }

        [Test]
        public void All_keyword() {
            var s = TransitionShorthandParser.Parse("all 0.3s ease-in");
            Assert.That(s[0].Property, Is.EqualTo("all"));
            Assert.That(s[0].DurationSeconds, Is.EqualTo(0.3).Within(Eps));
            Assert.That(s[0].Easing, Is.SameAs(EaseInEasing.Instance));
        }

        [Test]
        public void Comma_list_yields_multiple_specs() {
            var s = TransitionShorthandParser.Parse("color 100ms, background 200ms ease");
            Assert.That(s.Count, Is.EqualTo(2));
            Assert.That(s[0].Property, Is.EqualTo("color"));
            Assert.That(s[0].DurationSeconds, Is.EqualTo(0.1).Within(Eps));
            Assert.That(s[1].Property, Is.EqualTo("background"));
            Assert.That(s[1].DurationSeconds, Is.EqualTo(0.2).Within(Eps));
        }

        [Test]
        public void Cubic_bezier_easing_parses_inside_segment() {
            var s = TransitionShorthandParser.Parse("opacity 1s cubic-bezier(0.1,0.2,0.3,0.4)");
            Assert.That(s[0].DurationSeconds, Is.EqualTo(1.0).Within(Eps));
            Assert.That(s[0].Easing, Is.InstanceOf<CubicBezierEasing>());
        }

        [Test]
        public void Default_easing_is_ease_when_omitted() {
            var s = TransitionShorthandParser.Parse("opacity 1s");
            Assert.That(s[0].Easing, Is.SameAs(EaseEasing.Instance));
        }

        [Test]
        public void Default_property_is_all_when_omitted() {
            var s = TransitionShorthandParser.Parse("1s ease-out");
            Assert.That(s[0].Property, Is.EqualTo("all"));
            Assert.That(s[0].Easing, Is.SameAs(EaseOutEasing.Instance));
        }

        [Test]
        public void Steps_easing_parses() {
            var s = TransitionShorthandParser.Parse("opacity 1s steps(4, end)");
            Assert.That(s[0].Easing, Is.InstanceOf<StepsEasing>());
        }

        [Test]
        public void Linear_function_easing_parses_alongside_cubic_bezier_and_steps() {
            var lin = TransitionShorthandParser.Parse("opacity 1s linear(0, 0.5 50%, 1)");
            Assert.That(lin.Count, Is.EqualTo(1));
            Assert.That(lin[0].Property, Is.EqualTo("opacity"));
            Assert.That(lin[0].DurationSeconds, Is.EqualTo(1.0).Within(Eps));
            Assert.That(lin[0].Easing, Is.InstanceOf<PiecewiseLinearEasing>());

            var cb = TransitionShorthandParser.Parse("opacity 1s cubic-bezier(0.1,0.2,0.3,0.4)");
            Assert.That(cb[0].Easing, Is.InstanceOf<CubicBezierEasing>());

            var st = TransitionShorthandParser.Parse("opacity 1s steps(4, end)");
            Assert.That(st[0].Easing, Is.InstanceOf<StepsEasing>());
        }

        [Test]
        public void TryParseTime_handles_seconds_and_milliseconds() {
            Assert.That(TransitionShorthandParser.TryParseTime("0.5s", out double a), Is.True);
            Assert.That(a, Is.EqualTo(0.5).Within(Eps));
            Assert.That(TransitionShorthandParser.TryParseTime("250ms", out double b), Is.True);
            Assert.That(b, Is.EqualTo(0.25).Within(Eps));
            Assert.That(TransitionShorthandParser.TryParseTime("not-a-time", out double _), Is.False);
        }
    }
}
