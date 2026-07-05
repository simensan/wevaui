#if UNITY_2023_1_OR_NEWER
using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine.TestTools;
using Weva.Diagnostics;
using Weva.Text.Sdf;

namespace Weva.Tests.Text.Sdf {
    // DD6 — Four reflection-bound catch sites in SdfGlyphRasterizer used to
    // stash ex.Message into a static s_LookupError field only, with no
    // console signal. A FontEngine.LowLevel API rename in a Unity patch
    // release silently disabled SDF rendering — authors saw text disappear
    // with no warning.
    //
    // The fix routes every site through NoteCatch, which:
    //   (1) preserves the s_LookupError structured-access channel
    //       (so EC12's tests + tooling that read ReflectionError keep working)
    //   (2) fires a UICssDiagnostics.Warn on first occurrence of each
    //       (callsite, ex.Message) pair
    //   (3) dedupes by (callsite, ex.Message) — a tight catch loop emits
    //       at most one warning; a different callsite or message gets its
    //       own first-time fire.
    //
    // Tests drive NoteCatch through the SimulateCatchForTests seam — the
    // production catches only fire when a real FontEngine call reaches them,
    // which CI cannot stage without a font asset. The seam runs the
    // identical NoteCatch path, so the pin covers the production behaviour.
    public class SdfGlyphRasterizerDD6DiagnosticTests {
        [SetUp]
        public void Reset() {
            SdfGlyphRasterizer.ResetLookupForTests();
            SdfGlyphRasterizer.ResetDiagForTests();
            UICssDiagnostics.ResetForTests();
            UICssDiagnostics.Enabled = true;
        }

        [TearDown]
        public void TearDown() {
            SdfGlyphRasterizer.ResetLookupForTests();
            SdfGlyphRasterizer.ResetDiagForTests();
            UICssDiagnostics.ResetForTests();
        }

        [Test]
        public void Single_failure_emits_diagnostic_and_populates_lookup_error() {
            // The catch path must (a) populate s_LookupError so structured
            // readers like ReflectionError keep working, and (b) fire a
            // first-time UICssDiagnostics.Warn so the console shows an
            // immediate signal.
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] SdfGlyphRasterizer: EnsureLookup: dd6 single"));

            var ex = new InvalidOperationException("dd6 single");
            var captured = SdfGlyphRasterizer.SimulateCatchForTests("EnsureLookup", ex);

            Assert.That(captured, Is.EqualTo("dd6 single"),
                "s_LookupError must still receive ex.Message (preserves EC12 contract).");
            Assert.That(SdfGlyphRasterizer.GetRawLookupErrorForTests(), Is.EqualTo("dd6 single"));
            Assert.That(
                UICssDiagnostics.HasEmittedForTests("SdfGlyphRasterizer", "EnsureLookup: dd6 single"),
                Is.True,
                "First-time catch at this site must emit a diagnostic.");
        }

        [Test]
        public void Fifty_identical_failures_emit_at_most_one_diagnostic() {
            // Hot-path safety: the reflection catches sit on rasterizer code
            // that can fire on every glyph miss. Dedupe must collapse 50
            // identical (callsite, message) failures to a single
            // UICssDiagnostics warning so the console doesn't drown.
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] SdfGlyphRasterizer: TryRasterizeViaOverride: dd6 loop"));

            int before = UICssDiagnostics.EmittedCountForTests();
            int diagBefore = SdfGlyphRasterizer.DiagEmittedCountForTests();

            for (int i = 0; i < 50; i++) {
                var ex = new InvalidOperationException("dd6 loop");
                SdfGlyphRasterizer.SimulateCatchForTests("TryRasterizeViaOverride", ex);
            }

            Assert.That(UICssDiagnostics.EmittedCountForTests() - before, Is.EqualTo(1),
                "50 identical (callsite, message) failures must dedupe to 1 console warning.");
            Assert.That(SdfGlyphRasterizer.DiagEmittedCountForTests() - diagBefore, Is.EqualTo(1),
                "The rasterizer-local dedupe set tracks the same pair only once.");
            Assert.That(SdfGlyphRasterizer.GetRawLookupErrorForTests(), Is.EqualTo("dd6 loop"),
                "s_LookupError keeps the latest ex.Message even when warns dedupe.");
        }

        [Test]
        public void Different_callsite_emits_its_own_diagnostic() {
            // The dedupe key is (callsite, ex.Message). The same message
            // raised at a different catch site must still emit its own
            // first-time warning — otherwise the EnsureLookup failure mode
            // would mask a different failure mode in TryRasterizeViaFontEngine.
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] SdfGlyphRasterizer: EnsureAddGlyphLookup: dd6 shared"));
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] SdfGlyphRasterizer: TryRasterizeViaFontEngine\.Invoke: dd6 shared"));

            int before = UICssDiagnostics.EmittedCountForTests();

            // Same ex.Message, different callsite — must NOT dedupe.
            SdfGlyphRasterizer.SimulateCatchForTests(
                "EnsureAddGlyphLookup", new Exception("dd6 shared"));
            SdfGlyphRasterizer.SimulateCatchForTests(
                "TryRasterizeViaFontEngine.Invoke", new Exception("dd6 shared"));

            Assert.That(UICssDiagnostics.EmittedCountForTests() - before, Is.EqualTo(2),
                "Different callsites with identical messages must each emit one warning.");
        }

        [Test]
        public void Distinct_messages_at_same_callsite_each_emit() {
            // Belt-and-braces: dedupe key includes the message, so a fresh
            // FontEngine error after a Unity upgrade (different message)
            // should NOT be swallowed by an earlier warn at the same site.
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] SdfGlyphRasterizer: EnsureLookup: dd6 first"));
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] SdfGlyphRasterizer: EnsureLookup: dd6 second"));

            int before = UICssDiagnostics.EmittedCountForTests();

            SdfGlyphRasterizer.SimulateCatchForTests(
                "EnsureLookup", new Exception("dd6 first"));
            SdfGlyphRasterizer.SimulateCatchForTests(
                "EnsureLookup", new Exception("dd6 second"));

            Assert.That(UICssDiagnostics.EmittedCountForTests() - before, Is.EqualTo(2));
        }

        [Test]
        public void Null_message_does_not_throw_and_emits_one_diagnostic() {
            // Exception subclasses can return null from .Message (rare, but a
            // crafted Exception or proxy can). NoteCatch must tolerate that
            // and still emit a single deduped warning rather than NRE'ing
            // inside the catch path.
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] SdfGlyphRasterizer: EnsureLookup: <null>"));

            int before = UICssDiagnostics.EmittedCountForTests();

            Assert.DoesNotThrow(() =>
                SdfGlyphRasterizer.SimulateCatchForTests("EnsureLookup", null));

            Assert.That(UICssDiagnostics.EmittedCountForTests() - before, Is.EqualTo(1));
        }
    }
}
#endif
