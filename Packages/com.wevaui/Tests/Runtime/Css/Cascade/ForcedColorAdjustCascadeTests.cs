using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Color Adjustment L1 §10 — `forced-color-adjust` cascade coverage.
    //
    // `forced-color-adjust` controls whether the UA may override an element's
    // colors in high-contrast / forced-colors mode. Values are `auto` and `none`.
    //
    // v1 status: `forced-color-adjust` is NOT a registered CSS property in the
    // engine. Declarations spill to the ComputedStyle side dictionary (customProps)
    // and produce an "unknown property" diagnostic via UICssDiagnostics.Warn.
    // The value is still retrievable via `ComputedStyle.Get("forced-color-adjust")`.
    // Rendering ignores it (forced-colors OS integration is not implemented).
    //
    // These tests pin the current behavior and serve as a readiness baseline:
    // when the property is eventually registered as a first-class property,
    // the `[Ignore]` markers on the registration tests should be removed and
    // the "stored-in-side-dict" tests will still pass (Get works on both paths).
    //
    // Spec: CSS Color Adjustment L1 §10
    //       https://www.w3.org/TR/css-color-adjust-1/#forced-color-adjust-prop
    public class ForcedColorAdjustCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // The diagnostics side-channel logs "unknown property" on Set for
        // unregistered properties. The headless harness has no Debug.LogWarning
        // (the #if UNITY_EDITOR guard is false) so no NUnit noise is produced.
        // We still reset the dedupe set so tests can observe fresh warning state.
        [SetUp]
        public void ResetDiagnostics() {
            UICssDiagnostics.ResetForTests();
        }

        // ---- Registration status (documents the gap) ----

        [Test]
        public void Forced_color_adjust_is_registered_as_known_property() {
            // CSS Color Adjustment L1 §10: `forced-color-adjust` should be a
            // first-class registered property. Un-ignore this test when the
            // property is added to CssProperties.cs.
            Assert.That(CssProperties.TryGet("forced-color-adjust", out _), Is.True,
                "forced-color-adjust must be a registered CSS property");
        }

        // ---- Current behavior: spills to side dictionary ----

        [Test]
        public void Forced_color_adjust_auto_is_retrievable_via_get() {
            // Even though forced-color-adjust is unregistered, the cascade
            // engine stores the authored value in the ComputedStyle side dict.
            // ComputedStyle.Get(string) checks both the main array and customProps.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { forced-color-adjust: auto; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("forced-color-adjust"), Is.EqualTo("auto"),
                "authored `forced-color-adjust: auto` must be retrievable via ComputedStyle.Get");
        }

        [Test]
        public void Forced_color_adjust_none_is_retrievable_via_get() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { forced-color-adjust: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("forced-color-adjust"), Is.EqualTo("none"),
                "authored `forced-color-adjust: none` must be retrievable via ComputedStyle.Get");
        }

        [Test]
        public void Forced_color_adjust_does_not_emit_unknown_property_diagnostic() {
            // After FCOLOR-1 was closed (property registered in CssProperties),
            // the cascade must NOT emit the "unknown property" diagnostic for
            // authored forced-color-adjust declarations.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { forced-color-adjust: auto; }")
            });
            engine.Compute(doc.GetElementById("x"));
            Assert.That(
                UICssDiagnostics.HasEmittedForTests("CssProperties", "unknown property 'forced-color-adjust' skipped"),
                Is.False,
                "registered property must not emit the unknown-property diagnostic");
        }

        [Test]
        public void Forced_color_adjust_does_not_crash_cascade() {
            // The cascade must tolerate an unregistered property without throwing.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { forced-color-adjust: none; color: red; }")
            });
            ComputedStyle cs = null;
            Assert.DoesNotThrow(() => cs = engine.Compute(doc.GetElementById("x")),
                "forced-color-adjust must not cause the cascade to throw");
            Assert.That(cs, Is.Not.Null);
        }

        [Test]
        public void Forced_color_adjust_does_not_affect_adjacent_properties() {
            // The side-dict spill must not corrupt other registered properties.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { forced-color-adjust: none; color: blue; background-color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"),
                "color must cascade independently of forced-color-adjust");
            Assert.That(cs.Get("background-color"), Is.EqualTo("red"),
                "background-color must cascade independently of forced-color-adjust");
        }

        [Test]
        public void Forced_color_adjust_source_order_tiebreak_last_wins() {
            // When two same-specificity rules declare forced-color-adjust,
            // the later source-order declaration wins (same as any other property).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { forced-color-adjust: auto; } #x { forced-color-adjust: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("forced-color-adjust"), Is.EqualTo("none"),
                "later same-specificity declaration must win for unregistered properties");
        }

        [Test]
        public void Forced_color_adjust_higher_specificity_wins() {
            // ID selector beats tag selector even for unregistered properties.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { forced-color-adjust: auto; } #x { forced-color-adjust: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("forced-color-adjust"), Is.EqualTo("none"),
                "higher-specificity #x rule must win over the div rule");
        }

        [Test]
        public void Forced_color_adjust_absent_returns_initial_auto() {
            // After FCOLOR-1 was closed (property registered with initial `auto`
            // in CssProperties), Get returns the initial value `auto` when no
            // rule sets it (the cascade fills the initial slot during compute).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("forced-color-adjust"), Is.EqualTo("auto"),
                "registered property must return its initial value when no rule declares it");
        }
    }
}
