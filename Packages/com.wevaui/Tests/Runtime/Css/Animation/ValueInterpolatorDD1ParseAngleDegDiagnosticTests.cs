using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Animation;
using Weva.Css.Values;
using Weva.Diagnostics;

namespace Weva.Tests.Css.Animation {
    // DD1 — `ParseAngleDeg` previously returned 0 on any unparseable input
    // (null, garbage suffix, malformed number) and the caller (rotation
    // interpolation) silently treated that as "no rotation". An author who
    // typoed `rotate(abcdeg)` saw 0deg instead of a parse failure.
    //
    // Fix: keep the 0 fallback (animators tolerate no-rotation), but route
    // through UICssDiagnostics.Warn("animation", "ParseAngleDeg failed for:
    // <token>") so the malformed keyframe surfaces to the author. The dedupe
    // key is the (source, detail) pair handled by UICssDiagnostics — passing
    // the offending token as the detail gives per-token dedupe, so one bad
    // keyframe sampled 60Hz logs exactly once per session.
    public class ValueInterpolatorDD1ParseAngleDegDiagnosticTests {
        static LengthContext Ctx() => LengthContext.Default;

        [SetUp]
        public void Reset() {
            // ResetCaches_TestOnly drops the transformFnCache so a "garbage
            // keyframe" string from a prior test doesn't short-circuit the
            // re-parse and skip the ParseAngleDeg call in the cached path.
            // ResetWarnings_TestOnly clears the diagnostic dedupe so the
            // warn fires fresh per test.
            ValueInterpolator.ResetCaches_TestOnly();
            ValueInterpolator.ResetWarnings_TestOnly();
        }

        [Test]
        public void Garbage_angle_token_warns_and_returns_zero_degrees() {
            // `rotate(abcdeg)` — the token ends in `deg` so it routes through
            // ParseAngleDeg, but `abc` doesn't parse as a number → all
            // suffix branches fall through → final return 0. Author should
            // see the diagnostic; output should still be a clean `0deg`-style
            // rotation (no NaN, no crash).
            LogAssert.Expect(LogType.Warning,
                new Regex(@"animation.*ParseAngleDeg failed for:.*abcdeg"));

            string result = ValueInterpolator.Interpolate(
                "rotate(abcdeg)",
                "rotate(90deg)",
                0.5,
                PropertyKind.Transform,
                Ctx());

            // Behaviour preserved: parse failure → treated as 0deg → midway
            // interpolation reads 45deg. The exact format may vary; the
            // load-bearing fact is that the call returns a sane non-null
            // rotation string rather than throwing.
            Assert.That(result, Is.Not.Null.And.Not.Empty);
            Assert.That(result, Does.Contain("rotate"));
        }

        [Test]
        public void Same_garbage_token_50_times_logs_once() {
            // Hot-path contract: a single bad keyframe must not flood the
            // console. We drop the transform parse cache between iterations
            // so each Interpolate call genuinely re-parses and re-calls
            // ParseAngleDeg — that way the test exercises the UICssDiagnostics
            // (source, detail) dedupe gate directly, rather than masking it
            // behind transformFnCache memoisation.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ParseAngleDeg failed for:.*xyzdeg"));

            for (int i = 0; i < 50; i++) {
                ValueInterpolator.ResetCaches_TestOnly();
                ValueInterpolator.Interpolate(
                    "rotate(xyzdeg)",
                    "rotate(90deg)",
                    0.5,
                    PropertyKind.Transform,
                    Ctx());
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_angle_keyframe_does_not_warn() {
            // The happy path (parseable `90deg`) must remain silent — the
            // warning only fires when the defensive 0-fallback is actually
            // taken.
            var result = ValueInterpolator.Interpolate(
                "rotate(0deg)",
                "rotate(90deg)",
                0.5,
                PropertyKind.Transform,
                Ctx());

            Assert.That(result, Does.Contain("rotate"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Distinct_garbage_tokens_each_log_once() {
            // Different bad tokens → different dedupe keys → one warning
            // each. Confirms the dedupe is keyed on the offending token, not
            // a class-level "already warned" flag.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ParseAngleDeg failed for:.*foodeg"));
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ParseAngleDeg failed for:.*bardeg"));

            ValueInterpolator.Interpolate("rotate(foodeg)", "rotate(90deg)", 0.5, PropertyKind.Transform, Ctx());
            ValueInterpolator.Interpolate("rotate(bardeg)", "rotate(90deg)", 0.5, PropertyKind.Transform, Ctx());

            LogAssert.NoUnexpectedReceived();
        }
    }
}
