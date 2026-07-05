using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Animation;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    // EC4 — `try { EasingParser.Parse(...) } catch { }` is a by-design fallback
    // to the initial `ease` value per CSS Easing L1 §2.1. The fix:
    //   1. Keeps the broad catch (EasingParser throws BCL FormatException /
    //      ArgumentNullException, not a typed parse-exception).
    //   2. Preserves the `ease` fallback (production behavior unchanged).
    //   3. Adds a UICssDiagnostics.Warn so an author who typos
    //      transition-timing-function or animation-timing-function sees their
    //      easing string was rejected.
    //   4. Dedupes per offending easing string (process-static HashSet) so a
    //      hot path with the same bad value doesn't spam the console.
    public class CssAnimationRunnerEC4EasingDiagnosticTests {
        static (CssAnimationRunner runner, FakeUIClock clock) MakeRunner() {
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, System.Array.Empty<Stylesheet>(), clock);
            return (runner, clock);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        [SetUp]
        public void Reset() {
            CssAnimationRunner.ResetWarnings_TestOnly();
        }

        [Test]
        public void Transition_with_garbled_easing_falls_back_to_ease_and_warns() {
            // `wobble(0.5)` isn't a known easing function — EasingParser throws
            // FormatException, the EC4 catch in BuildFromLonghands swallows +
            // falls back to EaseEasing, and the diagnostic surfaces the
            // dropped string. We set the transition-* LONGHANDS directly
            // (not the `transition` shorthand) so the code path goes through
            // `BuildFromLonghands` — the site where the EC4 catch lives.
            LogAssert.Expect(LogType.Warning, new Regex(@"CssAnimationRunner.*EC4.*wobble\(0\.5\)"));

            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "wobble(0.5)"));
            var next = Style(e, ("opacity", "1"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "wobble(0.5)"));
            runner.OnStyleChange(e, prev, next);
            // Fallback behavior unchanged: transition still runs with `ease`.
            Assert.That(runner.HasRunningAnimations(e), Is.True);
        }

        [Test]
        public void Same_garbled_easing_repeated_50_times_logs_once() {
            // Dedupe contract: per-offending-input. 50 transitions all
            // declaring the same bad easing string → exactly one warning.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC4.*nope\(1,2,3\)"));

            for (int i = 0; i < 50; i++) {
                var (runner, _) = MakeRunner();
                var e = new Element("div");
                var prev = Style(e, ("opacity", "0"),
                    ("transition-property", "opacity"),
                    ("transition-duration", "1s"),
                    ("transition-timing-function", "nope(1,2,3)"));
                var next = Style(e, ("opacity", "1"),
                    ("transition-property", "opacity"),
                    ("transition-duration", "1s"),
                    ("transition-timing-function", "nope(1,2,3)"));
                runner.OnStyleChange(e, prev, next);
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_easing_does_not_warn() {
            // Sanity: well-formed easing must not trip the warn path. We use
            // the longhand path here too so the assertion targets the same
            // EC4 site as the failing tests above.
            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e, ("opacity", "0"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "linear"));
            var next = Style(e, ("opacity", "1"),
                ("transition-property", "opacity"),
                ("transition-duration", "1s"),
                ("transition-timing-function", "linear"));
            runner.OnStyleChange(e, prev, next);

            Assert.That(runner.HasRunningAnimations(e), Is.True);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Animation_longhand_with_garbled_easing_falls_back_and_warns() {
            // The second catch site (`BuildAnimSpecsFromLonghands`, line ~719)
            // — different code path but same diagnostic + fallback contract.
            // We set the animation-* longhands directly because the cascade
            // is what would normally expand the `animation` shorthand, and
            // this test bypasses the cascade.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC4.*bogus-easing"));

            var (runner, clock) = MakeRunner();
            var e = new Element("div");
            var prev = Style(e);
            var next = Style(e,
                ("animation-name", "spin"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "bogus-easing"),
                ("animation-iteration-count", "infinite"));
            runner.OnStyleChange(e, prev, next);

            // The warning was emitted; we don't assert HasRunningAnimations
            // since the keyframes resolver may decline to start without a
            // matching @keyframes rule.
            Assert.That(
                UICssDiagnostics.HasEmittedForTests(
                    "CssAnimationRunner",
                    "EC4: easing string 'bogus-easing' failed to parse (FormatException); falling back to the initial value `ease` per CSS Easing L1 §2.1."),
                Is.True);
        }
    }
}
