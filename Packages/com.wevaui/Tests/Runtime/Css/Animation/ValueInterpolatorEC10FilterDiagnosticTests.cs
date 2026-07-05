using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Weva.Css.Animation;
using Weva.Css.Values;
using Weva.Diagnostics;

namespace Weva.Tests.Css.Animation {
    // EC10 — `try { from = FilterParser.Parse(...); to = FilterParser.Parse(...); }
    // catch (FilterParseException) { return discrete-step; }` is by-design
    // (CSS Animations L1: unparseable endpoint → discrete). The fix adds a
    // UICssDiagnostics.Warn so an author who typos a filter function sees
    // their endpoint was rejected; behavior (discrete-step) is preserved.
    //
    // Dedupe key shape: "EC10:" + fromRaw + "|" + toRaw — so one bad keyframe
    // sampled 60 times per second doesn't spam the console.
    public class ValueInterpolatorEC10FilterDiagnosticTests {
        static LengthContext Ctx() => LengthContext.Default;

        [SetUp]
        public void Reset() {
            ValueInterpolator.ResetWarnings_TestOnly();
        }

        [Test]
        public void Garbled_filter_endpoint_discrete_steps_and_warns() {
            // `not-a-filter(...)` isn't a recognized filter function — the
            // parser throws FilterParseException, the catch returns the
            // discrete-step result, and the diagnostic surfaces.
            LogAssert.Expect(LogType.Warning, new Regex(@"ValueInterpolator.*EC10.*filter\(\) parse failed"));

            var v = ValueInterpolator.Interpolate(
                "not-a-filter(2px)",
                "blur(4px)",
                0.7,
                PropertyKind.Filter,
                Ctx());

            // Discrete-step fallback at t=0.7 returns the `to` raw text.
            Assert.That(v, Is.EqualTo("blur(4px)"));
        }

        [Test]
        public void Same_garbled_endpoint_pair_50_times_logs_once() {
            LogAssert.Expect(LogType.Warning, new Regex(@"EC10.*not-real\(1px\)"));

            for (int i = 0; i < 50; i++) {
                ValueInterpolator.Interpolate(
                    "not-real(1px)",
                    "blur(4px)",
                    0.5,
                    PropertyKind.Filter,
                    Ctx());
            }

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Valid_filter_endpoints_do_not_warn() {
            // The happy path (matching filter function lists) interpolates
            // normally and must not warn.
            var v = ValueInterpolator.Interpolate(
                "brightness(1)",
                "brightness(1.5)",
                0.5,
                PropertyKind.Filter,
                Ctx());

            Assert.That(v, Is.EqualTo("brightness(1.25)"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Distinct_garbled_pairs_each_log_once() {
            // Different (from, to) pairs → different dedup keys → one
            // warning each. Confirms the dedupe is keyed on the input pair,
            // not a class-level "warned" flag.
            LogAssert.Expect(LogType.Warning, new Regex(@"EC10.*'broken-a"));
            LogAssert.Expect(LogType.Warning, new Regex(@"EC10.*'broken-b"));

            ValueInterpolator.Interpolate("broken-a(1px)", "blur(2px)", 0.5, PropertyKind.Filter, Ctx());
            ValueInterpolator.Interpolate("broken-b(1px)", "blur(2px)", 0.5, PropertyKind.Filter, Ctx());

            LogAssert.NoUnexpectedReceived();
        }
    }
}
