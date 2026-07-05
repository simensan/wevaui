using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Writing Modes L4 §5.4 — text-orientation
    // CSS Text Decoration L4 §5  — text-emphasis-style / -color / -position
    //
    // All four properties are INHERITED. The engine carries them as string
    // passthroughs; the tests pin the parse → cascade → Get round-trip per the
    // spec-mandated initial values and keyword sets.
    public class TextOrientationEmphasisCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ── text-orientation ──────────────────────────────────────────────

        [Test]
        public void Text_orientation_initial_value_is_mixed() {
            // Writing Modes L4 §5.4: initial = mixed.
            var cs = Compute("");
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("mixed"));
        }

        [Test]
        public void Text_orientation_upright_round_trips() {
            var cs = Compute("#x { text-orientation: upright; }");
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("upright"));
        }

        [Test]
        public void Text_orientation_sideways_round_trips() {
            var cs = Compute("#x { text-orientation: sideways; }");
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("sideways"));
        }

        [Test]
        public void Text_orientation_is_inherited() {
            // Writing Modes L4 §5.4: text-orientation is inherited.
            var cs = ComputeChild("#p { text-orientation: upright; }");
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("upright"));
        }

        [Test]
        public void Text_orientation_initial_keyword_restores_mixed() {
            var cs = Compute("#x { text-orientation: upright; text-orientation: initial; }");
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("mixed"));
        }

        [Test]
        public void Text_orientation_unset_on_inherited_acts_as_inherit() {
            // unset on an inherited property = inherit.
            var cs = ComputeChild("#p { text-orientation: sideways; } #c { text-orientation: unset; }");
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("sideways"));
        }

        // ── text-emphasis-style ───────────────────────────────────────────

        [Test]
        public void Text_emphasis_style_initial_value_is_none() {
            // Text Decoration L4 §5.1: initial = none.
            var cs = Compute("");
            Assert.That(cs.Get("text-emphasis-style"), Is.EqualTo("none"));
        }

        [Test]
        public void Text_emphasis_style_dot_round_trips() {
            var cs = Compute("#x { text-emphasis-style: dot; }");
            Assert.That(cs.Get("text-emphasis-style"), Is.EqualTo("dot"));
        }

        [Test]
        public void Text_emphasis_style_circle_filled_round_trips() {
            var cs = Compute("#x { text-emphasis-style: filled circle; }");
            Assert.That(cs.Get("text-emphasis-style"), Is.EqualTo("filled circle"));
        }

        [Test]
        public void Text_emphasis_style_open_sesame_round_trips() {
            var cs = Compute("#x { text-emphasis-style: open sesame; }");
            Assert.That(cs.Get("text-emphasis-style"), Is.EqualTo("open sesame"));
        }

        [Test]
        public void Text_emphasis_style_string_form_round_trips() {
            // Text Decoration L4 §5.1: <string> is a valid emphasis mark.
            var cs = Compute("#x { text-emphasis-style: \"x\"; }");
            Assert.That(cs.Get("text-emphasis-style"), Is.EqualTo("\"x\""));
        }

        [Test]
        public void Text_emphasis_style_is_inherited() {
            var cs = ComputeChild("#p { text-emphasis-style: dot; }");
            Assert.That(cs.Get("text-emphasis-style"), Is.EqualTo("dot"));
        }

        [Test]
        public void Text_emphasis_style_initial_keyword_restores_none() {
            var cs = Compute("#x { text-emphasis-style: dot; text-emphasis-style: initial; }");
            Assert.That(cs.Get("text-emphasis-style"), Is.EqualTo("none"));
        }

        // ── text-emphasis-color ───────────────────────────────────────────

        [Test]
        public void Text_emphasis_color_initial_value_is_currentcolor() {
            // Text Decoration L4 §5.2: initial = currentcolor.
            var cs = Compute("");
            Assert.That(cs.Get("text-emphasis-color"), Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Text_emphasis_color_named_color_round_trips() {
            var cs = Compute("#x { text-emphasis-color: red; }");
            Assert.That(cs.Get("text-emphasis-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Text_emphasis_color_hex_round_trips() {
            var cs = Compute("#x { text-emphasis-color: #123456; }");
            Assert.That(cs.Get("text-emphasis-color"), Is.EqualTo("#123456"));
        }

        [Test]
        public void Text_emphasis_color_is_inherited() {
            var cs = ComputeChild("#p { text-emphasis-color: blue; }");
            Assert.That(cs.Get("text-emphasis-color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Text_emphasis_color_initial_keyword_restores_currentcolor() {
            var cs = Compute("#x { text-emphasis-color: red; text-emphasis-color: initial; }");
            Assert.That(cs.Get("text-emphasis-color"), Is.EqualTo("currentcolor"));
        }

        // ── text-emphasis-position ────────────────────────────────────────

        [Test]
        public void Text_emphasis_position_initial_value_is_over_right() {
            // Text Decoration L4 §5.3: initial = over right.
            var cs = Compute("");
            Assert.That(cs.Get("text-emphasis-position"), Is.EqualTo("over right"));
        }

        [Test]
        public void Text_emphasis_position_under_left_round_trips() {
            var cs = Compute("#x { text-emphasis-position: under left; }");
            Assert.That(cs.Get("text-emphasis-position"), Is.EqualTo("under left"));
        }

        [Test]
        public void Text_emphasis_position_over_left_round_trips() {
            var cs = Compute("#x { text-emphasis-position: over left; }");
            Assert.That(cs.Get("text-emphasis-position"), Is.EqualTo("over left"));
        }

        [Test]
        public void Text_emphasis_position_under_right_round_trips() {
            var cs = Compute("#x { text-emphasis-position: under right; }");
            Assert.That(cs.Get("text-emphasis-position"), Is.EqualTo("under right"));
        }

        [Test]
        public void Text_emphasis_position_is_inherited() {
            var cs = ComputeChild("#p { text-emphasis-position: under left; }");
            Assert.That(cs.Get("text-emphasis-position"), Is.EqualTo("under left"));
        }

        [Test]
        public void Text_emphasis_position_initial_keyword_restores_over_right() {
            var cs = Compute("#x { text-emphasis-position: under left; text-emphasis-position: initial; }");
            Assert.That(cs.Get("text-emphasis-position"), Is.EqualTo("over right"));
        }

        // ── Cross-property independence ───────────────────────────────────

        [Test]
        public void Text_emphasis_longhands_are_independent() {
            // Setting one emphasis longhand does not bleed into the others.
            var cs = Compute("#x { text-emphasis-style: dot; }");
            Assert.That(cs.Get("text-emphasis-color"), Is.EqualTo("currentcolor"),
                "text-emphasis-color must remain at initial when only style is set");
            Assert.That(cs.Get("text-emphasis-position"), Is.EqualTo("over right"),
                "text-emphasis-position must remain at initial when only style is set");
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Text_orientation_id_beats_element_selector() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { text-orientation: sideways; } #x { text-orientation: upright; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("text-orientation"), Is.EqualTo("upright"));
        }
    }
}
