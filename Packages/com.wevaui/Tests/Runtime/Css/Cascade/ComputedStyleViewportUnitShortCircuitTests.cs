using System;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;

namespace Weva.Tests.Css.Cascade {
    // PI1 (CODE_AUDIT_FINDINGS.md): ComputedStyle.Set(int) / Set(string)
    // previously called ContainsViewportUnit(value) — a char-by-char scan of
    // the raw string — on EVERY Set, even when HasViewportRelativeValues was
    // already true. The flag is sticky for the lifetime of the style (only
    // Reset() / pool recycle clears it), so once flipped the per-Set rescan
    // is pure waste. These tests pin:
    //   1. Once HasViewportRelativeValues is true, 100 follow-up Sets allocate
    //      near-zero bytes (and, by proxy, don't re-walk every value string).
    //   2. The flag flips on the first Set that carries a viewport unit (vw /
    //      vh / vmin / vmax) — i.e. the fix doesn't silently disable the
    //      detection.
    //   3. Reset() returns the flag to false, and the next viewport-unit Set
    //      re-arms it — the pool-recycle path stays correct.
    public class ComputedStyleViewportUnitShortCircuitTests {
        // PI1.1: hot-path alloc check. After flipping the flag, a long burst
        // of Set calls with non-viewport values must not pay the per-char
        // scan. The scan itself doesn't allocate, so we can't observe its
        // CPU cost directly via GC counters — but if anything in the Set
        // path silently allocates (e.g. boxing, dict probes) regression here
        // catches it. The real win is CPU, not bytes; this guard pins that
        // we didn't ADD an alloc while removing the scan.
        [Test]
        public void Hot_path_after_flag_is_set_allocates_near_zero_PI1() {
            var style = new ComputedStyle(new Element("div"));
            // Prime the flag with a viewport-unit value.
            style.Set(CssProperties.WidthId, "50vw");
            Assert.That(style.HasViewportRelativeValues, Is.True,
                "Flag must arm on first viewport-unit Set (precondition for hot-path test).");

            // Pre-build a stable pool of distinct non-viewport values so each
            // Set actually mutates the slot (string.Equals early-out would
            // otherwise mask the rescan cost). Use Width repeatedly so the
            // dispatch goes through the registered-property fast path.
            string[] vals = new string[100];
            for (int i = 0; i < vals.Length; i++) vals[i] = (i + 100) + "px";

            // Warm: JIT + any one-shot caches.
            for (int i = 0; i < vals.Length; i++) style.Set(CssProperties.WidthId, vals[i]);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < vals.Length; i++) {
                style.Set(CssProperties.WidthId, vals[i]);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            // The Set path may still produce small allocations from
            // ComputedStyleVersion bookkeeping if implemented as boxing,
            // but the steady state is expected to be well under 1 KB.
            Assert.That(delta, Is.LessThan(1024),
                $"100 Sets on a viewport-flagged style allocated {delta} bytes; expected near-zero with PI1 short-circuit.");
        }

        // PI1.2: the flag is correctly armed on the FIRST value containing a
        // viewport unit. Tests all four units, both registered and custom-
        // property dispatch paths.
        [Test]
        public void Flag_arms_on_first_viewport_unit_value_PI1() {
            // vw via int-id (registered) path.
            {
                var style = new ComputedStyle(new Element("div"));
                Assert.That(style.HasViewportRelativeValues, Is.False,
                    "Fresh style must start with HasViewportRelativeValues == false.");
                style.Set(CssProperties.WidthId, "12px");
                Assert.That(style.HasViewportRelativeValues, Is.False,
                    "Px value must not arm the flag.");
                style.Set(CssProperties.WidthId, "12vw");
                Assert.That(style.HasViewportRelativeValues, Is.True,
                    "vw value must arm the flag.");
            }
            // vh via string Set (registered).
            {
                var style = new ComputedStyle(new Element("div"));
                style.Set("height", "8vh");
                Assert.That(style.HasViewportRelativeValues, Is.True,
                    "vh value must arm the flag (string-keyed Set).");
            }
            // vmin / vmax both detected.
            {
                var style = new ComputedStyle(new Element("div"));
                style.Set(CssProperties.WidthId, "1vmin");
                Assert.That(style.HasViewportRelativeValues, Is.True, "vmin must arm.");
            }
            {
                var style = new ComputedStyle(new Element("div"));
                style.Set(CssProperties.WidthId, "1vmax");
                Assert.That(style.HasViewportRelativeValues, Is.True, "vmax must arm.");
            }
            // Custom property path (--*) also scans the value.
            {
                var style = new ComputedStyle(new Element("div"));
                style.Set("--my-size", "33vw");
                Assert.That(style.HasViewportRelativeValues, Is.True,
                    "Custom property path must arm the flag for viewport units.");
            }
        }

        // PI1.3: Reset() clears the sticky flag and the next viewport-unit Set
        // re-arms it. Critical because ComputedStyle is pooled — a recycled
        // style that incorrectly stayed "armed" would poison every fresh
        // cascade pass into thinking it had viewport deps.
        [Test]
        public void Reset_clears_flag_and_subsequent_viewport_set_rearms_PI1() {
            var style = new ComputedStyle(new Element("div"));
            style.Set(CssProperties.WidthId, "50vw");
            Assert.That(style.HasViewportRelativeValues, Is.True,
                "Precondition: flag armed after first viewport-unit Set.");

            style.Reset();
            Assert.That(style.HasViewportRelativeValues, Is.False,
                "Reset must clear HasViewportRelativeValues for pool recycle.");

            // After reset, a non-viewport Set must NOT re-arm.
            style.Set(CssProperties.WidthId, "100px");
            Assert.That(style.HasViewportRelativeValues, Is.False,
                "Non-viewport Set after Reset must not arm the flag.");

            // And then a real viewport-unit Set must re-arm cleanly.
            style.Set(CssProperties.HeightId, "75vh");
            Assert.That(style.HasViewportRelativeValues, Is.True,
                "Viewport-unit Set after Reset must re-arm the flag.");
        }

        // PI1 bonus: the short-circuit must NOT mask a legitimate flag flip
        // when the FIRST Set already carries a viewport unit. Tightens the
        // guard against an off-by-one in the `!HasViewportRelativeValues`
        // gate (e.g. checking the flag AFTER the assignment would skip the
        // first scan and leave the flag false). Covered indirectly by
        // tests 2 and 3 above but spelled out as its own assertion for
        // future readers.
        [Test]
        public void First_set_with_viewport_unit_arms_flag_PI1_gate_correctness() {
            var style = new ComputedStyle(new Element("div"));
            // No prior Sets — flag is false, so the short-circuit gate
            // evaluates `!false && ContainsViewportUnit(...)`. If the gate
            // were inverted, this would silently leave the flag false.
            style.Set(CssProperties.WidthId, "100vw");
            Assert.That(style.HasViewportRelativeValues, Is.True,
                "Short-circuit gate must still allow the FIRST viewport-unit Set to flip the flag.");
        }
    }
}
