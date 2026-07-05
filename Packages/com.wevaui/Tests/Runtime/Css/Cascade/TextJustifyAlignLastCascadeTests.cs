using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Text L3 §7  — `text-justify`
    // CSS Text L3 §7.1 — `text-align-last`
    //
    // Both are INHERITED. Both round-trip through the cascade as
    // string-passthrough values; the layout layer doesn't yet honour them
    // (Weva's inline justifier supports the basic `justify` keyword on
    // `text-align`, but `text-justify` and `text-align-last` are
    // cascade-only in v1 — registered so author CSS round-trips and
    // animation/transition can interpolate-discrete on them).
    //
    // Registration (verified against CssProperties.BuildRegistry):
    //   text-justify    inherited=true  initial="auto"
    //   text-align-last inherited=true  initial="auto"
    //
    // Keyword sets (per spec):
    //   text-justify    : auto | none | inter-word | inter-character
    //   text-align-last : auto | start | end | left | right | center | justify
    public class TextJustifyAlignLastCascadeTests {
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

        // ─── text-justify ──────────────────────────────────────────────────

        [Test]
        public void TextJustify_initial_value_is_auto() {
            // §7: initial = auto.
            var cs = Compute("");
            Assert.That(cs.Get("text-justify"), Is.EqualTo("auto"));
        }

        [Test]
        public void TextJustify_auto_round_trips() {
            var cs = Compute("#x { text-justify: auto; }");
            Assert.That(cs.Get("text-justify"), Is.EqualTo("auto"));
        }

        [Test]
        public void TextJustify_none_round_trips() {
            var cs = Compute("#x { text-justify: none; }");
            Assert.That(cs.Get("text-justify"), Is.EqualTo("none"));
        }

        [Test]
        public void TextJustify_inter_word_round_trips() {
            var cs = Compute("#x { text-justify: inter-word; }");
            Assert.That(cs.Get("text-justify"), Is.EqualTo("inter-word"));
        }

        [Test]
        public void TextJustify_inter_character_round_trips() {
            var cs = Compute("#x { text-justify: inter-character; }");
            Assert.That(cs.Get("text-justify"), Is.EqualTo("inter-character"));
        }

        [Test]
        public void TextJustify_inherits_from_parent() {
            // §7: inherited = yes.
            var child = ComputeChild("#p { text-justify: inter-word; }");
            Assert.That(child.Get("text-justify"), Is.EqualTo("inter-word"));
        }

        [Test]
        public void TextJustify_child_overrides_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-justify: inter-word; } #c { text-justify: none; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-justify"), Is.EqualTo("none"));
        }

        [Test]
        public void TextJustify_important_wins_cascade() {
            var cs = Compute("#x { text-justify: inter-word !important; text-justify: auto; }");
            Assert.That(cs.Get("text-justify"), Is.EqualTo("inter-word"));
        }

        [Test]
        public void TextJustify_initial_keyword_resets_to_auto() {
            // CSS Cascade L5 §7.1.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-justify: inter-word; } #c { text-justify: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-justify"), Is.EqualTo("auto"),
                "`initial` must resolve to spec initial `auto`, not inherit parent");
        }

        [Test]
        public void TextJustify_inherit_keyword_pulls_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-justify: inter-word; } #c { text-justify: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-justify"), Is.EqualTo("inter-word"));
        }

        [Test]
        public void TextJustify_unset_on_inherited_acts_as_inherit() {
            // §7 inherited → `unset` falls through to inherit.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-justify: inter-character; } #c { text-justify: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-justify"), Is.EqualTo("inter-character"));
        }

        // ─── text-align-last ──────────────────────────────────────────────

        [Test]
        public void TextAlignLast_initial_value_is_auto() {
            // §7.1: initial = auto.
            var cs = Compute("");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("auto"));
        }

        [Test]
        public void TextAlignLast_auto_round_trips() {
            var cs = Compute("#x { text-align-last: auto; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("auto"));
        }

        [Test]
        public void TextAlignLast_start_round_trips() {
            var cs = Compute("#x { text-align-last: start; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("start"));
        }

        [Test]
        public void TextAlignLast_end_round_trips() {
            var cs = Compute("#x { text-align-last: end; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("end"));
        }

        [Test]
        public void TextAlignLast_left_round_trips() {
            var cs = Compute("#x { text-align-last: left; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("left"));
        }

        [Test]
        public void TextAlignLast_right_round_trips() {
            var cs = Compute("#x { text-align-last: right; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("right"));
        }

        [Test]
        public void TextAlignLast_center_round_trips() {
            var cs = Compute("#x { text-align-last: center; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("center"));
        }

        [Test]
        public void TextAlignLast_justify_round_trips() {
            var cs = Compute("#x { text-align-last: justify; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("justify"));
        }

        [Test]
        public void TextAlignLast_inherits_from_parent() {
            // §7.1: inherited = yes.
            var child = ComputeChild("#p { text-align-last: justify; }");
            Assert.That(child.Get("text-align-last"), Is.EqualTo("justify"));
        }

        [Test]
        public void TextAlignLast_child_overrides_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-align-last: justify; } #c { text-align-last: start; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-align-last"), Is.EqualTo("start"));
        }

        [Test]
        public void TextAlignLast_important_wins_cascade() {
            var cs = Compute("#x { text-align-last: center !important; text-align-last: auto; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("center"));
        }

        [Test]
        public void TextAlignLast_initial_keyword_resets_to_auto() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-align-last: justify; } #c { text-align-last: initial; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-align-last"), Is.EqualTo("auto"));
        }

        [Test]
        public void TextAlignLast_inherit_keyword_pulls_parent() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-align-last: justify; } #c { text-align-last: inherit; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-align-last"), Is.EqualTo("justify"));
        }

        [Test]
        public void TextAlignLast_unset_on_inherited_acts_as_inherit() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#p { text-align-last: end; } #c { text-align-last: unset; }")
            });
            var child = engine.Compute(doc.GetElementById("c"));
            Assert.That(child.Get("text-align-last"), Is.EqualTo("end"));
        }

        // ─── Cross-property independence ──────────────────────────────────

        [Test]
        public void TextJustify_and_TextAlignLast_are_independent() {
            // Setting one MUST NOT bleed into the other — both share the
            // "text-*" prefix but live in distinct cascade slots.
            var cs = Compute("#x { text-justify: inter-word; }");
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("auto"),
                "text-align-last must remain at initial when only text-justify is set");
            var cs2 = Compute("#x { text-align-last: end; }");
            Assert.That(cs2.Get("text-justify"), Is.EqualTo("auto"),
                "text-justify must remain at initial when only text-align-last is set");
        }

        [Test]
        public void TextAlign_and_TextAlignLast_are_independent() {
            // text-align: center must NOT propagate to text-align-last.
            var cs = Compute("#x { text-align: center; }");
            Assert.That(cs.Get("text-align"), Is.EqualTo("center"));
            Assert.That(cs.Get("text-align-last"), Is.EqualTo("auto"),
                "text-align-last is a separate longhand — text-align changes must not bleed");
        }
    }
}
