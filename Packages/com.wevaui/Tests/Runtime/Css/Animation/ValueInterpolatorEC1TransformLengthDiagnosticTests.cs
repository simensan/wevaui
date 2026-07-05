using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Animation;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    // EC1 — `InterpolateTransformArg`'s mixed-unit length-conversion path
    // previously had a silent `catch (InvalidOperationException) { }` that
    // swallowed the `CssLength.ToPixels` failure when a `%` length endpoint
    // hit a `LengthContext` with no `BasisPixels` set. The interpolator then
    // silently degraded to the bare-number fallback (`TryParseNumber` on the
    // raw strings), losing the unit entirely.
    //
    // Fix: route the catch through `UICssDiagnostics.Warn("animation", ...)`
    // with a per-offending-unit-pair detail so an author who animates
    // `translateX(50%)` -> `translateX(100px)` without a resolvable basis
    // sees the failure. Bare-number fallback is preserved. Dedupe key shape:
    // "EC1: transform length-conversion failed for unit-pair %->px ..." —
    // 60Hz sampling on a single bad keyframe logs exactly once per session
    // per unit-pair.
    public class ValueInterpolatorEC1TransformLengthDiagnosticTests {
        // A LengthContext with BasisPixels unset — `%` lengths cannot resolve
        // and CssLength.ToPixels throws InvalidOperationException.
        static LengthContext NoBasisCtx() {
            var c = LengthContext.Default;
            c.BasisPixels = null;
            return c;
        }

        [SetUp]
        public void Reset() {
            // ResetCaches_TestOnly drops the transformFnCache so a repeated
            // keyframe pair from a prior test doesn't short-circuit the
            // re-parse and skip the InterpolateTransformArg call in the
            // cached path. ResetWarnings_TestOnly clears the UICssDiagnostics
            // dedupe so the warn fires fresh per test.
            ValueInterpolator.ResetCaches_TestOnly();
            ValueInterpolator.ResetWarnings_TestOnly();
        }

        [Test]
        public void Mixed_unit_translateX_with_missing_basis_warns_once() {
            // `translateX(50%)` -> `translateX(100px)` with no BasisPixels:
            //   - same-unit fast path skipped (`%` vs `px`)
            //   - ToPixels(la) -> InvalidOperationException ("Cannot resolve
            //     percent length without BasisPixels in LengthContext")
            //   - EC1 catch fires the diagnostic
            //   - bare-number fallback parses "50%" / "100px" — both fail
            //     TryParseNumber (suffix breaks double.Parse), so the discrete
            //     `t<0.5 ? aa : bb` arm runs. The load-bearing assertion is
            //     the warning, not the output string shape.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"animation.*EC1: transform length-conversion failed for unit-pair %->px"));

            var result = ValueInterpolator.Interpolate(
                "translateX(50%)",
                "translateX(100px)",
                0.5,
                PropertyKind.Transform,
                NoBasisCtx());

            Assert.That(result, Is.Not.Null.And.Not.Empty);
            Assert.That(result, Does.Contain("translateX"));
        }

        [Test]
        public void Same_offending_unit_pair_50_repeats_dedupe_to_one_warn() {
            // Hot-path contract: a single bad keyframe sampled many times
            // (~60Hz for a 1-second transition is 60 calls) must log exactly
            // once. We drop the transform parse cache between iterations so
            // each Interpolate call genuinely re-runs InterpolateTransformArg
            // and re-hits the EC1 catch — that way the test exercises the
            // UICssDiagnostics (source, detail) dedupe gate directly rather
            // than masking it behind transformFnCache memoisation.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"EC1: transform length-conversion failed for unit-pair %->px"));

            for (int i = 0; i < 50; i++) {
                ValueInterpolator.ResetCaches_TestOnly();
                ValueInterpolator.Interpolate(
                    "translateX(50%)",
                    "translateX(100px)",
                    0.5,
                    PropertyKind.Transform,
                    NoBasisCtx());
            }

            // LogAssert.NoUnexpectedReceived asserts no further warnings
            // beyond the single Expect above were emitted — i.e. the dedupe
            // collapsed the other 49 to no-ops.
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_mixed_unit_with_basis_is_silent() {
            // Sanity check: a `%` endpoint WITH a resolvable BasisPixels
            // (200px) resolves cleanly to 100px, the same-px fast path
            // commits, and the catch never fires. The warn must not appear.
            var ctx = LengthContext.Default;
            ctx.BasisPixels = 200; // 50% of 200px = 100px

            var result = ValueInterpolator.Interpolate(
                "translateX(50%)",
                "translateX(100px)",
                0.5,
                PropertyKind.Transform,
                ctx);

            Assert.That(result, Does.Contain("translateX"));
            // Both endpoints resolve to 100px, so the t=0.5 interpolated
            // value should also be ~100px. The load-bearing fact for EC1 is
            // that no warning fires — the value-shape assertion above is
            // belt-and-braces.
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Same_unit_endpoints_skip_conversion_and_stay_silent() {
            // Sanity check: same-unit endpoints (`px` <-> `px`) take the
            // early `la.Unit == lb.Unit` branch and never reach the ToPixels
            // try/catch at all. No warning, no diagnostic, even with a null
            // BasisPixels context.
            var result = ValueInterpolator.Interpolate(
                "translateX(20px)",
                "translateX(80px)",
                0.5,
                PropertyKind.Transform,
                NoBasisCtx());

            Assert.That(result, Does.Contain("translateX"));
            LogAssert.NoUnexpectedReceived();
        }
    }
}
