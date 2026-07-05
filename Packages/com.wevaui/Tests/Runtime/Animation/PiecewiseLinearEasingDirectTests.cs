using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Animation;

namespace Weva.Tests.Animation {
    // Direct coverage of PiecewiseLinearEasing (TG21). The ctor and Point
    // struct are `internal`; the test assembly is granted InternalsVisibleTo
    // by Runtime/Css/Selectors/AssemblyInfo.cs so we exercise the type
    // without routing every assertion through EasingParser.
    public class PiecewiseLinearEasingDirectTests {
        const double Eps = 1e-9;

        static PiecewiseLinearEasing Make(params (double output, double input)[] pts) {
            var list = new List<PiecewiseLinearEasing.Point>(pts.Length);
            foreach (var (o, i) in pts) list.Add(new PiecewiseLinearEasing.Point(o, i));
            return new PiecewiseLinearEasing(list);
        }

        [Test]
        public void TwoPoint_identity_midpoint_is_half() {
            // linear(0, 1) at t=0.5 — straightforward linear interpolation
            // between the only two endpoints.
            var e = Make((0.0, 0.0), (1.0, 1.0));
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Waypoint_at_fifty_percent_returns_waypoint_output() {
            // linear(0, 0.25 50%, 1) at t=0.5 sits exactly on the waypoint,
            // so the output equals the waypoint's output (0.25), not the
            // straight-line average of the endpoints.
            var e = Make((0.0, 0.0), (0.25, 0.5), (1.0, 1.0));
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.25).Within(Eps));
        }

        [Test]
        public void Endpoint_inputs_return_endpoint_outputs_exactly() {
            var e = Make((0.2, 0.0), (0.4, 0.3), (0.9, 1.0));
            Assert.That(e.Evaluate(0.0), Is.EqualTo(0.2).Within(Eps));
            Assert.That(e.Evaluate(1.0), Is.EqualTo(0.9).Within(Eps));
        }

        [Test]
        public void OutOfDomain_clamps_to_endpoints_no_extrapolation() {
            // CSS Easing L2 §3.2: outside [first.Input, last.Input] the
            // function clamps; it does NOT extrapolate the adjacent segment.
            var e = Make((0.25, 0.0), (0.75, 1.0));
            Assert.That(e.Evaluate(-1.0), Is.EqualTo(0.25).Within(Eps),
                "t<0 must clamp to the first stop output, not extrapolate.");
            Assert.That(e.Evaluate(2.0), Is.EqualTo(0.75).Within(Eps),
                "t>1 must clamp to the last stop output, not extrapolate.");
        }

        [Test]
        public void Constructor_rejects_fewer_than_two_points() {
            Assert.Throws<ArgumentException>(() =>
                new PiecewiseLinearEasing(new List<PiecewiseLinearEasing.Point> {
                    new PiecewiseLinearEasing.Point(0.5, 0.5),
                }));
            Assert.Throws<ArgumentException>(() =>
                new PiecewiseLinearEasing(Array.Empty<PiecewiseLinearEasing.Point>()));
        }

        [Test]
        public void Constructor_rejects_null_points() {
            Assert.Throws<ArgumentNullException>(() => new PiecewiseLinearEasing(null));
        }

        [Test]
        public void Interpolates_linearly_within_each_segment() {
            // Two segments with different slopes — t=0.25 falls inside the
            // first segment, t=0.75 inside the second. Both should match the
            // exact slope-based formula.
            var e = Make((0.0, 0.0), (0.6, 0.5), (1.0, 1.0));
            // first segment: slope = 0.6 / 0.5 = 1.2 — at t=0.25 -> 0.3
            Assert.That(e.Evaluate(0.25), Is.EqualTo(0.30).Within(Eps));
            // second segment: slope = (1.0-0.6) / (1.0-0.5) = 0.8 — at t=0.75 -> 0.8
            Assert.That(e.Evaluate(0.75), Is.EqualTo(0.80).Within(Eps));
        }

        [Test]
        public void Adjacent_points_with_identical_input_form_step_at_boundary() {
            // PiecewiseLinearEasing.cs lines 44-45 document a `span <= 0`
            // branch that "prefers the later output" for zero-width segments.
            // In practice that branch is only reachable when the FIRST
            // duplicated-input point sits at index 0 and we evaluate at an
            // input strictly greater than the duplicated value — because the
            // forward scan stops at the FIRST b whose Input >= t, never
            // visiting the second copy. This test pins the observed
            // behaviour: at t exactly equal to the shared input we land on
            // the segment ending at the first copy (earlier output);
            // moving epsilon past it picks up the segment starting at the
            // second copy (later output). The "step" is therefore a
            // discontinuity at t = sharedInput rather than a clean
            // later-wins on the boundary point itself.
            var e = Make((0.0, 0.0), (0.4, 0.5), (0.6, 0.5), (1.0, 1.0));
            // At t=0.5 exactly: returns the earlier-output side of the step.
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.4).Within(Eps),
                "Evaluate at the shared input returns the first stop's output (earlier side of the step).");
            // Just past 0.5: continues from the later-output side (0.6 -> 1.0
            // over input [0.5, 1.0]).
            const double t = 0.5 + 1e-6;
            double expected = 0.6 + ((t - 0.5) / 0.5) * (1.0 - 0.6);
            Assert.That(e.Evaluate(t), Is.EqualTo(expected).Within(1e-6),
                "Just past the shared input the later segment takes over from the later output.");
        }

        [Test]
        public void Decreasing_outputs_are_supported() {
            // Outputs may decrease across the curve (e.g. for bounce or
            // overshoot effects). The interpolation must still be monotone
            // along the segment in question.
            var e = Make((1.0, 0.0), (0.0, 1.0));
            Assert.That(e.Evaluate(0.25), Is.EqualTo(0.75).Within(Eps));
            Assert.That(e.Evaluate(0.75), Is.EqualTo(0.25).Within(Eps));
        }

        [Test]
        public void Outputs_outside_unit_interval_are_preserved() {
            // CSS allows easing outputs outside [0,1] (overshoot/undershoot).
            // The easing function itself must NOT clamp the output range —
            // only the input domain is clamped.
            var e = Make((-0.5, 0.0), (1.5, 1.0));
            Assert.That(e.Evaluate(0.0), Is.EqualTo(-0.5).Within(Eps));
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.5).Within(Eps));
            Assert.That(e.Evaluate(1.0), Is.EqualTo(1.5).Within(Eps));
        }
    }
}
