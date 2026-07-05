using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // DD5 — `BoxToPaintConverter.ParseOriginAxisSlice` previously returned
    // null on calc() parse failure, and the caller (ResolveTransformOrigin)
    // silently fell back to the 50% default. An author who typed
    // `transform-origin: calc(broken) center` saw the default instead of a
    // parse failure.
    //
    // Fix: keep the null/default fallback (caller behaviour preserved), but
    // route through UICssDiagnostics.Warn("paint", "ParseOriginAxisSlice
    // failed for: <token>") so the malformed value surfaces to the author.
    // The dedupe key is the (source, detail) pair handled by
    // UICssDiagnostics — passing the offending calc token as the detail
    // gives per-token dedupe, so one bad value resolved every frame logs
    // exactly once per session.
    public class BoxToPaintConverterDD5ParseOriginAxisSliceDiagnosticTests {
        static ComputedStyle MakeStyle(string transformOrigin) {
            var s = new ComputedStyle(new Element("div"));
            s.Set(CssProperties.TransformOriginId, transformOrigin);
            return s;
        }

        static LengthContext Ctx() => LengthContext.Default;

        [SetUp]
        public void Reset() {
            UICssDiagnostics.ResetForTests();
        }

        [Test]
        public void Malformed_calc_token_warns_and_uses_default_origin() {
            // `calc(broken syntax)` doesn't parse → the catch arm fires →
            // ParseOriginAxisSlice returns null → the caller leaves ox at
            // the w*0.5 default. Author should see the diagnostic naming
            // the offending token.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"paint.*ParseOriginAxisSlice failed for:.*calc\(broken"));

            var style = MakeStyle("calc(broken syntax) center");
            var (ox, oy) = BoxToPaintConverter.ResolveTransformOrigin(style, 100, 80, Ctx());

            // Defensive default preserved: x defaults to half the basis
            // (the malformed token was dropped); y is `center` → 40.
            Assert.That(ox, Is.EqualTo(50.0).Within(1e-6));
            Assert.That(oy, Is.EqualTo(40.0).Within(1e-6));
        }

        [Test]
        public void Same_malformed_calc_50_times_logs_once() {
            // Hot-path contract: a per-frame paint pass on a single bad
            // style must not flood the console. UICssDiagnostics dedupes
            // on the (source, detail) pair, so 50 resolves of the same
            // bad calc emit exactly one Warning.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ParseOriginAxisSlice failed for:.*calc\(zzz"));

            var style = MakeStyle("calc(zzz) center");
            for (int i = 0; i < 50; i++) {
                BoxToPaintConverter.ResolveTransformOrigin(style, 100, 80, Ctx());
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_calc_origin_does_not_warn() {
            // The happy path (parseable calc) must remain silent — the
            // warning only fires when the defensive null-fallback is
            // actually taken.
            var style = MakeStyle("calc(10px + 20px) center");
            var (ox, oy) = BoxToPaintConverter.ResolveTransformOrigin(style, 100, 80, Ctx());

            // 10 + 20 = 30px on the x axis; y still center (40).
            Assert.That(ox, Is.EqualTo(30.0).Within(1e-6));
            Assert.That(oy, Is.EqualTo(40.0).Within(1e-6));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Distinct_malformed_calcs_each_log_once() {
            // Different bad tokens → different dedupe keys → one warning
            // each. Confirms the dedupe is keyed on the offending token,
            // not a class-level "already warned" flag.
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ParseOriginAxisSlice failed for:.*calc\(aa"));
            LogAssert.Expect(LogType.Warning,
                new Regex(@"ParseOriginAxisSlice failed for:.*calc\(bb"));

            BoxToPaintConverter.ResolveTransformOrigin(MakeStyle("calc(aa zz) center"), 100, 80, Ctx());
            BoxToPaintConverter.ResolveTransformOrigin(MakeStyle("calc(bb zz) center"), 100, 80, Ctx());

            LogAssert.NoUnexpectedReceived();
        }
    }
}
