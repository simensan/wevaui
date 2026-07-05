using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Text L3 §6.2 — `hyphens`.
    //
    // `hyphens` controls whether words may be broken and hyphenated at
    // soft-wrap opportunities.
    //
    // Values: none | manual | auto
    // Inherited: yes
    // Initial: manual
    //
    // Weva registers `hyphens` as an inherited cascade round-trip property.
    // The text layout engine reads the cascaded value; actual soft-hyphen
    // insertion is tracked separately.
    public class HyphensCascadeTests {
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
        public void Hyphens_initial_value_is_manual() {
            // CSS Text L3 §6.2: initial = `manual`.
            var cs = Compute("");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("manual"));
        }

        // ── Keyword values ────────────────────────────────────────────────

        [Test]
        public void Hyphens_none_round_trips() {
            // `none` disables all word-break hyphenation.
            var cs = Compute("#x { hyphens: none; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"));
        }

        [Test]
        public void Hyphens_manual_explicit_round_trips() {
            // `manual` = only break at U+00AD SOFT HYPHEN.
            var cs = Compute("#x { hyphens: manual; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("manual"));
        }

        [Test]
        public void Hyphens_auto_round_trips() {
            // `auto` = UA may insert hyphens at language-dependent positions.
            var cs = Compute("#x { hyphens: auto; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("auto"));
        }

        // ── Inheritance ───────────────────────────────────────────────────

        [Test]
        public void Hyphens_inherits_from_parent() {
            // Inherited: yes — child picks up parent value automatically.
            var cs = ComputeChild("#parent { hyphens: none; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"),
                "hyphens must be inherited per CSS Text L3 §6.2");
        }

        [Test]
        public void Hyphens_auto_inherits_to_child() {
            var cs = ComputeChild("#parent { hyphens: auto; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("auto"));
        }

        [Test]
        public void Hyphens_child_overrides_inherited_value() {
            var cs = ComputeChild(
                "#parent { hyphens: auto; } " +
                "#child  { hyphens: none; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"));
        }

        // ── CSS-wide keywords ─────────────────────────────────────────────

        [Test]
        public void Hyphens_initial_keyword_restores_manual() {
            var cs = Compute("#x { hyphens: initial; }");
            Assert.That(cs.Get("hyphens"), Is.EqualTo("manual"));
        }

        [Test]
        public void Hyphens_inherit_keyword_propagates_parent_value() {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { hyphens: auto; } #child { hyphens: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("hyphens"), Is.EqualTo("auto"));
        }

        [Test]
        public void Hyphens_unset_on_inherited_property_acts_as_inherit() {
            // `unset` on an inherited property = `inherit`.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { hyphens: none; } #child { hyphens: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"));
        }

        // ── Specificity ───────────────────────────────────────────────────

        [Test]
        public void Hyphens_id_beats_element_selector() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { hyphens: auto; } #x { hyphens: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("hyphens"), Is.EqualTo("none"));
        }

        [Test]
        public void Hyphens_later_rule_beats_earlier_at_equal_specificity() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { hyphens: none; } #x { hyphens: auto; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("hyphens"), Is.EqualTo("auto"));
        }
    }
}
