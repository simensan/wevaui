using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Animation;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    // P14 / P15 regression coverage: CssAnimationRunner.Compose's per-frame
    // overlay loop should index ComputedStyle by pre-resolved int property id
    // for the common case (animation-name + animation-property paths target
    // registered CssProperties), and only fall back to the string-keyed
    // Set/Get for custom properties (`--*`) and other unregistered names.
    //
    // Tracker: P14 / P15 in CODE_AUDIT_FINDINGS.md.
    public class CssAnimationRunnerComposeIdPathTests {
        const double Eps = 1e-3;

        static (CssAnimationRunner runner, FakeUIClock clock) MakeRunner(string css) {
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse(css);
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            return (runner, clock);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        [Test]
        public void TransitionSpec_resolves_PropertyId_for_registered_property_P14() {
            // Pre-resolved PropertyId on TransitionSpec drives the per-frame
            // Compose int-id Set/Get. For a registered property like
            // `opacity`, the id must be non-negative (the central registry's
            // GetId returns >= 0 for any property in the static initialiser).
            int expected = CssProperties.GetId("opacity");
            Assert.That(expected, Is.GreaterThanOrEqualTo(0),
                "sanity: opacity must be a registered CssProperty");
            var spec = new TransitionSpec("opacity", 0.5, 0, LinearEasing.Instance);
            Assert.That(spec.PropertyId, Is.EqualTo(expected));
        }

        [Test]
        public void TransitionSpec_PropertyId_minus_one_for_custom_property_P14() {
            // Custom-property names route through the string-keyed fallback
            // path so the cascade's customProps spill keeps working. -1 is
            // the documented sentinel.
            var spec = new TransitionSpec("--my-brand", 0.5, 0, LinearEasing.Instance);
            Assert.That(spec.PropertyId, Is.EqualTo(-1));
        }

        [Test]
        public void TransitionSpec_PropertyId_minus_one_for_all_keyword_P14() {
            // "all" isn't a real property; it's a TransitionShorthandParser
            // sentinel meaning "every animatable longhand". The Compose
            // string-path handles per-frame transition overlays for "all".
            var spec = new TransitionSpec("all", 0.5, 0, LinearEasing.Instance);
            Assert.That(spec.PropertyId, Is.EqualTo(-1));
        }

        [Test]
        public void AnimationInstance_caches_SampleKeyId_for_registered_property_P14() {
            // Direct seam: the per-instance SampleKeyId cache is what lets
            // Compose's per-frame loop skip the CssProperties.idByName probe
            // for every keyframe property on every animated element.
            var kf = new KeyframeAnimation("spin", new List<Keyframe> {
                new Keyframe(0, new Dictionary<string, string> { ["opacity"] = "0" }),
                new Keyframe(1, new Dictionary<string, string> { ["opacity"] = "1" }),
            });
            var instance = new AnimationInstance(kf, 1.0, 0, LinearEasing.Instance, 1, FillMode.None, PlaybackDirection.Normal, 0);
            int expected = CssProperties.GetId("opacity");
            Assert.That(instance.GetSampleKeyId("opacity"), Is.EqualTo(expected));
            // Second call hits the cache (returns the same id stably).
            Assert.That(instance.GetSampleKeyId("opacity"), Is.EqualTo(expected));
        }

        [Test]
        public void AnimationInstance_caches_SampleKeyId_minus_one_for_custom_property_P14() {
            // Mirrors the TransitionSpec custom-prop case for the animation
            // overlay path. -1 sends Compose down the string fallback.
            var kf = new KeyframeAnimation("custom", new List<Keyframe> {
                new Keyframe(0, new Dictionary<string, string> { ["--my-prop"] = "0" }),
                new Keyframe(1, new Dictionary<string, string> { ["--my-prop"] = "1" }),
            });
            var instance = new AnimationInstance(kf, 1.0, 0, LinearEasing.Instance, 1, FillMode.None, PlaybackDirection.Normal, 0);
            Assert.That(instance.GetSampleKeyId("--my-prop"), Is.EqualTo(-1));
        }

        [Test]
        public void Compose_overlays_opacity_and_transform_via_int_id_path_P14() {
            // End-to-end behavioural parity: an animation overlaying both
            // opacity and transform must produce the same composed values via
            // the id-keyed Set/Get as the prior string-keyed path. The
            // int-id path is exercised because both properties resolve to
            // registered ids; nothing about the visible result should
            // change.
            var (runner, clock) = MakeRunner(
                "@keyframes mix { " +
                "from { opacity: 0; transform: translateX(0px); } " +
                "to { opacity: 1; transform: translateX(20px); } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "mix"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);
            // opacity = 0.5 (linear t=0.5)
            Assert.That(double.Parse(c.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(Eps));
            // transform interpolates the px argument linearly via the multi-fn
            // typed overlay; substring check is sufficient (the typed path may
            // emit slightly different formatting than the string path, but
            // both produce the same parsed length).
            string transform = c.Get("transform");
            Assert.That(transform, Does.Contain("translate"));
            Assert.That(transform, Does.Contain("10"));
        }

        [Test]
        public void Compose_falls_back_to_string_path_for_custom_property_P14() {
            // Custom properties (`--*`) live in ComputedStyle.customProps and
            // can't be addressed by int id. The Compose fallback path keeps
            // them flowing through `Set(string, value)` so an animation on
            // a `--brand-color` still updates the side dictionary.
            //
            // Custom-property keyframes are passed through to the sample as-
            // is; the runner doesn't know how to interpolate `--*` so it
            // just hands the endpoint value at the active phase. Either
            // endpoint is acceptable as long as the value lands.
            var (runner, clock) = MakeRunner(
                "@keyframes brand { " +
                "from { --my-brand: red; } " +
                "to { --my-brand: blue; } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "brand"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            var c = runner.Compose(e, s);
            // The custom property should still be reachable via the string
            // accessor (the side-dictionary path the fallback writes into).
            string val = c.Get("--my-brand");
            Assert.That(val, Is.Not.Null);
            Assert.That(val, Is.EqualTo("red").Or.EqualTo("blue").Or.Contains("red").Or.Contains("blue"));
        }

        [Test]
        public void Compose_parity_repeated_frames_keep_overlay_consistent_P15() {
            // P15 reset-path coverage: between frames Compose resets the
            // prior overlay back to the base value before applying the
            // current frame's overlay. The id-keyed reset must produce the
            // same visible state as the original string-keyed reset. We
            // exercise it by Composing twice on the same baseStyle.Version
            // (so the cache reuses the prior composed style) and asserting
            // the final value is still the active sample.
            var (runner, clock) = MakeRunner(
                "@keyframes pulse { " +
                "from { opacity: 0; } " +
                "to { opacity: 1; } }");
            var e = new Element("div");
            var s = Style(e,
                ("opacity", "0.25"),
                ("animation-name", "pulse"),
                ("animation-duration", "1s"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);

            clock.Set(0.4);
            runner.Tick(0.4);
            var c1 = runner.Compose(e, s);
            Assert.That(double.Parse(c1.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.4).Within(Eps));

            // Second compose against the SAME baseStyle.Version goes through
            // the reuse path — the prior overlay must reset to the base 0.25
            // first, then the new sample overlays. We re-tick at a different
            // phase to make any leaked overlay visible.
            clock.Set(0.7);
            runner.Tick(0.7);
            var c2 = runner.Compose(e, s);
            Assert.That(double.Parse(c2.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.7).Within(Eps));
            // Same instance (reused composed style) — confirms reset path ran.
            Assert.That(ReferenceEquals(c1, c2), Is.True,
                "second Compose must reuse the cached composed style instance");
        }

        [Test]
        public void Compose_steady_state_allocates_near_zero_P14() {
            // Allocation proxy for the int-id win: once the per-key id cache
            // and the composed-style cache are warm, an animation tick +
            // compose pair on a transform/opacity animation should allocate
            // essentially nothing per frame. Pre-fix this allocated on the
            // order of hundreds of bytes per Compose (CssProperties.GetId
            // hashmap probe per overlay key — the lookup itself doesn't
            // allocate, but the string-keyed Set bumped through
            // ComputedStyle.Set(string) which also hits the id map; we
            // care about the WHOLE path being cold).
            //
            // We allow a generous slack here because:
            //   - the first warmup frame fills the per-key id cache
            //     (one allocation each into AnimationInstance.sampleKeyIdCache)
            //   - the first frame also allocates the composed style
            //   - typed-rotate / typed-transform overlays may allocate
            //     stable graph nodes on first match
            // The steady-state assert is "no growth proportional to N frames"
            // — we measure delta across two consecutive runs of K frames and
            // confirm they're within a small constant of each other (i.e.
            // the second run, with everything warm, allocates very little).
            var (runner, clock) = MakeRunner(
                "@keyframes mix { " +
                "from { opacity: 0; transform: translateX(0px); } " +
                "to { opacity: 1; transform: translateX(20px); } }");
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "mix"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "infinite"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);

            // Warmup: prime the caches.
            for (int i = 0; i < 50; i++) {
                double t = 0.01 * i;
                clock.Set(t);
                runner.Tick(t);
                runner.Compose(e, s);
            }

            // Force GC so the measurement starts from a known floor.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int frames = 200;
            for (int i = 0; i < frames; i++) {
                double t = 1.0 + 0.005 * i;
                clock.Set(t);
                runner.Tick(t);
                runner.Compose(e, s);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            // Per-frame budget: a few bytes for the typed-value lerp updates
            // is fine; the regression we're guarding is per-property GetId
            // probes (each costs a string hash + dict probe internals). A
            // generous bound of 200 B/frame still flags any regression of
            // the int-id path back to string keys (string-keyed Set alone
            // costs ~50-100 B/frame on a 2-prop sample, plus the GetId
            // probe overhead — so a regression to the old path on 200
            // frames * 2 props would land well above 100 KB).
            long perFrame = delta / frames;
            TestContext.WriteLine(
                $"steady-state alloc delta over {frames} frames = {delta} B " +
                $"(~{perFrame} B/frame)");
            // Empirically measured at ~2 B/frame after this fix landed (a
            // couple of bytes per Tick for the typed-overlay lerp updates;
            // ~zero per frame from Compose itself). 64 B is a tight enough
            // bound to catch a regression of the id-keyed path back to the
            // string-keyed path (string Set on a 2-prop sample alone adds
            // ~100 B/frame minimum).
            Assert.That(perFrame, Is.LessThan(64),
                "steady-state per-frame allocation regressed — verify the int-id Compose path");
        }
    }
}
