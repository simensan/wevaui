using System;
using NUnit.Framework;
using Weva.Animation;

namespace Weva.Tests.Animation {
    public class EasingTests {
        const double Eps = 1e-3;

        [Test]
        public void Linear_endpoints_and_midpoint() {
            var e = LinearEasing.Instance;
            Assert.That(e.Evaluate(0), Is.EqualTo(0));
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.5));
            Assert.That(e.Evaluate(1), Is.EqualTo(1));
        }

        [Test]
        public void Linear_clamps_out_of_range() {
            var e = LinearEasing.Instance;
            Assert.That(e.Evaluate(-1), Is.EqualTo(0));
            Assert.That(e.Evaluate(2), Is.EqualTo(1));
        }

        [Test]
        public void Ease_is_front_loaded_above_linear_at_midpoint() {
            // CSS 'ease' = cubic-bezier(0.25, 0.1, 0.25, 1) — y > x for most of the curve.
            var e = EaseEasing.Instance;
            Assert.That(e.Evaluate(0.5), Is.GreaterThan(0.5));
            Assert.That(e.Evaluate(0), Is.EqualTo(0).Within(Eps));
            Assert.That(e.Evaluate(1), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void EaseIn_starts_slow_and_ends_at_one() {
            var e = EaseInEasing.Instance;
            Assert.That(e.Evaluate(0.1), Is.LessThan(0.1));
            Assert.That(e.Evaluate(1), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void EaseOut_starts_fast_and_ends_at_one() {
            var e = EaseOutEasing.Instance;
            Assert.That(e.Evaluate(0.1), Is.GreaterThan(0.1));
            Assert.That(e.Evaluate(1), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void EaseInOut_passes_through_half_at_half() {
            // The (0.42,0,0.58,1) curve is symmetric — exactly 0.5 at t=0.5.
            var e = EaseInOutEasing.Instance;
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.5).Within(Eps));
            Assert.That(e.Evaluate(0.1), Is.LessThan(0.1));
            Assert.That(e.Evaluate(0.9), Is.GreaterThan(0.9));
        }

        [Test]
        public void CubicBezier_reproduces_ease_with_matching_control_points() {
            var bez = new CubicBezierEasing(0.25, 0.1, 0.25, 1.0);
            var ease = EaseEasing.Instance;
            for (double t = 0; t <= 1.0001; t += 0.1) {
                Assert.That(bez.Evaluate(t), Is.EqualTo(ease.Evaluate(t)).Within(Eps),
                    $"mismatch at t={t}");
            }
        }

        [Test]
        public void CubicBezier_identity_when_control_points_match_diagonal() {
            // P1 = P2 along the y = x diagonal -> identity output.
            var bez = new CubicBezierEasing(0.5, 0.5, 0.5, 0.5);
            for (double t = 0; t <= 1.0001; t += 0.1) {
                Assert.That(bez.Evaluate(t), Is.EqualTo(t).Within(Eps));
            }
        }

        [Test]
        public void Steps_end_is_zero_at_t_zero_and_one_at_t_one() {
            var s = new StepsEasing(4, StepPosition.End);
            Assert.That(s.Evaluate(0), Is.EqualTo(0));
            Assert.That(s.Evaluate(1), Is.EqualTo(1));
            // Just before each step boundary the value remains at the lower step.
            Assert.That(s.Evaluate(0.24), Is.EqualTo(0).Within(Eps));
            Assert.That(s.Evaluate(0.26), Is.EqualTo(0.25).Within(Eps));
            Assert.That(s.Evaluate(0.74), Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Steps_start_is_one_step_above_end_at_same_t() {
            var start = new StepsEasing(4, StepPosition.Start);
            var end = new StepsEasing(4, StepPosition.End);
            // At t = 0.25 + tiny epsilon, start jumps a step earlier than end.
            Assert.That(start.Evaluate(0.1), Is.EqualTo(0.25).Within(Eps));
            Assert.That(end.Evaluate(0.1), Is.EqualTo(0).Within(Eps));
            // start at t > 0 starts at 1/N immediately.
            Assert.That(start.Evaluate(0.0001), Is.EqualTo(0.25).Within(Eps));
        }

        [Test]
        public void Steps_jump_start_and_jump_both_emit_start_offset_at_t_zero() {
            // jump-start: N jumps, value 1/N at t=0 (inclusive), 1 at t=1.
            var jumpStart = new StepsEasing(4, StepPosition.JumpStart);
            Assert.That(jumpStart.Evaluate(0), Is.EqualTo(0.25).Within(Eps));
            Assert.That(jumpStart.Evaluate(1), Is.EqualTo(1).Within(Eps));

            // jump-both: N+1 jumps, value 1/(N+1) at t=0, N/(N+1) just before t=1.
            var jumpBoth = new StepsEasing(4, StepPosition.JumpBoth);
            Assert.That(jumpBoth.Evaluate(0), Is.EqualTo(0.2).Within(Eps));

            // Regression: jump-end (the default) still returns 0 at t=0.
            var jumpEnd = new StepsEasing(4, StepPosition.JumpEnd);
            Assert.That(jumpEnd.Evaluate(0), Is.EqualTo(0).Within(Eps));
        }

        [Test]
        public void Steps_jump_none_outputs_zero_to_one_inclusive() {
            var s = new StepsEasing(4, StepPosition.JumpNone);
            // 4 steps with jump-none -> 3 jumps; values 0, 1/3, 2/3, 1 across the input range.
            // Boundaries are at 0, 0.25, 0.5, 0.75; the function is right-continuous so t = 0.5
            // sits in the third sub-interval (value = 2/3).
            Assert.That(s.Evaluate(0), Is.EqualTo(0).Within(Eps));
            Assert.That(s.Evaluate(0.1), Is.EqualTo(0).Within(Eps));
            Assert.That(s.Evaluate(0.3), Is.EqualTo(1.0 / 3.0).Within(Eps));
            Assert.That(s.Evaluate(0.6), Is.EqualTo(2.0 / 3.0).Within(Eps));
            Assert.That(s.Evaluate(1), Is.EqualTo(1).Within(Eps));
        }

        [Test]
        public void Parser_round_trip_keywords() {
            Assert.That(EasingParser.Parse("linear"), Is.InstanceOf<LinearEasing>());
            Assert.That(EasingParser.Parse("ease"), Is.InstanceOf<EaseEasing>());
            Assert.That(EasingParser.Parse("ease-in"), Is.InstanceOf<EaseInEasing>());
            Assert.That(EasingParser.Parse("ease-out"), Is.InstanceOf<EaseOutEasing>());
            Assert.That(EasingParser.Parse("ease-in-out"), Is.InstanceOf<EaseInOutEasing>());
        }

        [Test]
        public void Parser_cubic_bezier() {
            var e = EasingParser.Parse("cubic-bezier(0.42, 0, 0.58, 1)");
            var bez = (CubicBezierEasing)e;
            Assert.That(bez.X1, Is.EqualTo(0.42));
            Assert.That(bez.Y1, Is.EqualTo(0));
            Assert.That(bez.X2, Is.EqualTo(0.58));
            Assert.That(bez.Y2, Is.EqualTo(1));
        }

        [Test]
        public void Parser_steps_one_arg_defaults_to_end() {
            var s = (StepsEasing)EasingParser.Parse("steps(5)");
            Assert.That(s.Count, Is.EqualTo(5));
            Assert.That(s.Position, Is.EqualTo(StepPosition.End));
        }

        [Test]
        public void Parser_steps_explicit_positions() {
            Assert.That(((StepsEasing)EasingParser.Parse("steps(3, start)")).Position, Is.EqualTo(StepPosition.Start));
            Assert.That(((StepsEasing)EasingParser.Parse("steps(3, end)")).Position, Is.EqualTo(StepPosition.End));
            Assert.That(((StepsEasing)EasingParser.Parse("steps(3, jump-start)")).Position, Is.EqualTo(StepPosition.JumpStart));
            Assert.That(((StepsEasing)EasingParser.Parse("steps(3, jump-end)")).Position, Is.EqualTo(StepPosition.JumpEnd));
            Assert.That(((StepsEasing)EasingParser.Parse("steps(3, jump-both)")).Position, Is.EqualTo(StepPosition.JumpBoth));
            Assert.That(((StepsEasing)EasingParser.Parse("steps(3, jump-none)")).Position, Is.EqualTo(StepPosition.JumpNone));
        }

        [Test]
        public void Parser_linear_function_piecewise() {
            // Baseline: linear(0, 1) is the identity mapping.
            var identity = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 1)");
            Assert.That(identity.Evaluate(0.0), Is.EqualTo(0.0).Within(Eps));
            Assert.That(identity.Evaluate(0.25), Is.EqualTo(0.25).Within(Eps));
            Assert.That(identity.Evaluate(0.5), Is.EqualTo(0.5).Within(Eps));
            Assert.That(identity.Evaluate(0.75), Is.EqualTo(0.75).Within(Eps));
            Assert.That(identity.Evaluate(1.0), Is.EqualTo(1.0).Within(Eps));

            // Explicit middle position: 0..0.5 over [0%,50%], 0.5..1 over [50%,100%].
            var halfway = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 0.5 50%, 1)");
            Assert.That(halfway.Evaluate(0.25), Is.EqualTo(0.25).Within(Eps));
            Assert.That(halfway.Evaluate(0.5), Is.EqualTo(0.5).Within(Eps));
            Assert.That(halfway.Evaluate(0.75), Is.EqualTo(0.75).Within(Eps));

            // Auto-distributed inputs: linear(0, 1, 0) -> points at 0%, 50%, 100% -> a triangle.
            var triangle = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 1, 0)");
            Assert.That(triangle.Evaluate(0.25), Is.EqualTo(0.5).Within(Eps));
            Assert.That(triangle.Evaluate(0.5), Is.EqualTo(1.0).Within(Eps));
            Assert.That(triangle.Evaluate(0.75), Is.EqualTo(0.5).Within(Eps));

            // Double-position shorthand expands to two points with identical output.
            var flat = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 0.25 25% 75%, 1)");
            Assert.That(flat.Evaluate(0.25), Is.EqualTo(0.25).Within(Eps));
            Assert.That(flat.Evaluate(0.5), Is.EqualTo(0.25).Within(Eps));
            Assert.That(flat.Evaluate(0.75), Is.EqualTo(0.25).Within(Eps));

            // Parse-error conventions: empty arg list and non-numeric output progress both throw.
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear()"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear(abc)"));
        }

        [Test]
        public void Parser_throws_on_malformed() {
            Assert.Throws<FormatException>(() => EasingParser.Parse("bezier(0,0,1,1)"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("cubic-bezier(0,0,1)"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("steps()"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("steps(3, sideways)"));
            Assert.Throws<FormatException>(() => EasingParser.Parse(""));
            Assert.Throws<FormatException>(() => EasingParser.Parse("nope"));
        }

        // DC3: PiecewiseLinearEasing.Point and the public ctor are now
        // internal — only EasingParser constructs the type. This test pins
        // that the existing parser-driven path still produces a usable
        // PiecewiseLinearEasing instance (caught here at compile time too,
        // since the cast in EasingTests above would fail to compile if the
        // visibility of the class itself ever narrowed).
        [Test]
        public void Piecewise_linear_easing_constructed_via_parser_evaluates() {
            var e = EasingParser.Parse("linear(0, 1)");
            Assert.That(e, Is.InstanceOf<PiecewiseLinearEasing>());
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.5).Within(Eps));
        }

        // ──────────────────────────────────────────────────────────────────
        // TG22: direct coverage of EasingParser.ParseLinear (the lines
        // 30-32 dispatch + the ParseLinear body). We cross the structural
        // shape of the produced curve by sampling Evaluate, plus pin the
        // expected internal Point layout where the spec's contract is
        // subtle (multi-position shorthand, auto-distribution).
        // ──────────────────────────────────────────────────────────────────

        [Test]
        public void ParseLinear_bare_two_stops_yields_two_points() {
            // Two outputs with no positions -> first anchored at 0, last at 1.
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 1)");
            var pts = e.Points;
            Assert.That(pts.Count, Is.EqualTo(2));
            Assert.That(pts[0].Input, Is.EqualTo(0.0).Within(Eps));
            Assert.That(pts[0].Output, Is.EqualTo(0.0).Within(Eps));
            Assert.That(pts[1].Input, Is.EqualTo(1.0).Within(Eps));
            Assert.That(pts[1].Output, Is.EqualTo(1.0).Within(Eps));
        }

        [Test]
        public void ParseLinear_explicit_middle_percentage_yields_three_points() {
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 0.5 50%, 1)");
            var pts = e.Points;
            Assert.That(pts.Count, Is.EqualTo(3));
            Assert.That(pts[0].Input, Is.EqualTo(0.0).Within(Eps));
            Assert.That(pts[1].Input, Is.EqualTo(0.5).Within(Eps));
            Assert.That(pts[1].Output, Is.EqualTo(0.5).Within(Eps));
            Assert.That(pts[2].Input, Is.EqualTo(1.0).Within(Eps));
        }

        [Test]
        public void ParseLinear_double_position_shorthand_expands_to_two_points_same_output() {
            // `linear(0, 1 50% 75%, 1)` — per CSS Easing L2 §3.2 step 1 the
            // double-position shorthand splits "1 50% 75%" into two points
            // (output=1, input=0.5) and (output=1, input=0.75). Combined
            // with the anchored endpoints that's four points total.
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 1 50% 75%, 1)");
            var pts = e.Points;
            Assert.That(pts.Count, Is.EqualTo(4),
                "Double-position shorthand must expand the middle stop into TWO Points sharing the same output.");
            Assert.That(pts[0].Input, Is.EqualTo(0.0).Within(Eps));
            Assert.That(pts[0].Output, Is.EqualTo(0.0).Within(Eps));
            Assert.That(pts[1].Input, Is.EqualTo(0.5).Within(Eps));
            Assert.That(pts[1].Output, Is.EqualTo(1.0).Within(Eps));
            Assert.That(pts[2].Input, Is.EqualTo(0.75).Within(Eps));
            Assert.That(pts[2].Output, Is.EqualTo(1.0).Within(Eps));
            Assert.That(pts[3].Input, Is.EqualTo(1.0).Within(Eps));
            Assert.That(pts[3].Output, Is.EqualTo(1.0).Within(Eps));
            // Functional confirmation: between 50% and 75% the curve is flat
            // at output=1 (slope of the duplicated-output segment is zero).
            Assert.That(e.Evaluate(0.6), Is.EqualTo(1.0).Within(Eps));
        }

        [Test]
        public void ParseLinear_auto_distributes_missing_positions_evenly() {
            // Four outputs with no explicit positions — first/last anchor
            // at 0/1, the two middle points are filled by linear distribution.
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(0, 0.3, 0.7, 1)");
            var pts = e.Points;
            Assert.That(pts.Count, Is.EqualTo(4));
            Assert.That(pts[0].Input, Is.EqualTo(0.0).Within(Eps));
            Assert.That(pts[1].Input, Is.EqualTo(1.0 / 3.0).Within(Eps));
            Assert.That(pts[2].Input, Is.EqualTo(2.0 / 3.0).Within(Eps));
            Assert.That(pts[3].Input, Is.EqualTo(1.0).Within(Eps));
            Assert.That(pts[1].Output, Is.EqualTo(0.3).Within(Eps));
            Assert.That(pts[2].Output, Is.EqualTo(0.7).Within(Eps));
        }

        [Test]
        public void ParseLinear_partial_positions_fill_gap_between_anchors() {
            // Mix of explicit + missing positions inside the list — the run
            // of unspecified points between two known inputs is distributed
            // proportionally across that sub-interval (not [0,1]).
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(0 0%, 0.5, 0.7, 1 100%)");
            var pts = e.Points;
            Assert.That(pts.Count, Is.EqualTo(4));
            // Gap is [0%, 100%] with 3 sub-intervals -> middles at 1/3 and 2/3.
            Assert.That(pts[1].Input, Is.EqualTo(1.0 / 3.0).Within(Eps));
            Assert.That(pts[2].Input, Is.EqualTo(2.0 / 3.0).Within(Eps));
        }

        [Test]
        public void ParseLinear_clamps_decreasing_inputs_to_running_max() {
            // Author-supplied positions that go backwards must be clamped up
            // to the running maximum to preserve a non-decreasing input
            // sequence (otherwise Evaluate's segment-scan breaks).
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(0 0%, 0.5 80%, 0.6 30%, 1 100%)");
            var pts = e.Points;
            Assert.That(pts.Count, Is.EqualTo(4));
            Assert.That(pts[1].Input, Is.EqualTo(0.8).Within(Eps));
            // 30% would go backwards from 80%, so it is clamped up to 0.8.
            Assert.That(pts[2].Input, Is.EqualTo(0.8).Within(Eps),
                "Out-of-order percentage must be clamped up to the running max.");
            Assert.That(pts[3].Input, Is.EqualTo(1.0).Within(Eps));
        }

        [Test]
        public void ParseLinear_single_stop_is_rejected() {
            // Even though the body needs >= 2 points after expansion, a single
            // bare-output input string fails the same Count check.
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear(0.5)"));
        }

        [Test]
        public void ParseLinear_garbage_inputs_throw_format_exception() {
            // Empty args, whitespace-only args, non-numeric output, missing
            // percent sign on the position, more than two percentages per
            // stop — every one of these must throw FormatException.
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear()"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear(   )"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear(0, foo)"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear(0, 1 50)"));
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear(0, 1 25% 50% 75%, 1)"));
            // Empty trailing element from a stray comma.
            Assert.Throws<FormatException>(() => EasingParser.Parse("linear(0, 1,)"));
        }

        [Test]
        public void ParseLinear_endpoint_clamping_via_parsed_instance() {
            // Cross-check: the parser-built instance honours the clamp-to-
            // endpoint domain rule (same contract as TG21's direct tests).
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(0.2, 0.8)");
            Assert.That(e.Evaluate(-1.0), Is.EqualTo(0.2).Within(Eps));
            Assert.That(e.Evaluate(5.0), Is.EqualTo(0.8).Within(Eps));
        }

        [Test]
        public void ParseLinear_accepts_negative_and_overshoot_outputs() {
            // CSS allows easing outputs outside [0,1]; the parser must not
            // reject negative or >1 output values.
            var e = (PiecewiseLinearEasing)EasingParser.Parse("linear(-0.25, 1.25)");
            Assert.That(e.Evaluate(0.0), Is.EqualTo(-0.25).Within(Eps));
            Assert.That(e.Evaluate(1.0), Is.EqualTo(1.25).Within(Eps));
            Assert.That(e.Evaluate(0.5), Is.EqualTo(0.5).Within(Eps));
        }
    }
}
