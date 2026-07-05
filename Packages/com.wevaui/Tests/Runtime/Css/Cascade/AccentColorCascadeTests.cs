using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Basic User Interface 4 §5.5 — `accent-color`.
    //
    // `accent-color` tints UA-drawn form-control accents (checkbox tick,
    // radio dot, slider thumb, etc.). It is INHERITED so authors can theme
    // an entire form region by setting it on a wrapping element.
    //
    // Initial = `auto` (UA picks the accent; InputRenderer falls back to
    // its hard-coded default when the cascaded value is `auto` or unset).
    //
    // v1 note: the property is a cascade round-trip in Weva. InputRenderer
    // reads the string from the computed style when painting a form control.
    // There is no colour-parsing pipeline in the cascade layer itself —
    // `#hex`, named colours, `rgb()`, and `auto` all pass through verbatim.
    public class AccentColorCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── Initial value ─────────────────────────────────────────────────

        [Test]
        public void Accent_color_initial_value_is_auto() {
            // CSS UI4 §5.5 — initial value = `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("auto"));
        }

        // ── Keyword values ────────────────────────────────────────────────

        [Test]
        public void Accent_color_auto_explicit_round_trips() {
            var cs = Compute("#x { accent-color: auto; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("auto"));
        }

        // ── Color values ──────────────────────────────────────────────────

        [Test]
        public void Accent_color_named_color_round_trips() {
            var cs = Compute("#x { accent-color: rebeccapurple; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("rebeccapurple"));
        }

        [Test]
        public void Accent_color_hex_color_round_trips() {
            var cs = Compute("#x { accent-color: #b388ff; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("#b388ff"));
        }

        [Test]
        public void Accent_color_rgb_function_round_trips() {
            var cs = Compute("#x { accent-color: rgb(100, 149, 237); }");
            var val = cs.Get("accent-color");
            Assert.That(val, Does.Contain("100").Or.StartWith("rgb("),
                "rgb() value should survive cascade");
        }

        [Test]
        public void Accent_color_currentcolor_keyword_round_trips() {
            // `currentcolor` is a valid <color> value per CSS Color L4.
            var cs = Compute("#x { accent-color: currentcolor; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("currentcolor"));
        }

        // ── Inheritance ───────────────────────────────────────────────────

        [Test]
        public void Accent_color_inherits_from_parent() {
            // CSS UI4 §5.5: Inherited: yes. Setting on a form wrapper propagates
            // to all descendant form controls automatically.
            var cs = ComputeChild("#parent { accent-color: coral; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("coral"),
                "accent-color must be inherited by descendants");
        }

        [Test]
        public void Accent_color_child_overrides_inherited_value() {
            // Child-level rule beats inherited parent value (normal cascade).
            var cs = ComputeChild(
                "#parent { accent-color: coral; } " +
                "#child  { accent-color: teal; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("teal"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Accent_color_initial_keyword_restores_auto() {
            var cs = Compute("#x { accent-color: initial; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("auto"));
        }

        [Test]
        public void Accent_color_inherit_keyword_on_child_propagates_parent() {
            // Explicit `inherit` forces propagation even when the UA stylesheet
            // would normally pick a non-inherited default.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { accent-color: #003300; } " +
                       "#child  { accent-color: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("accent-color"), Is.EqualTo("#003300"));
        }

        [Test]
        public void Accent_color_unset_on_inherited_property_resolves_as_inherit() {
            // For an inherited property, `unset` acts like `inherit`.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { accent-color: goldenrod; } " +
                       "#child  { accent-color: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("accent-color"), Is.EqualTo("goldenrod"));
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Accent_color_id_selector_beats_class_selector() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".x { accent-color: red; } #x { accent-color: blue; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("accent-color"), Is.EqualTo("blue"));
        }
    }
}
