using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine.TestTools;
using Weva.Css.Cascade;
using Weva.Diagnostics;
using Weva.Dom;

namespace Weva.Tests.Diagnostics {
    // Tests asserting the diagnostic side-channel observes silent skips.
    // We never assert on the Debug.LogWarning string contents directly —
    // Unity's LogAssert is brittle on text matching — instead we use
    // HasEmittedForTests, which is a test-only helper that consults the
    // dedupe HashSet. We DO whitelist the LogWarning calls via
    // LogAssert.Expect so the EditMode test runner doesn't fail on the
    // unexpected-warning policy.
    public class UICssDiagnosticsTests {
        [SetUp]
        public void Reset() {
            UICssDiagnostics.ResetForTests();
            UICssDiagnostics.Enabled = true;
        }

        [Test]
        public void Unknown_property_set_emits_warning() {
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] CssProperties: unknown property 'colour' skipped"));

            var cs = new ComputedStyle(new Element("div"));
            cs.Set("colour", "red"); // British spelling — typo, not registered.

            Assert.That(
                UICssDiagnostics.HasEmittedForTests("CssProperties", "unknown property 'colour' skipped"),
                Is.True,
                "Setting an unknown (non-custom) property should emit a one-shot diagnostic.");
        }

        [Test]
        public void Custom_property_set_does_not_emit_unknown_property_warning() {
            // `--foo` is a valid CSS Custom Property (a CSS Variable). It must
            // not trip the unknown-property diagnostic; if it does, LogAssert
            // catches the unexpected warning and fails the test.
            int before = UICssDiagnostics.EmittedCountForTests();

            var cs = new ComputedStyle(new Element("div"));
            cs.Set("--brand-color", "#0a84ff");

            Assert.That(UICssDiagnostics.EmittedCountForTests(), Is.EqualTo(before));
        }

        [Test]
        public void Repeated_warning_is_deduplicated() {
            // Single Expect — only one Debug.LogWarning should fire even
            // though Set is invoked three times with the same typo.
            LogAssert.Expect(UnityEngine.LogType.Warning,
                new Regex(@"\[Weva/CSS\] CssProperties: unknown property 'typo-prop' skipped"));

            int before = UICssDiagnostics.EmittedCountForTests();

            var cs = new ComputedStyle(new Element("div"));
            cs.Set("typo-prop", "red");
            cs.Set("typo-prop", "blue");
            var cs2 = new ComputedStyle(new Element("span"));
            cs2.Set("typo-prop", "green");

            int after = UICssDiagnostics.EmittedCountForTests();
            Assert.That(after - before, Is.EqualTo(1),
                "Identical (source, detail) pairs must dedupe to a single emission.");
        }

        [Test]
        public void Disabled_diagnostics_emit_nothing() {
            UICssDiagnostics.Enabled = false;
            int before = UICssDiagnostics.EmittedCountForTests();

            UICssDiagnostics.Warn("Test", "this should be silent");

            Assert.That(UICssDiagnostics.EmittedCountForTests(), Is.EqualTo(before));
        }
    }
}
